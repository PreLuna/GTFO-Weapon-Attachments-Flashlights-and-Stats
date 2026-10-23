using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;

namespace PreLuna.WeaponAttachments.Modifiers;

public sealed class ModifierCalculator
{
    public const string LayerId = "preluna.weaponattachments.numeric";
    private readonly AttachmentCatalog _catalog;
    private readonly IReadOnlyDictionary<string, StatDescriptor> _stats;
    public int KnownStatCount => _stats.Count;

    public ModifierCalculator(AttachmentCatalog catalog, CapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(registry);
        registry.Seal();
        _catalog = catalog; _stats = registry.Stats;
    }

    public LoadoutPreview Preview(LoadoutDraft draft, IUpstreamStatSource source, string instanceId, ConditionSnapshot? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(source);
        UpstreamSnapshot snapshot;
        try
        {
            snapshot = source.Read(new(draft.Target, draft.Weapon, instanceId, LayerId));
            if (snapshot == null || snapshot.InstanceId != instanceId || snapshot.Target.StableKey != draft.Target.StableKey ||
                !snapshot.Weapon.Equals(draft.Weapon))
                return FailedPreview(draft, instanceId, "SOURCE_CONTEXT", "上游来源返回了其他对象、武器或实例的数据。");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return FailedPreview(draft, instanceId, "SOURCE_READ_FAILED", error.Message);
        }
        return PreviewResolved(draft.CommittedSnapshot, draft.Snapshot, snapshot, conditions);
    }

    public LoadoutPreview PreviewResolved(LoadoutSnapshot committed, LoadoutSnapshot pending, UpstreamSnapshot snapshot, ConditionSnapshot? conditions = null)
    {
        // Both columns use exactly one capture; the interface may resolve unsupported selections to original first.
        var current = Calculate(committed, snapshot, conditions);
        var preview = Calculate(pending, snapshot, conditions);
        var ids = current.Stats.Select(item => item.Stat).Union(preview.Stats.Select(item => item.Stat), StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        var rows = ids.Select(id =>
        {
            var before = current.Stats.FirstOrDefault(item => item.Stat == id);
            var after = preview.Stats.FirstOrDefault(item => item.Stat == id);
            var info = after ?? before!;
            return new PreviewRow(id, info.Unit, info.Form, info.BaseValue, info.UpstreamValue,
                before?.Value, after?.Value, before?.CanCalculate == true && after?.CanCalculate == true);
        }).ToArray();
        return new(pending.Target, pending.Weapon, snapshot.InstanceId, current, preview, Array.AsReadOnly(rows),
            Array.AsReadOnly(current.Issues.Concat(preview.Issues).Distinct().ToArray()));
    }

    public CalculationResult Calculate(LoadoutSnapshot loadout, UpstreamSnapshot upstream, ConditionSnapshot? conditions = null)
        => CalculateCore(loadout, upstream, conditions, null);

    public CalculationResult CalculateSelectedStats(LoadoutSnapshot loadout, UpstreamSnapshot upstream,
        IReadOnlyCollection<string> stats, ConditionSnapshot? conditions = null)
        => CalculateCore(loadout, upstream, conditions, stats ?? throw new ArgumentNullException(nameof(stats)));

    private CalculationResult CalculateCore(LoadoutSnapshot loadout, UpstreamSnapshot upstream, ConditionSnapshot? conditions,
        IReadOnlyCollection<string>? selectedStats)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(upstream);
        var issues = new List<CalculationIssue>();
        var deferred = new List<DeferredEffect>();
        if (loadout.Target.StableKey != upstream.Target.StableKey || !loadout.Weapon.Equals(upstream.Weapon))
            issues.Add(Error("SOURCE_CONTEXT", "$", "对象或武器不匹配，未计算此配装。"));
        if (upstream.AppliedLayerIds.Contains(LayerId, StringComparer.Ordinal))
            issues.Add(Error("OWN_LAYER_IN_UPSTREAM", "$", "上游已包含本配件层，拒绝重复相乘。"));
        if (loadout.Selections == null || loadout.Selections.Any(item => item == null || SlotCatalog.Find(item.Slot) == null) ||
            loadout.Selections.GroupBy(item => item.Slot).Any(group => group.Count() > 1))
            issues.Add(Error("SELECTION_STRUCTURE", "$", "配装快照包含未知或重复槽位。"));
        if (issues.Count > 0) return new(upstream, Array.Empty<CalculatedStat>(), issues, deferred);
        var contributions = new List<ModifierContribution>();
        conditions ??= ConditionSnapshot.Unknown;
        foreach (var selection in loadout.Selections!.OrderBy(item => item.Slot))
        {
            var entry = DefinitionIds.IsValid(selection.RequestedId) ? _catalog.Find(selection.RequestedId) : null;
            string? unavailable = entry == null ? "UNKNOWN_ATTACHMENT" : entry.Definition.Slot != selection.Slot ? "WRONG_SLOT" : null;
            if (unavailable == null)
            {
                var availability = _catalog.CheckAvailability(selection.RequestedId, upstream.Weapon, upstream.Target, upstream.WeaponCapabilities);
                if (!availability.Applicable) unavailable = availability.Code;
            }
            if (unavailable != null)
            {
                issues.Add(Warning(unavailable, selection.RequestedId, "此选择当前不可用，计算时暂用原装。"));
                continue;
            }
            var definition = entry!.Definition;
            for (int i = 0; i < definition.Modifiers.Count; i++)
            {
                var modifier = definition.Modifiers[i];
                if (selectedStats != null && !selectedStats.Contains(modifier.Stat)) continue;
                contributions.Add(new(selection.Slot, definition.Id, i, modifier.Stat, modifier.Percent!.Value,
                    ConditionSnapshot.Key(modifier.Condition), conditions.Evaluate(modifier.Condition)));
            }
            foreach (var mechanism in selectedStats == null ? definition.Mechanisms : Array.Empty<MechanismDefinition>())
                deferred.Add(new(selection.Slot, definition.Id, "mechanism." + mechanism.Capability));
            if (selectedStats == null && definition.Flashlight != null) deferred.Add(new(selection.Slot, definition.Id, "flashlight.original_profile"));
        }
        if (deferred.Count > 0) issues.Add(Warning("DEFERRED_EFFECTS", "$", "机制和原版手电只列出待接入能力，不当作普通百分比计算。"));
        var rows = upstream.Values.Keys.Where(id => selectedStats == null || selectedStats.Contains(id)).Union(contributions.Select(item => item.Stat), StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).Select(id => CalculateStat(id,
                upstream.Values.GetValueOrDefault(id), contributions.Where(item => item.Stat == id).ToArray())).ToList();
        CheckFalloffOrder(rows);
        issues.AddRange(rows.SelectMany(row => row.Issues));
        return new(upstream, rows, issues, deferred);
    }

    private CalculatedStat CalculateStat(string id, StatInput? input, ModifierContribution[] contributions)
    {
        var issues = new List<CalculationIssue>();
        var layers = new List<PercentageLayer>();
        bool canCalculate = true;
        double? original = Finite(input?.UpstreamValue);
        double? baseValue = Finite(input?.BaseValue);
        void Reject(string code, string message) { canCalculate = false; issues.Add(Error(code, id, message)); }
        if (input == null) Reject("MISSING_UPSTREAM", "未取得此属性排除本层后的上游值。");
        if (!_stats.TryGetValue(id, out var descriptor)) Reject("UNKNOWN_STAT", "此属性没有注册计算规则。");
        if (input != null && (!double.IsFinite(input.UpstreamValue) || input.UpstreamValue < 0))
            Reject("UPSTREAM_VALUE", "上游值必须为非负有限数。");
        if (input?.BaseValue is double rawBase && (!double.IsFinite(rawBase) || rawBase < 0))
        {
            baseValue = null;
            issues.Add(Warning("BASE_VALUE", id, "展示用基础值无效，仍可独立使用有效上游值计算。"));
        }
        var form = input?.Form ?? StatValueForm.Direct;
        if (!Enum.IsDefined(typeof(StatValueForm), form) || (form == StatValueForm.DurationSeconds && descriptor?.CalculationKind != StatCalculationKind.Speed))
            Reject("VALUE_FORM", "只有速度属性可使用秒数形式，不能混用倍率与时间。");
        if (form == StatValueForm.DurationSeconds && input?.UpstreamValue <= 0)
            Reject("DURATION_VALUE", "时间基准必须大于零。");
        if (descriptor?.CalculationKind == StatCalculationKind.Capacity && original is double capacity &&
            (capacity != Math.Truncate(capacity) || capacity > int.MaxValue))
            Reject("CAPACITY_BASELINE", "容量基准必须是非负 32 位整数，不能先取整改变上游值。");
        double multiplier = 1;
        if (descriptor != null)
        foreach (var group in contributions.GroupBy(item => item.LayerId).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var activity = group.First().Activity;
            double sum = 0;
            foreach (var contribution in group) sum += contribution.Percent;
            double? requested = double.IsFinite(sum) ? sum : null;
            double percent = 0, factor = 1;
            if (activity == ConditionActivity.Unknown)
                issues.Add(Warning("CONDITION_UNKNOWN", group.Key, "未提供此条件的状态，本次只预览永久加成与已知激活层。"));
            if (activity == ConditionActivity.Active)
            {
                if (!requested.HasValue) Reject("PERCENT_OVERFLOW", "同层百分比求和溢出。");
                else
                {
                    percent = descriptor.BeneficialOperation == PercentOperation.DecreasePercent ? Math.Min(100, sum) : sum;
                    if (percent != sum) issues.Add(Warning("REDUCTION_CAPPED", id, "同层减少比例合计超过 100%，有效比例限制为 100%。"));
                    factor = descriptor.BeneficialOperation == PercentOperation.IncreasePercent ? 1 + percent / 100 : 1 - percent / 100;
                    multiplier *= factor;
                    if (!double.IsFinite(multiplier)) Reject("MULTIPLIER_OVERFLOW", "合成倍率溢出，未使用部分结果。");
                }
            }
            layers.Add(new(group.Key, activity, requested, percent, factor, Array.AsReadOnly(group.ToArray())));
        }
        bool hasBonus = layers.Any(layer => layer.Activity == ConditionActivity.Active && layer.AppliedPercent > 0);
        if (hasBonus && descriptor?.RequiresNonZeroBaseline == true && original == 0)
            Reject("ZERO_BASELINE", "上游基准为零，不能用百分比新增非零属性。");
        double? value = original;
        if (canCalculate && original.HasValue && hasBonus)
        {
            if (descriptor!.CalculationKind == StatCalculationKind.Capacity)
            {
                try
                {
                    decimal result = (decimal)original.Value;
                    foreach (var group in contributions.Where(item => item.Activity == ConditionActivity.Active).GroupBy(item => item.LayerId)
                        .OrderBy(group => group.Key, StringComparer.Ordinal))
                    {
                        decimal percent = 0;
                        foreach (var contribution in group) percent += (decimal)contribution.Percent;
                        result *= 1 + percent / 100m;
                    }
                    result = decimal.Floor(result);
                    if (result > int.MaxValue || result < (decimal)original.Value) Reject("CAPACITY_RANGE", "容量结果超出有效整数范围，未截断或回绕。");
                    else value = (double)result;
                }
                catch (OverflowException) { Reject("CAPACITY_RANGE", "容量计算溢出，保持上游值。"); }
            }
            else
            {
                double result = form == StatValueForm.DurationSeconds ? original.Value / multiplier : original.Value * multiplier;
                if (!double.IsFinite(result) || result < 0 || (form == StatValueForm.DurationSeconds && result <= 0))
                    Reject("RESULT_RANGE", "结果溢出或时间下溢，保持上游值。");
                else value = result;
            }
        }
        if (!canCalculate) { value = original; multiplier = 1; }
        return new(id, form == StatValueForm.DurationSeconds ? "seconds" : descriptor?.BaseUnit ?? "unknown",
            form, baseValue, original, value, multiplier, canCalculate, canCalculate && value != original,
            Array.AsReadOnly(layers.ToArray()), Array.AsReadOnly(issues.ToArray()));
    }

    private static void CheckFalloffOrder(List<CalculatedStat> rows)
    {
        var start = rows.Find(row => row.Stat == "falloff_start");
        var end = rows.Find(row => row.Stat == "falloff_end");
        if (start?.Value is not double from || end?.Value is not double to || from <= to) return;
        foreach (var row in new[] { start, end })
        {
            var issue = Error("FALLOFF_ORDER", row.Stat, "衰减起点超过终点，两项保持各自上游值并报告不可计算。");
            rows[rows.IndexOf(row)] = row with { Value = row.UpstreamValue, OwnMultiplier = 1, CanCalculate = false,
                Changed = false, Issues = Array.AsReadOnly(row.Issues.Append(issue).ToArray()) };
        }
    }
    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
    private static CalculationIssue Error(string code, string subject, string message) => new(IssueSeverity.Error, code, subject, message);
    private static CalculationIssue Warning(string code, string subject, string message) => new(IssueSeverity.Warning, code, subject, message);
    private static LoadoutPreview FailedPreview(LoadoutDraft draft, string instanceId, string code, string message) =>
        new(draft.Target, draft.Weapon, instanceId, null, null, Array.Empty<PreviewRow>(),
            Array.AsReadOnly(new[] { Error(code, "$", message) }));
}
