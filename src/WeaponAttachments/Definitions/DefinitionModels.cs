using System;
using System.Collections.Generic;

namespace PreLuna.WeaponAttachments.Definitions;

public enum AttachmentSlot { Unknown, Muzzle, Barrel, Handguard, FrontGrip, RearGrip, Stock, Magazine, Ammunition, Perk, Optic, Flashlight }
public enum ConfigurationTargetKind { Unknown, LocalPlayer, HostBot }
public enum WeaponScope { Unknown, All, ListedOnly }
public enum PercentOperation { Unknown, IncreasePercent, DecreasePercent }
public enum IssueSeverity { Warning, Error }
public enum ParameterUnit { Percent, Count, Seconds }

public sealed record CatalogDocument
{
    public int SchemaVersion { get; init; }
    public string CatalogId { get; init; } = "";
    public IReadOnlyList<AttachmentDefinition> Attachments { get; init; } = Array.Empty<AttachmentDefinition>();

    public static CatalogDocument Empty() => new() { SchemaVersion = 1, CatalogId = "preluna.weaponattachments" };
}

public sealed record AttachmentDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public AttachmentSlot Slot { get; init; }
    public ApplicabilityDefinition? Applicability { get; init; }
    public IReadOnlyList<StatModifierDefinition> Modifiers { get; init; } = Array.Empty<StatModifierDefinition>();
    public IReadOnlyList<MechanismDefinition> Mechanisms { get; init; } = Array.Empty<MechanismDefinition>();
    public FlashlightReference? Flashlight { get; init; }
    public VisualBindingDefinition VisualBinding { get; init; } = new();
}

public sealed record ApplicabilityDefinition
{
    public IReadOnlyList<ConfigurationTargetKind> Targets { get; init; } = Array.Empty<ConfigurationTargetKind>();
    public WeaponScope WeaponScope { get; init; }
    public IReadOnlyList<WeaponReference> Weapons { get; init; } = Array.Empty<WeaponReference>();
    public IReadOnlyList<WeaponReference> ExcludedWeapons { get; init; } = Array.Empty<WeaponReference>();
    public IReadOnlyList<string> RequiredWeaponCapabilities { get; init; } = Array.Empty<string>();
}

public sealed record WeaponReference
{
    public string DataPackId { get; init; } = "";
    public uint? OfflineGearId { get; init; }
    public string? VariantKey { get; init; }
    public uint? ArchetypeId { get; init; }

    public WeaponIdentity ToIdentity() => new(DataPackId, OfflineGearId, VariantKey, ArchetypeId);
}

public sealed record StatModifierDefinition
{
    public string Stat { get; init; } = "";
    public PercentOperation Operation { get; init; }
    public double? Percent { get; init; }
    public ConditionDefinition? Condition { get; init; }
}

public sealed record MechanismDefinition
{
    public string Capability { get; init; } = "";
    public IReadOnlyDictionary<string, double> Parameters { get; init; } = new Dictionary<string, double>();
    public ConditionDefinition? Condition { get; init; }
}

public sealed record ConditionDefinition
{
    public string Id { get; init; } = "";
    public IReadOnlyDictionary<string, double> Parameters { get; init; } = new Dictionary<string, double>();
}

public sealed record FlashlightReference
{
    public string DataPackId { get; init; } = "";
    public uint? SourcePartId { get; init; }
    public uint? SettingsId { get; init; }
    public WeaponReference? SourceWeapon { get; init; }
}

public sealed record VisualBindingDefinition
{
    public bool Enabled { get; init; }
    public string? BindingId { get; init; }
    public string? MountPointId { get; init; }
    public string? FirstPersonAsset { get; init; }
    public string? ThirdPersonAsset { get; init; }
}

public sealed record DefinitionIssue(IssueSeverity Severity, string Code, string Path, string Message);

public sealed class CatalogReadResult
{
    public AttachmentCatalog? Catalog { get; }
    public IReadOnlyList<DefinitionIssue> Issues { get; }
    public bool Success => Catalog != null;

    internal CatalogReadResult(AttachmentCatalog? catalog, IReadOnlyList<DefinitionIssue> issues)
    {
        Catalog = catalog;
        Issues = issues;
    }
}
