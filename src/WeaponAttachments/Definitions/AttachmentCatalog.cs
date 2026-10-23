using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PreLuna.WeaponAttachments.Definitions;

public sealed class CatalogEntry
{
    public AttachmentDefinition Definition { get; }
    public bool IsOriginal { get; }
    public bool IsSystemOption { get; }

    internal CatalogEntry(AttachmentDefinition definition, bool original, bool systemOption = false)
    {
        Definition = Freeze(definition);
        IsOriginal = original;
        IsSystemOption = systemOption;
    }

    private static AttachmentDefinition Freeze(AttachmentDefinition value)
    {
        var applicability = value.Applicability!;
        return value with
        {
            Applicability = applicability with
            {
                Targets = Array.AsReadOnly(applicability.Targets.ToArray()),
                Weapons = Array.AsReadOnly(applicability.Weapons.ToArray()),
                ExcludedWeapons = Array.AsReadOnly(applicability.ExcludedWeapons.ToArray()),
                RequiredWeaponCapabilities = Array.AsReadOnly(applicability.RequiredWeaponCapabilities.ToArray())
            },
            Modifiers = Array.AsReadOnly(value.Modifiers.Select(item => item with { Condition = FreezeCondition(item.Condition) }).ToArray()),
            Mechanisms = Array.AsReadOnly(value.Mechanisms.Select(item => item with
                { Parameters = FreezeParameters(item.Parameters), Condition = FreezeCondition(item.Condition) }).ToArray())
        };
    }

    private static ConditionDefinition? FreezeCondition(ConditionDefinition? value) => value == null ? null :
        value with { Parameters = FreezeParameters(value.Parameters) };

    private static IReadOnlyDictionary<string, double> FreezeParameters(IReadOnlyDictionary<string, double> value) =>
        new ReadOnlyDictionary<string, double>(value.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

public sealed record AvailabilityResult(bool Applicable, bool RuntimeReady, string Code, IReadOnlyList<string> MissingCapabilities);

public sealed class AttachmentCatalog
{
    private readonly IReadOnlyDictionary<string, CatalogEntry> _byId;
    private readonly IReadOnlyList<CatalogEntry> _entries;
    private IReadOnlyDictionary<string, CatalogEntry> _system = new Dictionary<string, CatalogEntry>();
    public string CatalogId { get; }
    public IReadOnlyList<CatalogEntry> Entries => Array.AsReadOnly(_entries.Concat(_system.Values).ToArray());
    public int OriginalCount => Entries.Count(item => item.IsOriginal);
    public int CustomCount => _entries.Count - OriginalCount;
    public int SystemOptionCount => _system.Count;

    internal AttachmentCatalog(CatalogDocument document)
    {
        CatalogId = document.CatalogId;
        var entries = SlotCatalog.All.Select(slot => new CatalogEntry(new AttachmentDefinition
        {
            Id = SlotCatalog.OriginalId(slot.Slot), DisplayName = "原装" + slot.DisplayName, Slot = slot.Slot,
            Applicability = new ApplicabilityDefinition
            {
                Targets = new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All
            }
        }, true)).ToList();
        entries.AddRange(document.Attachments.Select(definition => new CatalogEntry(definition, false)));
        _entries = entries.AsReadOnly();
        _byId = new ReadOnlyDictionary<string, CatalogEntry>(entries.ToDictionary(item => item.Definition.Id, StringComparer.Ordinal));
    }

    public CatalogEntry? Find(string id) => _byId.TryGetValue(id, out var entry) ? entry : _system.GetValueOrDefault(id);
    internal void SetSystemFlashlights(string dataPackId, IEnumerable<(uint Id, string Name)> parts)
    {
        var values = parts.Select(part => new CatalogEntry(new AttachmentDefinition
        {
            Id = "system.flashlight.part_" + part.Id, DisplayName = part.Name, Slot = AttachmentSlot.Flashlight,
            Applicability = new() { Targets = new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All },
            Flashlight = new() { DataPackId = dataPackId, SourcePartId = part.Id }
        }, false, true)).ToDictionary(entry => entry.Definition.Id, StringComparer.Ordinal);
        _system = new ReadOnlyDictionary<string, CatalogEntry>(values);
    }
    public IReadOnlyList<CatalogEntry> ForSlot(AttachmentSlot slot) => Array.AsReadOnly(Entries.Where(item => item.Definition.Slot == slot).ToArray());

    public AvailabilityResult CheckAvailability(string id, WeaponIdentity weapon, ConfigurationTarget target,
        IReadOnlyCollection<string>? weaponCapabilities = null, IReadOnlyCollection<string>? implementedCapabilities = null)
    {
        var entry = Find(id);
        if (entry == null) return Unavailable("UNKNOWN_ATTACHMENT");
        var definition = entry.Definition;
        var rules = definition.Applicability!;
        if (!rules.Targets.Contains(target.Kind)) return Unavailable("WRONG_TARGET");
        bool Matches(WeaponReference reference) => reference.ToIdentity().Equals(weapon);
        if (rules.ExcludedWeapons.Any(Matches) || (rules.WeaponScope == WeaponScope.ListedOnly && !rules.Weapons.Any(Matches)))
            return Unavailable("WRONG_WEAPON");
        var missingWeaponCapabilities = rules.RequiredWeaponCapabilities.Where(item => weaponCapabilities?.Contains(item) != true).ToArray();
        if (missingWeaponCapabilities.Length > 0) return new(false, false, "MISSING_WEAPON_CAPABILITY", Array.AsReadOnly(missingWeaponCapabilities));
        if (entry.IsOriginal) return new(true, true, "ORIGINAL", Array.Empty<string>());
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modifier in definition.Modifiers)
        {
            required.Add("stat." + modifier.Stat);
            if (modifier.Condition != null) required.Add("condition." + modifier.Condition.Id);
        }
        foreach (var mechanism in definition.Mechanisms)
        {
            required.Add("mechanism." + mechanism.Capability);
            if (mechanism.Condition != null) required.Add("condition." + mechanism.Condition.Id);
        }
        if (definition.Flashlight != null) required.Add("flashlight.original_profile");
        var missing = required.Where(item => implementedCapabilities?.Contains(item) != true).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        return new(true, missing.Length == 0, missing.Length == 0 ? "AVAILABLE" : "RUNTIME_NOT_IMPLEMENTED", Array.AsReadOnly(missing));
    }

    private static AvailabilityResult Unavailable(string code) => new(false, false, code, Array.Empty<string>());
}
