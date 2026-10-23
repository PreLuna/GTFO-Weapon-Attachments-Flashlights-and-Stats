using System;
using System.Collections.Generic;
using System.Linq;

namespace PreLuna.WeaponAttachments.Definitions;

public static class CatalogValidator
{
    public static IReadOnlyList<DefinitionIssue> Validate(CatalogDocument document, CapabilityRegistry registry)
    {
        var issues = new List<DefinitionIssue>();
        void Error(string code, string path, string message) => issues.Add(new(IssueSeverity.Error, code, path, message));
        if (document.SchemaVersion != 1) Error("SCHEMA_VERSION", "$.SchemaVersion", "只支持格式版本 1。");
        if (!DefinitionIds.IsValid(document.CatalogId)) Error("CATALOG_ID", "$.CatalogId", "目录标识必须为小写稳定标识。");
        if (document.Attachments == null || document.Attachments.Count > CatalogJson.MaximumAttachments)
        {
            Error("ATTACHMENT_COLLECTION", "$.Attachments", "定义列表不能为空对象或超过数量上限。");
            return issues.AsReadOnly();
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < document.Attachments.Count; i++)
        {
            var definition = document.Attachments[i];
            string path = "$.Attachments[" + i + "]";
            if (definition == null) { Error("NULL_ATTACHMENT", path, "配件定义不能为 null。"); continue; }
            if (!DefinitionIds.IsValid(definition.Id) || definition.Id.StartsWith("original.", StringComparison.Ordinal) || definition.Id.StartsWith("system.", StringComparison.Ordinal))
                Error("ATTACHMENT_ID", path + ".Id", "配件需要稳定小写 ID；original. 前缀保留给系统原装项。");
            else if (!ids.Add(definition.Id)) Error("DUPLICATE_ID", path + ".Id", "配件 ID 重复。");
            if (string.IsNullOrWhiteSpace(definition.DisplayName)) Error("DISPLAY_NAME", path + ".DisplayName", "显示名称不能为空。");
            var slot = SlotCatalog.Find(definition.Slot);
            if (slot == null) Error("UNKNOWN_SLOT", path + ".Slot", "未知配件槽位。");
            else if (slot.Reserved) Error("RESERVED_SLOT", path + ".Slot", "瞄准镜首版预留，只提供系统原装项。");

            ValidateApplicability(definition.Applicability, path + ".Applicability", issues);
            if (definition.Modifiers == null) Error("NULL_MODIFIERS", path + ".Modifiers", "使用空列表表示无属性修正。");
            else for (int j = 0; j < definition.Modifiers.Count; j++) ValidateModifier(definition.Modifiers[j], path + ".Modifiers[" + j + "]", registry, issues);
            if (definition.Mechanisms == null) Error("NULL_MECHANISMS", path + ".Mechanisms", "使用空列表表示无机制。");
            else for (int j = 0; j < definition.Mechanisms.Count; j++)
            {
                var mechanism = definition.Mechanisms[j];
                string at = path + ".Mechanisms[" + j + "]";
                if (mechanism == null) { Error("NULL_MECHANISM", at, "机制定义不能为 null。"); continue; }
                ValidateParameters(mechanism.Capability, mechanism.Parameters, registry.Mechanisms, at, "UNKNOWN_MECHANISM", issues);
                ValidateCondition(mechanism.Condition, at + ".Condition", registry, issues);
            }
            if (definition.VisualBinding == null) Error("NULL_VISUAL_BINDING", path + ".VisualBinding", "外观绑定必须是关闭的描述对象。");
            else if (definition.VisualBinding.Enabled) Error("VISUALS_DISABLED", path + ".VisualBinding.Enabled", "当前版本保留模型与动作资源，不启用外观绑定。");
            if (definition.Slot == AttachmentSlot.Flashlight)
            {
                if (definition.Flashlight == null) Error("FLASHLIGHT_REQUIRED", path + ".Flashlight", "手电选项需要原版配置来源引用。");
                if (definition.Modifiers?.Count > 0 || definition.Mechanisms?.Count > 0)
                    Error("FLASHLIGHT_PROFILE_ONLY", path, "手电槽只选装原版照明与光斑，不附加武器属性或额外机制。");
            }
            else if (definition.Flashlight != null) Error("FLASHLIGHT_SLOT", path + ".Flashlight", "原版手电引用只能用于手电槽。");
            if (definition.Flashlight != null) ValidateFlashlight(definition.Flashlight, path + ".Flashlight", issues);
            if (definition.Modifiers?.Count == 0 && definition.Mechanisms?.Count == 0 && definition.Flashlight == null)
                Error("EMPTY_CUSTOM_ATTACHMENT", path, "自定义配件没有声明任何效果；原装项由系统生成。");
        }
        if (!issues.Any(item => item.Severity == IssueSeverity.Error) && document.Attachments.Count > 0)
            issues.Add(new(IssueSeverity.Warning, "RUNTIME_CHECK_REQUIRED", "$.Attachments", "格式已通过校验；实际执行仍需检查目标实例的接点、能力与资源是否就绪。"));
        return issues.AsReadOnly();
    }

    private static void ValidateApplicability(ApplicabilityDefinition? value, string path, List<DefinitionIssue> issues)
    {
        void Error(string code, string message) => issues.Add(new(IssueSeverity.Error, code, path, message));
        if (value == null) { Error("APPLICABILITY_REQUIRED", "必须声明适用对象与武器范围。"); return; }
        if (value.Targets == null || value.Targets.Count == 0 || value.Targets.Any(item => item is not (ConfigurationTargetKind.LocalPlayer or ConfigurationTargetKind.HostBot)))
            Error("TARGETS", "只能声明 LocalPlayer、HostBot，且列表不能为空。");
        else if (value.Targets.Distinct().Count() != value.Targets.Count) Error("DUPLICATE_TARGET", "适用对象重复。");
        if (value.WeaponScope is not (WeaponScope.All or WeaponScope.ListedOnly)) Error("WEAPON_SCOPE", "必须显式选择 All 或 ListedOnly。");
        if (value.Weapons == null || value.ExcludedWeapons == null) Error("WEAPON_LIST", "武器列表需要使用数组。");
        else
        {
            if (value.WeaponScope == WeaponScope.ListedOnly && value.Weapons.Count == 0) Error("EMPTY_WEAPON_ALLOWLIST", "ListedOnly 不能使用空武器列表。");
            if (value.WeaponScope == WeaponScope.All && value.Weapons.Count > 0) Error("AMBIGUOUS_WEAPON_SCOPE", "All 不同时填写包含列表；可使用 ExcludedWeapons。");
            ValidateWeaponList(value.Weapons, path + ".Weapons", issues);
            ValidateWeaponList(value.ExcludedWeapons, path + ".ExcludedWeapons", issues);
        }
        if (value.RequiredWeaponCapabilities == null || value.RequiredWeaponCapabilities.Any(item => !DefinitionIds.IsValid(item)))
            Error("WEAPON_CAPABILITIES", "武器能力标识必须是稳定小写标识。");
        else if (value.RequiredWeaponCapabilities.Distinct(StringComparer.Ordinal).Count() != value.RequiredWeaponCapabilities.Count)
            Error("DUPLICATE_WEAPON_CAPABILITY", "武器能力要求重复。");
    }

    private static void ValidateWeaponList(IReadOnlyList<WeaponReference> values, string path, List<DefinitionIssue> issues)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < values.Count; i++)
        {
            string at = path + "[" + i + "]";
            WeaponIdentity? identity = ValidateWeapon(values[i], at, issues);
            if (identity != null && !identities.Add(identity.StableKey)) issues.Add(new(IssueSeverity.Error, "DUPLICATE_WEAPON", at, "武器身份重复。"));
        }
    }

    private static WeaponIdentity? ValidateWeapon(WeaponReference? value, string path, List<DefinitionIssue> issues)
    {
        try { return value?.ToIdentity() ?? throw new ArgumentException("武器引用不能为空。"); }
        catch (ArgumentException error) { issues.Add(new(IssueSeverity.Error, "WEAPON_IDENTITY", path, error.Message)); return null; }
    }

    private static void ValidateModifier(StatModifierDefinition? value, string path, CapabilityRegistry registry, List<DefinitionIssue> issues)
    {
        if (value == null) { issues.Add(new(IssueSeverity.Error, "NULL_MODIFIER", path, "属性修正不能为 null。")); return; }
        if (value.Stat == null || !registry.Stats.TryGetValue(value.Stat, out var descriptor))
            issues.Add(new(IssueSeverity.Error, "UNKNOWN_STAT", path + ".Stat", "属性尚未注册，不能静默忽略。"));
        else if (value.Operation != descriptor.BeneficialOperation)
            issues.Add(new(IssueSeverity.Error, "NON_BENEFICIAL_OPERATION", path + ".Operation", "修正方向不符合此属性的加成含义。"));
        if (!value.Percent.HasValue || !double.IsFinite(value.Percent.Value) || value.Percent < 0 ||
            (value.Operation == PercentOperation.DecreasePercent && value.Percent > 100))
            issues.Add(new(IssueSeverity.Error, "PERCENT_RANGE", path + ".Percent", "百分比必须是非负有限数；减少比例不得超过 100%。"));
        ValidateCondition(value.Condition, path + ".Condition", registry, issues);
    }

    private static void ValidateCondition(ConditionDefinition? value, string path, CapabilityRegistry registry, List<DefinitionIssue> issues)
    {
        if (value != null) ValidateParameters(value.Id, value.Parameters, registry.Conditions, path, "UNKNOWN_CONDITION", issues);
    }

    private static void ValidateParameters(string id, IReadOnlyDictionary<string, double>? values,
        IReadOnlyDictionary<string, ParameterCapability> capabilities, string path, string unknownCode, List<DefinitionIssue> issues)
    {
        if (id == null || !capabilities.TryGetValue(id, out var capability))
        {
            issues.Add(new(IssueSeverity.Error, unknownCode, path, "未知能力或条件，需先注册格式定义。")); return;
        }
        if (values == null) { issues.Add(new(IssueSeverity.Error, "NULL_PARAMETERS", path + ".Parameters", "参数必须是对象。")); return; }
        foreach (var supplied in values.Keys)
            if (!capability.Parameters.Any(rule => rule.Name == supplied)) issues.Add(new(IssueSeverity.Error, "UNKNOWN_PARAMETER", path + ".Parameters." + supplied, "参数未注册。"));
        foreach (var rule in capability.Parameters)
        {
            if (!values.TryGetValue(rule.Name, out double number))
            {
                if (rule.Required) issues.Add(new(IssueSeverity.Error, "MISSING_PARAMETER", path + ".Parameters." + rule.Name, "缺少必需参数。"));
                continue;
            }
            if (!double.IsFinite(number) || number < rule.Minimum || (rule.Maximum.HasValue && number > rule.Maximum.Value) ||
                (rule.WholeNumber && Math.Truncate(number) != number))
                issues.Add(new(IssueSeverity.Error, "PARAMETER_RANGE", path + ".Parameters." + rule.Name, "参数不符合其单位、范围或整数要求。"));
        }
    }

    private static void ValidateFlashlight(FlashlightReference value, string path, List<DefinitionIssue> issues)
    {
        if (!DefinitionIds.IsValid(value.DataPackId)) issues.Add(new(IssueSeverity.Error, "FLASHLIGHT_PACK", path + ".DataPackId", "手电来源需要明确数据包身份。"));
        if (value.SourcePartId == 0 || value.SettingsId == 0) issues.Add(new(IssueSeverity.Error, "FLASHLIGHT_ID", path, "部件和照明配置 ID 必须大于零，且是两个独立编号。"));
        if (!value.SourcePartId.HasValue && !value.SettingsId.HasValue && value.SourceWeapon == null)
            issues.Add(new(IssueSeverity.Error, "FLASHLIGHT_SOURCE", path, "至少声明原版部件、照明配置或来源武器之一。"));
        if (value.SourceWeapon != null) ValidateWeapon(value.SourceWeapon, path + ".SourceWeapon", issues);
    }
}
