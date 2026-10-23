using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;

namespace PreLuna.WeaponAttachments.Modifiers;

public enum StatValueForm { Direct, DurationSeconds }
public enum ConditionActivity { Unknown, Inactive, Active }
public sealed record StatInput(string Stat, double UpstreamValue, double? BaseValue = null, StatValueForm Form = StatValueForm.Direct);
public sealed record UpstreamReadRequest(ConfigurationTarget Target, WeaponIdentity Weapon, string InstanceId, string ExcludedLayerId);

// The source must capture a fresh upstream result before this MOD's layer, never its previous output.
public interface IUpstreamStatSource
{
    UpstreamSnapshot Read(UpstreamReadRequest request);
}

public sealed class UpstreamSnapshot
{
    public ConfigurationTarget Target { get; }
    public WeaponIdentity Weapon { get; }
    public string InstanceId { get; }
    public string SourceId { get; }
    public long Version { get; }
    public IReadOnlyDictionary<string, StatInput> Values { get; }
    public IReadOnlyCollection<string> AppliedLayerIds { get; }
    public IReadOnlyCollection<string> WeaponCapabilities { get; }

    public UpstreamSnapshot(ConfigurationTarget target, WeaponIdentity weapon, string instanceId, string sourceId,
        long version, IEnumerable<StatInput> values, IEnumerable<string>? appliedLayerIds = null,
        IEnumerable<string>? weaponCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(weapon);
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(instanceId) || !DefinitionIds.IsValid(sourceId) || version < 0)
            throw new ArgumentException("上游快照需要实例身份、稳定来源及非负版本号。");
        var inputs = values.ToArray();
        if (inputs.Any(item => item == null || !DefinitionIds.IsValid(item.Stat)))
            throw new ArgumentException("上游属性需要稳定标识。");
        Target = target; Weapon = weapon; InstanceId = instanceId; SourceId = sourceId; Version = version;
        Values = new ReadOnlyDictionary<string, StatInput>(inputs.ToDictionary(item => item.Stat, StringComparer.Ordinal));
        AppliedLayerIds = Copy(appliedLayerIds);
        WeaponCapabilities = Copy(weaponCapabilities);
    }

    private static IReadOnlyCollection<string> Copy(IEnumerable<string>? values)
    {
        var copy = (values ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (copy.Any(item => !DefinitionIds.IsValid(item))) throw new ArgumentException("能力及属性层需要稳定标识。");
        return Array.AsReadOnly(copy);
    }
}

public sealed class ConditionSnapshot
{
    private readonly IReadOnlyDictionary<string, bool> _states;
    public static ConditionSnapshot Unknown { get; } = new(new Dictionary<string, bool>());
    public ConditionSnapshot(IReadOnlyDictionary<string, bool> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _states = new ReadOnlyDictionary<string, bool>(states.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
    public ConditionActivity Evaluate(ConditionDefinition? condition)
    {
        if (condition == null || condition.Id == "always") return ConditionActivity.Active;
        return _states.TryGetValue(Key(condition), out bool active) ?
            active ? ConditionActivity.Active : ConditionActivity.Inactive : ConditionActivity.Unknown;
    }
    public static string Key(ConditionDefinition? condition) => condition == null || condition.Id == "always" ? "permanent" :
        condition.Id + "(" + string.Join(",", condition.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value.ToString("R", CultureInfo.InvariantCulture))) + ")";
}

public sealed record CalculationIssue(IssueSeverity Severity, string Code, string Subject, string Message);
public sealed record ModifierContribution(AttachmentSlot Slot, string AttachmentId, int ModifierIndex,
    string Stat, double Percent, string LayerId, ConditionActivity Activity);
public sealed record PercentageLayer(string Id, ConditionActivity Activity, double? RequestedPercent,
    double AppliedPercent, double Factor, IReadOnlyList<ModifierContribution> Contributions);
public sealed record CalculatedStat(string Stat, string Unit, StatValueForm Form, double? BaseValue,
    double? UpstreamValue, double? Value, double OwnMultiplier, bool CanCalculate, bool Changed,
    IReadOnlyList<PercentageLayer> Layers, IReadOnlyList<CalculationIssue> Issues);
public sealed record DeferredEffect(AttachmentSlot Slot, string AttachmentId, string Capability);

public sealed class CalculationResult
{
    public ConfigurationTarget Target { get; }
    public WeaponIdentity Weapon { get; }
    public string InstanceId { get; }
    public string SourceId { get; }
    public long UpstreamVersion { get; }
    public string OutputLayerId => ModifierCalculator.LayerId;
    public IReadOnlyList<CalculatedStat> Stats { get; }
    public IReadOnlyList<CalculationIssue> Issues { get; }
    public IReadOnlyList<DeferredEffect> DeferredEffects { get; }
    public bool Success => !Issues.Any(item => item.Severity == IssueSeverity.Error) && Stats.All(item => item.CanCalculate);
    public bool ConditionsFullyKnown => !Stats.SelectMany(item => item.Layers).Any(item => item.Activity == ConditionActivity.Unknown);
    public bool GameplayApplied => false;
    internal CalculationResult(UpstreamSnapshot snapshot, IEnumerable<CalculatedStat> stats,
        IEnumerable<CalculationIssue> issues, IEnumerable<DeferredEffect> deferred)
    {
        Target = snapshot.Target; Weapon = snapshot.Weapon; InstanceId = snapshot.InstanceId; SourceId = snapshot.SourceId;
        UpstreamVersion = snapshot.Version; Stats = Array.AsReadOnly(stats.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray()); DeferredEffects = Array.AsReadOnly(deferred.ToArray());
    }
}

public sealed record PreviewRow(string Stat, string Unit, StatValueForm Form, double? BaseValue, double? UpstreamValue,
    double? CurrentValue, double? PreviewValue, bool CanCalculate);
public sealed record LoadoutPreview(ConfigurationTarget Target, WeaponIdentity Weapon, string InstanceId,
    CalculationResult? Current, CalculationResult? Draft, IReadOnlyList<PreviewRow> Rows,
    IReadOnlyList<CalculationIssue> Issues)
{
    public bool Success => Current?.Success == true && Draft?.Success == true &&
        !Issues.Any(item => item.Severity == IssueSeverity.Error);
    public bool GameplayApplied => false;
}
