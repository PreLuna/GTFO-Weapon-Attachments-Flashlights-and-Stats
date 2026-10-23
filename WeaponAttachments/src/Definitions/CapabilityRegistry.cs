using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PreLuna.WeaponAttachments.Definitions;

public enum StatCalculationKind { Scalar, Capacity, Speed }
public sealed record StatDescriptor(string Id, PercentOperation BeneficialOperation, string BaseUnit,
    bool RequiresNonZeroBaseline = true, StatCalculationKind CalculationKind = StatCalculationKind.Scalar);
public sealed record ParameterRule(string Name, ParameterUnit Unit, double Minimum, double? Maximum = null, bool Required = true, bool WholeNumber = false);
public sealed record ParameterCapability(string Id, IReadOnlyList<ParameterRule> Parameters);

public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, StatDescriptor> _stats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterCapability> _mechanisms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterCapability> _conditions = new(StringComparer.Ordinal);
    private bool _sealed;

    public IReadOnlyDictionary<string, StatDescriptor> Stats => new ReadOnlyDictionary<string, StatDescriptor>(_stats);
    public IReadOnlyDictionary<string, ParameterCapability> Mechanisms => new ReadOnlyDictionary<string, ParameterCapability>(_mechanisms);
    public IReadOnlyDictionary<string, ParameterCapability> Conditions => new ReadOnlyDictionary<string, ParameterCapability>(_conditions);

    public void RegisterStat(StatDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        CheckRegistration(descriptor.Id);
        if (descriptor.BeneficialOperation is not (PercentOperation.IncreasePercent or PercentOperation.DecreasePercent))
            throw new ArgumentException("属性必须明确加成方向。");
        if (string.IsNullOrWhiteSpace(descriptor.BaseUnit)) throw new ArgumentException("属性需要注明基础单位。");
        if (!Enum.IsDefined(typeof(StatCalculationKind), descriptor.CalculationKind) ||
            (descriptor.CalculationKind != StatCalculationKind.Scalar && descriptor.BeneficialOperation != PercentOperation.IncreasePercent))
            throw new ArgumentException("容量和速度类型必须使用增加方向；计算类型必须已知。");
        if (!_stats.TryAdd(descriptor.Id, descriptor)) throw new ArgumentException("重复属性：" + descriptor.Id);
    }

    public void RegisterMechanism(ParameterCapability descriptor) => Register(_mechanisms, descriptor);
    public void RegisterCondition(ParameterCapability descriptor) => Register(_conditions, descriptor);
    public void Seal() => _sealed = true;

    private void CheckRegistration(string id)
    {
        if (_sealed) throw new InvalidOperationException("注册表已封存，需在构造目录读取器前注册扩展。");
        if (!DefinitionIds.IsValid(id)) throw new ArgumentException("无效能力标识：" + id);
    }

    private void Register(Dictionary<string, ParameterCapability> table, ParameterCapability descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        CheckRegistration(descriptor.Id);
        if (descriptor.Parameters == null) throw new ArgumentException("参数规则不能为空。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in descriptor.Parameters)
        {
            if (rule == null || !DefinitionIds.IsValid(rule.Name) || !names.Add(rule.Name) || !Enum.IsDefined(typeof(ParameterUnit), rule.Unit) ||
                !double.IsFinite(rule.Minimum) || (rule.Maximum.HasValue && (!double.IsFinite(rule.Maximum.Value) || rule.Maximum < rule.Minimum)))
                throw new ArgumentException("能力参数规则无效。");
        }
        var copy = descriptor with { Parameters = Array.AsReadOnly(descriptor.Parameters.ToArray()) };
        if (!table.TryAdd(copy.Id, copy)) throw new ArgumentException("重复能力：" + copy.Id);
    }

    public static CapabilityRegistry CreateDefaults()
    {
        var result = new CapabilityRegistry();
        foreach (var (id, unit) in new[]
        {
            ("damage", "damage"), ("weakspot_multiplier", "multiplier"), ("stagger_multiplier", "multiplier"),
            ("falloff_start", "meters"), ("falloff_end", "meters"), ("max_range", "meters"),
            ("fire_rate", "rounds_per_minute"), ("reload_speed", "speed_multiplier"),
            ("magazine_capacity", "rounds"), ("reserve_capacity", "rounds"), ("ammo_refill", "resource_units"),
            ("hit_radius", "meters"), ("charge_speed", "speed_multiplier"), ("backstab_multiplier", "multiplier")
        }) result.RegisterStat(new(id, PercentOperation.IncreasePercent, unit, CalculationKind:
            id is "magazine_capacity" or "reserve_capacity" ? StatCalculationKind.Capacity :
            id is "reload_speed" or "charge_speed" or "fire_rate" ? StatCalculationKind.Speed : StatCalculationKind.Scalar));
        foreach (var id in new[] { "hip_spread", "aim_spread", "recoil", "alert_buildup" })
            result.RegisterStat(new(id, PercentOperation.DecreasePercent, "relative"));
        foreach (var id in new[] { "wake_up_radius", "alert_radius" })
            result.RegisterStat(new(id, PercentOperation.DecreasePercent, "meters"));

        result.RegisterMechanism(new("armor_pierce", new[] { new ParameterRule("ignored_armor_percent", ParameterUnit.Percent, 0, 100) }));
        foreach (var id in new[] { "ammo_return_to_clip", "ammo_return_to_reserve" })
            result.RegisterMechanism(new(id, new[]
            {
                new ParameterRule("rounds", ParameterUnit.Count, 1, int.MaxValue, WholeNumber: true),
                new ParameterRule("chance_percent", ParameterUnit.Percent, 0, 100, Required: false)
            }));
        foreach (var id in new[] { "always", "crouched", "aiming" })
            result.RegisterCondition(new(id, Array.Empty<ParameterRule>()));
        foreach (var id in new[] { "consecutive_hits", "weakpoint_hits", "kills", "shots_fired" })
            result.RegisterCondition(new(id, new[]
            {
                new ParameterRule("count", ParameterUnit.Count, 1, int.MaxValue, WholeNumber: true),
                new ParameterRule("window_seconds", ParameterUnit.Seconds, double.Epsilon, Required: false)
            }));
        return result;
    }
}
