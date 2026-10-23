using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PreLuna.WeaponAttachments.Definitions;

internal static class DefinitionTests
{
    private static readonly List<object> Results = new();
    private static int _failed;

    public static int Main(string[] args)
    {
        string output = args[0];
        string files = args[1];
        Directory.CreateDirectory(files);
        Run("空目录生成11个无效果原装项", () =>
        {
            var catalog = Read(CatalogDocument.Empty());
            Check(catalog.OriginalCount == 11 && catalog.CustomCount == 0 && catalog.Entries.Count == 11);
            Check(catalog.Entries.All(item => item.IsOriginal && item.Definition.Modifiers.Count == 0 &&
                item.Definition.Mechanisms.Count == 0 && item.Definition.Flashlight == null && !item.Definition.VisualBinding.Enabled));
            Check(SlotCatalog.All.All(slot => catalog.ForSlot(slot.Slot).Count == 1));
        });
        Run("11槽与四个配置对象均有稳定标识", () =>
        {
            Check(SlotCatalog.All.Count == 11 && SlotCatalog.All.Count(slot => slot.Reserved) == 1);
            Check(ConfigurationTarget.All.Count == 4 && ConfigurationTarget.All.Select(item => item.StableKey).Distinct().Count() == 4);
            Check(ConfigurationTarget.All.Skip(1).All(item => item.Kind == ConfigurationTargetKind.HostBot));
            Throws<ArgumentOutOfRangeException>(() => ConfigurationTarget.ForBot(0));
            Throws<ArgumentOutOfRangeException>(() => ConfigurationTarget.ForBot(4));
        });
        Run("同Archetype的不同武器不共用身份", () =>
            Check(!new WeaponIdentity("test.pack", 1, archetypeId: 18).Equals(new WeaponIdentity("test.pack", 2, archetypeId: 18))));
        Run("不同数据包和变体身份独立", () =>
        {
            var identity = new WeaponIdentity("test.pack", 1);
            Check(!identity.Equals(new WeaponIdentity("other.pack", 1)));
            Check(!identity.Equals(new WeaponIdentity("test.pack", 1, "variant.a")));
            Check(!identity.Equals(new WeaponIdentity("test.pack", 1, "none")));
            Check(!new WeaponIdentity("test.pack", null, "variant.a").Equals(new WeaponIdentity("test.pack", null, "variant.b")));
        });
        Run("Archetype只作描述不影响稳定身份或哈希", () =>
        {
            var first = new WeaponIdentity("test.pack", 1, archetypeId: 18);
            var second = new WeaponIdentity("test.pack", 1, archetypeId: 19);
            Check(first.Equals(second) && first.GetHashCode() == second.GetHashCode());
        });
        Run("拒绝仅用Archetype或昵称作为身份", () =>
        {
            Throws<ArgumentException>(() => new WeaponIdentity("test.pack", null, archetypeId: 18));
            Throws<ArgumentException>(() => new WeaponIdentity("中文昵称", 1));
            Throws<ArgumentException>(() => new WeaponIdentity("test.pack", 0));
            Throws<ArgumentException>(() => new WeaponIdentity("test.pack", 1, "variant\n"));
        });
        Run("空目录可以序列化再读取", () => Check(Read(CatalogDocument.Empty()).OriginalCount == 11));
        Run("有效百分比定义可读取但执行器未实现", () =>
        {
            var result = Parse(Document(Definition()));
            Check(result.Success && result.Issues.Any(issue => issue.Code == "RUNTIME_CHECK_REQUIRED"));
            var availability = result.Catalog!.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.LocalPlayer);
            Check(availability.Applicable && !availability.RuntimeReady && availability.MissingCapabilities.Contains("stat.damage"));
        });
        Run("正的减少百分比在低值有利属性上属于加成", () =>
            Check(Parse(Document(Definition() with { Modifiers = new[] { new StatModifierDefinition { Stat = "recoil", Operation = PercentOperation.DecreasePercent, Percent = 20 } } })).Success));
        Run("错误加成方向被识别", () =>
            Reject(Document(Definition() with { Modifiers = new[] { new StatModifierDefinition { Stat = "damage", Operation = PercentOperation.DecreasePercent, Percent = 20 } } }), "NON_BENEFICIAL_OPERATION"));
        Run("负加成百分比被拒绝", () => Reject(Document(Definition(-1)), "PERCENT_RANGE"));
        Run("缺少百分比被拒绝", () => Reject(Document(Definition() with { Modifiers = new[] { new StatModifierDefinition { Stat = "damage", Operation = PercentOperation.IncreasePercent } } }), "PERCENT_RANGE"));
        Run("超过100%的减少比例被拒绝", () =>
            Reject(Document(Definition() with { Modifiers = new[] { new StatModifierDefinition { Stat = "recoil", Operation = PercentOperation.DecreasePercent, Percent = 101 } } }), "PERCENT_RANGE"));
        Run("直接构造的非有限百分比也被校验", () =>
        {
            var issues = CatalogValidator.Validate(Document(Definition(double.NaN)), CapabilityRegistry.CreateDefaults());
            Check(issues.Any(issue => issue.Code == "PERCENT_RANGE"));
        });
        Run("未知属性不静默忽略", () =>
            Reject(Document(Definition() with { Modifiers = new[] { new StatModifierDefinition { Stat = "damgae", Operation = PercentOperation.IncreasePercent, Percent = 10 } } }), "UNKNOWN_STAT"));
        Run("重复配件ID使整个目录失败", () => Reject(Document(Definition(), Definition()), "DUPLICATE_ID"));
        Run("不允许冒充原装ID", () => Reject(Document(Definition() with { Id = "original.muzzle" }), "ATTACHMENT_ID"));
        Run("空显示名称被拒绝", () => Reject(Document(Definition() with { DisplayName = " " }), "DISPLAY_NAME"));
        Run("未定义槽位被拒绝", () => Reject(Document(Definition() with { Slot = AttachmentSlot.Unknown }), "UNKNOWN_SLOT"));
        Run("首版瞄准镜仅有原装项", () =>
        {
            Reject(Document(Definition() with { Slot = AttachmentSlot.Optic }), "RESERVED_SLOT");
            Check(Read(CatalogDocument.Empty()).CheckAvailability("original.optic", Weapon(), ConfigurationTarget.LocalPlayer).RuntimeReady);
        });
        Run("未知字段和拼错百分比字段被识别", () =>
        {
            var node = JsonNode.Parse(CatalogJson.Serialize(Document(Definition())))!;
            node["Attachments"]![0]!["Modifiers"]![0]!["Percnet"] = 20;
            RejectJson(node.ToJsonString(), "UNKNOWN_PROPERTY");
        });
        Run("重复JSON字段被拒绝而非采用最后值", () =>
            RejectJson("""{"SchemaVersion":1,"SchemaVersion":2,"CatalogId":"test.catalog","Attachments":[]}""", "DUPLICATE_PROPERTY"));
        Run("未知根字段被拒绝", () =>
            RejectJson("""{"SchemaVersion":1,"CatalogId":"test.catalog","Attachments":[],"Typo":true}""", "UNKNOWN_PROPERTY"));
        Run("缺少Attachments数组被拒绝", () =>
            RejectJson("""{"SchemaVersion":1,"CatalogId":"test.catalog"}""", "MISSING_ATTACHMENTS"));
        Run("不兼容格式版本被拒绝", () => Reject(CatalogDocument.Empty() with { SchemaVersion = 2 }, "SCHEMA_VERSION"));
        Run("坏JSON和null目录不会导致加载器崩溃", () =>
        {
            RejectJson("{", "INVALID_JSON");
            Check(!CatalogJson.Read("null", CapabilityRegistry.CreateDefaults()).Success);
        });
        Run("枚举拒绝数字及数字字符串", () =>
        {
            string source = CatalogJson.Serialize(Document(Definition()));
            RejectJson(source.Replace("\"Muzzle\"", "1", StringComparison.Ordinal), "INVALID_JSON");
            RejectJson(source.Replace("\"Muzzle\"", "\"1\"", StringComparison.Ordinal), "INVALID_JSON");
            RejectJson(source.Replace("\"Muzzle\"", "\"muzzle\"", StringComparison.Ordinal), "INVALID_JSON");
        });
        Run("只有本人和主机BOT两类声明对象", () =>
        {
            string source = CatalogJson.Serialize(Document(Definition()));
            RejectJson(source.Replace("\"HostBot\"", "\"RemotePlayer\"", StringComparison.Ordinal), "INVALID_JSON");
        });
        Run("没有明确武器范围会被拒绝", () =>
            Reject(Document(Definition() with { Applicability = Rules() with { WeaponScope = WeaponScope.Unknown } }), "WEAPON_SCOPE"));
        Run("ListedOnly不能悄悄退化为全武器", () =>
            Reject(Document(Definition() with { Applicability = Rules() with { WeaponScope = WeaponScope.ListedOnly } }), "EMPTY_WEAPON_ALLOWLIST"));
        Run("All与包含列表冲突会被识别", () =>
            Reject(Document(Definition() with { Applicability = Rules() with { Weapons = new[] { Reference(1) } } }), "AMBIGUOUS_WEAPON_SCOPE"));
        Run("列出的武器按稳定身份匹配", () =>
        {
            var catalog = Read(Document(Definition() with { Applicability = Rules() with { WeaponScope = WeaponScope.ListedOnly, Weapons = new[] { Reference(1) } } }));
            Check(catalog.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.ForBot(1)).Applicable);
            Check(catalog.CheckAvailability("test.bonus", new WeaponIdentity("test.pack", 2), ConfigurationTarget.ForBot(1)).Code == "WRONG_WEAPON");
            Check(catalog.CheckAvailability("test.bonus", new WeaponIdentity("other.pack", 1), ConfigurationTarget.ForBot(1)).Code == "WRONG_WEAPON");
        });
        Run("排除列表和对象限制实际参与匹配", () =>
        {
            var catalog = Read(Document(Definition() with { Applicability = Rules() with { ExcludedWeapons = new[] { Reference(2) }, Targets = new[] { ConfigurationTargetKind.HostBot } } }));
            Check(catalog.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.LocalPlayer).Code == "WRONG_TARGET");
            Check(catalog.CheckAvailability("test.bonus", new WeaponIdentity("test.pack", 2), ConfigurationTarget.ForBot(2)).Code == "WRONG_WEAPON");
            Check(catalog.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.ForBot(3)).Applicable);
        });
        Run("武器能力要求缺失时明确返回原因", () =>
        {
            var catalog = Read(Document(Definition() with { Applicability = Rules() with { RequiredWeaponCapabilities = new[] { "uses_magazine" } } }));
            Check(catalog.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.LocalPlayer).Code == "MISSING_WEAPON_CAPABILITY");
            Check(catalog.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.LocalPlayer, new[] { "uses_magazine" }).Applicable);
        });
        Run("武器列表中的重复身份被识别", () =>
            Reject(Document(Definition() with { Applicability = Rules() with { WeaponScope = WeaponScope.ListedOnly, Weapons = new[] { Reference(1), Reference(1) } } }), "DUPLICATE_WEAPON"));
        Run("机制参数保留比例含义并校验范围", () =>
        {
            Check(Parse(Document(Mechanism("armor_pierce", new() { ["ignored_armor_percent"] = 50 }))).Success);
            Reject(Document(Mechanism("armor_pierce", new() { ["ignored_armor_percent"] = 101 })), "PARAMETER_RANGE");
            Reject(Document(Mechanism("armor_pierce", new())), "MISSING_PARAMETER");
        });
        Run("返弹计数必须是正整数而非任意浮点数", () =>
        {
            Check(Parse(Document(Mechanism("ammo_return_to_reserve", new() { ["rounds"] = 1 }))).Success);
            Reject(Document(Mechanism("ammo_return_to_reserve", new() { ["rounds"] = 1.5 })), "PARAMETER_RANGE");
            Reject(Document(Mechanism("ammo_return_to_reserve", new() { ["rounds"] = 0 })), "PARAMETER_RANGE");
        });
        Run("未知机制或额外参数被拒绝", () =>
        {
            Reject(Document(Mechanism("unregistered.effect", new())), "UNKNOWN_MECHANISM");
            Reject(Document(Mechanism("armor_pierce", new() { ["ignored_armor_percent"] = 50, ["typo"] = 1 })), "UNKNOWN_PARAMETER");
        });
        Run("条件类型与计数参数被校验", () =>
        {
            var modifier = Definition().Modifiers[0] with { Condition = new ConditionDefinition { Id = "weakpoint_hits", Parameters = new Dictionary<string, double> { ["count"] = 3 } } };
            Check(Parse(Document(Definition() with { Modifiers = new[] { modifier } })).Success);
            Reject(Document(Definition() with { Modifiers = new[] { modifier with { Condition = new ConditionDefinition { Id = "unknown" } } } }), "UNKNOWN_CONDITION");
            Reject(Document(Definition() with { Modifiers = new[] { modifier with { Condition = new ConditionDefinition { Id = "weakpoint_hits" } } } }), "MISSING_PARAMETER");
        });
        Run("自定义注册可以扩展格式但不伪装已实现", () =>
        {
            var registry = CapabilityRegistry.CreateDefaults();
            registry.RegisterStat(new("extension.test", PercentOperation.IncreasePercent, "relative"));
            var definition = Definition() with { Modifiers = new[] { new StatModifierDefinition { Stat = "extension.test", Operation = PercentOperation.IncreasePercent, Percent = 10 } } };
            var result = CatalogJson.Read(CatalogJson.Serialize(Document(definition)), registry);
            Check(result.Success && !result.Catalog!.CheckAvailability("test.bonus", Weapon(), ConfigurationTarget.LocalPlayer).RuntimeReady);
            Throws<InvalidOperationException>(() => registry.RegisterStat(new("extension.late", PercentOperation.IncreasePercent, "relative")));
        });
        Run("注册表拒绝重复条目和无效参数单位", () =>
        {
            var registry = CapabilityRegistry.CreateDefaults();
            Throws<ArgumentException>(() => registry.RegisterStat(new("damage", PercentOperation.IncreasePercent, "damage")));
            Throws<ArgumentException>(() => registry.RegisterCondition(new("bad.rule", new[] { new ParameterRule("count", (ParameterUnit)999, 0) })));
        });
        Run("手电部件和配置编号可不同且仍待运行验证", () =>
        {
            var catalog = Read(Document(Flashlight()));
            var reference = catalog.Find("test.light")!.Definition.Flashlight!;
            Check(reference.SourcePartId == 4 && reference.SettingsId == 5);
            var availability = catalog.CheckAvailability("test.light", Weapon(), ConfigurationTarget.LocalPlayer);
            Check(availability.Applicable && !availability.RuntimeReady && availability.MissingCapabilities.Contains("flashlight.original_profile"));
        });
        Run("手电来源不能为空或使用零编号", () =>
        {
            Reject(Document(Flashlight() with { Flashlight = new FlashlightReference { DataPackId = "test.pack" } }), "FLASHLIGHT_SOURCE");
            Reject(Document(Flashlight() with { Flashlight = new FlashlightReference { DataPackId = "test.pack", SettingsId = 0 } }), "FLASHLIGHT_ID");
        });
        Run("手电引用不能跨槽或附带武器数值修改", () =>
        {
            Reject(Document(Flashlight() with { Slot = AttachmentSlot.Muzzle }), "FLASHLIGHT_SLOT");
            Reject(Document(Flashlight() with { Modifiers = Definition().Modifiers }), "FLASHLIGHT_PROFILE_ONLY");
        });
        Run("外观绑定可描述但不能启用", () =>
        {
            Check(Parse(Document(Definition() with { VisualBinding = new VisualBindingDefinition { Enabled = false, BindingId = "future.binding", MountPointId = "future.mount" } })).Success);
            Reject(Document(Definition() with { VisualBinding = new VisualBindingDefinition { Enabled = true } }), "VISUALS_DISABLED");
        });
        Run("目录快照不能通过列表引用绕过校验", () =>
        {
            var catalog = Read(Document(Definition()));
            Throws<NotSupportedException>(() => ((IList<CatalogEntry>)catalog.Entries).Clear());
            Throws<NotSupportedException>(() => ((IList<StatModifierDefinition>)catalog.Find("test.bonus")!.Definition.Modifiers).Clear());
            Throws<NotSupportedException>(() => ((IList<ConfigurationTargetKind>)catalog.Find("test.bonus")!.Definition.Applicability!.Targets).Clear());
        });
        Run("null列表和null条目不会导致未处理异常", () =>
        {
            RejectJson("""{"SchemaVersion":1,"CatalogId":"test.catalog","Attachments":null}""", "ATTACHMENT_COLLECTION");
            RejectJson("""{"SchemaVersion":1,"CatalogId":"test.catalog","Attachments":[null]}""", "OBJECT_EXPECTED");
            string source = CatalogJson.Serialize(Document(Definition()));
            var node = JsonNode.Parse(source)!;
            node["Attachments"]![0]!["Modifiers"] = null;
            RejectJson(node.ToJsonString(), "NULL_MODIFIERS");
        });
        Run("文件缺失创建空目录而非示例配件", () =>
        {
            string path = Path.Combine(files, "new", "attachments.catalog.json");
            var result = CatalogFile.Load(path, CapabilityRegistry.CreateDefaults());
            Check(result.Status == "CreatedDefaults" && result.Catalog.OriginalCount == 11 && result.Catalog.CustomCount == 0);
            Check(File.Exists(path) && Parse(JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(path))!).Success);
        });
        Run("错误文件回退原装且不覆盖原内容", () =>
        {
            string path = Path.Combine(files, "invalid.json");
            const string invalid = """{"SchemaVersion":999,"CatalogId":"test.catalog","Attachments":[]}""";
            File.WriteAllText(path, invalid);
            var result = CatalogFile.Load(path, CapabilityRegistry.CreateDefaults());
            Check(result.Status == "RejectedFallback" && result.Catalog.OriginalCount == 11 && result.Catalog.CustomCount == 0);
            Check(File.ReadAllText(path) == invalid && result.Issues.Any(issue => issue.Code == "SCHEMA_VERSION"));
        });
        Run("一个错误条目不会留下部分已接受的加成", () =>
        {
            string path = Path.Combine(files, "partially_invalid.json");
            File.WriteAllText(path, CatalogJson.Serialize(Document(Definition(), Definition() with { Id = "test.other", Slot = AttachmentSlot.Optic })));
            var result = CatalogFile.Load(path, CapabilityRegistry.CreateDefaults());
            Check(result.Status == "RejectedFallback" && result.Catalog.CustomCount == 0);
        });
        Run("过大目录不会被无限读取或覆盖", () =>
        {
            string path = Path.Combine(files, "oversized.json");
            string input = new(' ', CatalogJson.MaximumBytes + 1);
            File.WriteAllText(path, input);
            var result = CatalogFile.Load(path, CapabilityRegistry.CreateDefaults());
            Check(result.Status == "RejectedFallback" && result.Issues.Any(issue => issue.Code == "DOCUMENT_SIZE"));
            Check(new FileInfo(path).Length == input.Length);
        });
        Run("正式目录只含生成的原装项", () =>
        {
            var result = CatalogFile.Load(args[2], CapabilityRegistry.CreateDefaults());
            Check(result.Status == "Loaded" && result.Catalog.CustomCount == 0 && result.Catalog.OriginalCount == 11 && result.Issues.Count == 0);
        });

        var report = new { Total = Results.Count, Passed = Results.Count - _failed, Failed = _failed, Cases = Results, ProductionCatalogChanged = false };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"定义模块测试：{Results.Count - _failed}/{Results.Count} 通过，失败 {_failed}。");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try { action(); Results.Add(new { Name = name, Passed = true, Error = "" }); }
        catch (Exception error) { _failed++; Results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("断言未通过"); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("预期异常未发生：" + typeof(T).Name);
    }
    private static CatalogReadResult Parse(CatalogDocument document) => CatalogJson.Read(CatalogJson.Serialize(document), CapabilityRegistry.CreateDefaults());
    private static AttachmentCatalog Read(CatalogDocument document)
    {
        var result = Parse(document);
        if (!result.Success) throw new Exception(string.Join("; ", result.Issues.Select(issue => issue.Code + ": " + issue.Message)));
        return result.Catalog!;
    }
    private static void Reject(CatalogDocument document, string code) => RejectJson(CatalogJson.Serialize(document), code);
    private static void RejectJson(string json, string code)
    {
        var result = CatalogJson.Read(json, CapabilityRegistry.CreateDefaults());
        Check(!result.Success && result.Issues.Any(issue => issue.Code == code));
    }
    private static CatalogDocument Document(params AttachmentDefinition[] values) => new() { SchemaVersion = 1, CatalogId = "test.catalog", Attachments = values };
    private static ApplicabilityDefinition Rules() => new() { Targets = new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All };
    private static WeaponReference Reference(uint id) => new() { DataPackId = "test.pack", OfflineGearId = id };
    private static WeaponIdentity Weapon() => new("test.pack", 1);
    private static AttachmentDefinition Definition(double percentage = 10) => new()
    {
        Id = "test.bonus", DisplayName = "仅供测试，不加入正式配件", Slot = AttachmentSlot.Muzzle, Applicability = Rules(),
        Modifiers = new[] { new StatModifierDefinition { Stat = "damage", Operation = PercentOperation.IncreasePercent, Percent = percentage } }
    };
    private static AttachmentDefinition Mechanism(string id, Dictionary<string, double> parameters) => Definition() with
    {
        Modifiers = Array.Empty<StatModifierDefinition>(), Mechanisms = new[] { new MechanismDefinition { Capability = id, Parameters = parameters } }
    };
    private static AttachmentDefinition Flashlight() => Definition() with
    {
        Id = "test.light", Slot = AttachmentSlot.Flashlight, Modifiers = Array.Empty<StatModifierDefinition>(),
        Flashlight = new FlashlightReference { DataPackId = "test.pack", SourcePartId = 4, SettingsId = 5 }
    };
}
