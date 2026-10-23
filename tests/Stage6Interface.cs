using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Execution;
using PreLuna.WeaponAttachments.Interface;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;

internal static class Stage6Interface
{
    private static readonly ConfigurationTarget Me = ConfigurationTarget.LocalPlayer;
    private static readonly PanelWeapon A = new(new("test.pack", 1), "浏览武器 A"), B = new(new("test.pack", 2), "浏览武器 B");
    private static string _root = "";
    private static int _tests, _failures;
    private static readonly List<object> Results = new();
    private sealed class Port : IAttachmentPanelPort
    {
        public bool Host = true, ResourcesReady = true;
        public int CommittedCount;
        public double Upstream = 100;
        public string[] Caps = new[] { "stat.damage", "stat.magazine_capacity", "condition.always", "flashlight.original_profile" };
        public IReadOnlyList<PanelWeapon> Weapons => new[] { A, B };
        public PanelContext Read(ConfigurationTarget target, WeaponIdentity weapon) => new(new(target, weapon, "ui:" + target.StableKey + weapon.StableKey,
            "test.port", 1, new[] { new StatInput("damage", Upstream + (weapon.Equals(B.Identity) ? 100 : 0), 100), new StatInput("magazine_capacity", 10, 10) }),
            Caps, ConditionSnapshot.Unknown, target == Me || Host, "需要主机权限");
        public string? UnavailableResource(AttachmentDefinition definition) => definition.Flashlight != null && !ResourcesReady ? "资源加载中" : null;
        public void Committed(ConfigurationTarget target, WeaponIdentity weapon) => CommittedCount++;
    }
    private sealed record Fixture(AttachmentCatalog Catalog, LoadoutManager Manager, PanelController Panel, Port Port, string Path);
    private static Fixture Open(bool parts = true)
    {
        var registry = CapabilityRegistry.CreateDefaults();
        var definitions = parts ? new[] { Part("test.ten", 10), Part("test.twenty", 20), Part("test.mag", 50, AttachmentSlot.Magazine, "magazine_capacity") } : Array.Empty<AttachmentDefinition>();
        var catalog = CatalogJson.Read(CatalogJson.Serialize(CatalogDocument.Empty() with { Attachments = definitions }), registry).Catalog!;
        string path = Path.Combine(_root, Guid.NewGuid() + ".json");
        var manager = LoadoutManager.Open(catalog, new JsonLoadoutStorage(path, catalog.CatalogId)); var port = new Port();
        return new(catalog, manager, new(catalog, manager, new(catalog, registry), port), port, path);
    }
    private static AttachmentDefinition Part(string id, double percent, AttachmentSlot slot = AttachmentSlot.Muzzle, string stat = "damage") => new()
    {
        Id = id, DisplayName = "测试配件 " + percent, Slot = slot,
        Applicability = new() { Targets = new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All },
        Modifiers = new[] { new StatModifierDefinition { Stat = stat, Percent = percent, Operation = PercentOperation.IncreasePercent } }
    };
    private static double Value(PanelState state, bool draft = true, string stat = "damage") =>
        (draft ? state.Preview.Rows.First(row => row.Stat == stat).PreviewValue : state.Preview.Rows.First(row => row.Stat == stat).CurrentValue)!.Value;
    private static void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-7, $"Expected {expected}, got {actual}");
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void Run(string name, Action test)
    {
        _tests++; try { test(); Results.Add(new { Name = name, Success = true }); }
        catch (Exception error) { _failures++; Results.Add(new { Name = name, Success = false, Error = error.ToString() }); Console.WriteLine(name + ": " + error.Message); }
    }
    private static int Main(string[] args)
    {
        _root = args[1]; Directory.CreateDirectory(_root);
        Run("面板绑定浏览武器身份", () => { var f = Open(); f.Panel.Open(B); Check(f.Panel.Read().Weapon.Identity.Equals(B.Identity)); Near(Value(f.Panel.Read()), 200); });
        Run("正式空目录仍有十一槽原装项", () => { var f = Open(false); f.Panel.Open(A); Check(f.Panel.Read().Draft.Selections.Count == 11 && f.Panel.Read().Choices.Count == 1); });
        Run("百分比说明与条件中文化", () => { var text = PanelText.Describe(Part("test.label", 12.5)); Check(text.Contains("提高 12.5%") && text.Contains("常驻")); });
        Run("预览不写盘或执行", () => { var f = Open(); f.Panel.Open(A); string before = File.ReadAllText(f.Path); Check(f.Panel.Select("test.ten").Success); Near(Value(f.Panel.Read()), 110); Near(Value(f.Panel.Read(), false), 100); Check(File.ReadAllText(f.Path) == before && f.Port.CommittedCount == 0); });
        Run("应用后保存且更新当前列", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); Check(f.Panel.Apply().Success); Near(Value(f.Panel.Read(), false), 110); Check(f.Manager.Revision == 1 && !f.Panel.Read().Dirty); });
        Run("无变化应用不重复写盘", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); long revision = f.Manager.Revision; f.Panel.Apply(); Check(f.Manager.Revision == revision); });
        Run("取消恢复已提交配装", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); f.Panel.Select("test.twenty"); f.Panel.Cancel(); Near(Value(f.Panel.Read()), 110); Check(!f.Panel.Read().Dirty); });
        Run("恢复原装先预览不保存", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); f.Panel.Reset(); Near(Value(f.Panel.Read()), 100); Near(Value(f.Panel.Read(), false), 110); Check(f.Manager.Revision == 1); });
        Run("恢复原装应用后才持久化", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); f.Panel.Reset(); f.Panel.Apply(); Near(Value(f.Panel.Read(), false), 100); Check(f.Manager.Revision == 2); });
        Run("四对象草稿相互隔离", () => { var f = Open(); f.Panel.Open(A); foreach (var target in ConfigurationTarget.All) { f.Panel.SelectTarget(target); f.Panel.Select(target == Me ? "test.ten" : "test.twenty"); } Check(f.Panel.DirtyCount == 4); f.Panel.SelectTarget(Me); Near(Value(f.Panel.Read()), 110); f.Panel.SelectTarget(ConfigurationTarget.ForBot(2)); Near(Value(f.Panel.Read()), 120); });
        Run("同枪四对象独立保存", () => { var f = Open(); f.Panel.Open(A); foreach (var target in ConfigurationTarget.All) { f.Panel.SelectTarget(target); f.Panel.Select(target == Me ? "test.ten" : "test.twenty"); Check(f.Panel.Apply().Success); } Check(f.Manager.SavedLoadoutCount == 4); f.Panel.SelectTarget(Me); Near(Value(f.Panel.Read(), false), 110); });
        Run("换浏览武器保留各自草稿", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.SelectWeapon(B); f.Panel.Select("test.twenty"); Near(Value(f.Panel.Read()), 240); f.Panel.SelectWeapon(A); Near(Value(f.Panel.Read()), 110); Check(f.Panel.DirtyCount == 2); });
        Run("应用只提交当前对象武器", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.SelectWeapon(B); f.Panel.Select("test.twenty"); f.Panel.Apply(); Check(f.Manager.SavedLoadoutCount == 1 && f.Panel.DirtyCount == 1); });
        Run("非主机人机只读", () => { var f = Open(); f.Port.Host = false; f.Panel.Open(A, ConfigurationTarget.ForBot(1)); Check(!f.Panel.Read().CanApply && !f.Panel.Select("test.ten").Success); Check(!f.Panel.Apply().Success && f.Manager.Revision == 0); });
        Run("权限变化在应用时重新检查", () => { var f = Open(); f.Panel.Open(A, ConfigurationTarget.ForBot(1)); f.Panel.Select("test.ten"); f.Port.Host = false; Check(!f.Panel.Apply().Success && f.Manager.Revision == 0); });
        Run("缺失执行能力选项禁用", () => { var f = Open(); f.Port.Caps = Array.Empty<string>(); f.Panel.Open(A); Check(!f.Panel.Select("test.ten").Success); });
        Run("能力变化不保存新的不可执行选择", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Port.Caps = Array.Empty<string>(); Check(!f.Panel.Apply().Success && f.Manager.Revision == 0); });
        Run("瞄准镜明确预留", () => { var f = Open(); f.Panel.Open(A); f.Panel.SelectSlot(AttachmentSlot.Optic); Check(f.Panel.Read().Choices.All(value => !value.Enabled && value.Reason.Contains("预留"))); });
        Run("旧草稿拒绝覆盖新提交", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); var other = f.Manager.BeginEdit(Me, A.Identity); other.Select(AttachmentSlot.Muzzle, "test.twenty"); other.Apply(); Check(!f.Panel.Apply().Success); Near(Value(f.Panel.Read(), false), 120); });
        Run("旧草稿取消后可继续编辑", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); var other = f.Manager.BeginEdit(Me, A.Identity); other.Select(AttachmentSlot.Muzzle, "test.twenty"); other.Apply(); f.Panel.Cancel(); f.Panel.Select("test.ten"); Check(f.Panel.Apply().Success); });
        Run("上游更新同步当前与草稿", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); f.Panel.Select("test.twenty"); f.Port.Upstream = 200; Near(Value(f.Panel.Read(), false), 220); Near(Value(f.Panel.Read()), 240); });
        Run("未知条件不假装激活", () => { Check(PanelText.Condition(new() { Id = "kills", Parameters = new Dictionary<string,double> { ["count"] = 3, ["window_seconds"] = 5 } }).Contains("击杀 3 次 / 5 秒内")); });
        Run("关闭未应用面板需要确认", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); Check(!f.Panel.RequestClose() && f.Panel.CloseConfirmation && f.Panel.Opened); });
        Run("继续编辑保留未应用草稿", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.RequestClose(); f.Panel.ContinueEditing(); Check(!f.Panel.CloseConfirmation); Near(Value(f.Panel.Read()), 110); });
        Run("放弃关闭不保存任何草稿", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.SelectWeapon(B); f.Panel.Select("test.twenty"); f.Panel.DiscardAndClose(); Check(!f.Panel.Opened && f.Panel.DirtyCount == 0 && f.Manager.Revision == 0); });
        Run("无更改关闭直接返回", () => { var f = Open(); f.Panel.Open(A); Check(f.Panel.RequestClose() && !f.Panel.Opened); });
        Run("手电型号由原版系统选项生成", () => { var f = Open(false); AddLights(f.Catalog); f.Panel.Open(A); f.Panel.SelectSlot(AttachmentSlot.Flashlight); Check(f.Panel.Read().Choices.Count == 6 && f.Catalog.CustomCount == 0 && f.Catalog.SystemOptionCount == 5); Check(f.Panel.Select("system.flashlight.part_2").Success && f.Panel.Apply().Success); });
        Run("手电等待资源时不可选", () => { var f = Open(false); AddLights(f.Catalog); f.Port.ResourcesReady = false; f.Panel.Open(A); f.Panel.SelectSlot(AttachmentSlot.Flashlight); Check(!f.Panel.Select("system.flashlight.part_2").Success && f.Panel.Read().Choices.Any(value => value.Reason == "资源加载中")); });
        Run("手电资源就绪后自动可选", () => { var f = Open(false); AddLights(f.Catalog); f.Port.ResourcesReady = false; f.Panel.Open(A); f.Panel.SelectSlot(AttachmentSlot.Flashlight); f.Panel.Read(); f.Port.ResourcesReady = true; Check(f.Panel.Select("system.flashlight.part_2").Success); });
        Run("系统手电重启可解析同一稳定ID", () => { var f = Open(false); AddLights(f.Catalog); f.Panel.Open(A); f.Panel.SelectSlot(AttachmentSlot.Flashlight); f.Panel.Select("system.flashlight.part_4"); f.Panel.Apply(); var manager = LoadoutManager.Open(f.Catalog, new JsonLoadoutStorage(f.Path, f.Catalog.CatalogId)); Check(manager.GetCommitted(Me, A.Identity).Selections.Any(value => value.RequestedId == "system.flashlight.part_4")); });
        Run("容量预览统一取整", () => { var f = Open(); f.Panel.Open(A); f.Panel.SelectSlot(AttachmentSlot.Magazine); f.Panel.Select("test.mag"); Near(Value(f.Panel.Read(), true, "magazine_capacity"), 15); });
        Run("格式不允许伪装系统配件ID", () => { var result = CatalogJson.Read(CatalogJson.Serialize(CatalogDocument.Empty() with { Attachments = new[] { Part("system.fake", 10) } }), CapabilityRegistry.CreateDefaults()); Check(!result.Success); });
        Run("未知选择不改变草稿", () => { var f = Open(); f.Panel.Open(A); Check(!f.Panel.Select("missing.part").Success && !f.Panel.Read().Dirty); });
        Run("已保存但暂不可用选择保留并回退", () => { var f = Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); f.Port.Caps = new[] { "stat.magazine_capacity" }; Near(Value(f.Panel.Read(), false), 100); f.Panel.SelectSlot(AttachmentSlot.Magazine); Check(f.Panel.Select("test.mag").Success && f.Panel.Apply().Success); Check(f.Manager.GetCommitted(Me, A.Identity).Selections.Any(value => value.RequestedId == "test.ten")); });
        Run("用户数据中的加成方向描述正确", () => { var value = Part("test.recoil", 25) with { Modifiers = new[] { new StatModifierDefinition { Stat = "recoil", Operation = PercentOperation.DecreasePercent, Percent = 25 } } }; Check(PanelText.Describe(value).Contains("后坐力减少 25%")); });
        Run("数值格式不输出NaN或Infinity", () => { Check(PanelText.Value(double.NaN, "relative", StatValueForm.Direct) == "—" && PanelText.Value(null, "rounds", StatValueForm.Direct) == "—"); });
        Run("读取预览上游时嵌套排除自身倍率", () => { var f=Open(); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); var b=new RuntimeBinding(f.Catalog,f.Manager,new(f.Catalog,CapabilityRegistry.CreateDefaults()),Me,A.Identity,"preview",f.Port.Caps); Near(b.Calculate("damage",100,0),110); using(b.ExcludeOwnEffects()){Near(b.Calculate("damage",200,0),200); using(b.ExcludeOwnEffects()){Near(b.Calculate("damage",300,0),300);} Near(b.Calculate("damage",200,0),200);} Near(b.Calculate("damage",200,0),220); });
        Run("记录原装上游且不记录自身最终结果", () => { var f=Open(); var b=new RuntimeBinding(f.Catalog,f.Manager,new(f.Catalog,CapabilityRegistry.CreateDefaults()),Me,A.Identity,"preview",f.Port.Caps); b.Calculate("damage",75,0); Near(b.ObservedUpstream.Single().UpstreamValue,75); f.Panel.Open(A); f.Panel.Select("test.ten"); f.Panel.Apply(); Near(b.Calculate("damage",100,0),110); Near(b.ObservedUpstream.Single().UpstreamValue,100); });
        Run("未提交条件也按当前姿态预览且不执行草稿", () => { var f=Open(); var b=new RuntimeBinding(f.Catalog,f.Manager,new(f.Catalog,CapabilityRegistry.CreateDefaults()),Me,A.Identity,"preview",f.Port.Caps); var condition=new ConditionDefinition{Id="aiming"}; Check(b.Conditions(0,true,false,new[]{condition}).Evaluate(condition)==ConditionActivity.Active); Check(b.Conditions(0,false,false,new[]{condition}).Evaluate(condition)==ConditionActivity.Inactive); Near(b.Calculate("damage",100,0,true),100); });
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { Tests = _tests, Passed = _tests - _failures, Failed = _failures, Success = _failures == 0, Results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Stage6 interface: {_tests - _failures}/{_tests} passed."); return _failures == 0 ? 0 : 1;
    }
    private static void AddLights(AttachmentCatalog catalog) => typeof(AttachmentCatalog).GetMethod("SetSystemFlashlights", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(catalog,
        new object[] { "test.pack", new[] { (2u, "原版型号 A"), (3u, "原版型号 B"), (4u, "原版型号 C"), (5u, "原版型号 D"), (6u, "原版型号 E") } });
}
