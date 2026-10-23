using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Execution;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;

internal static class Stage5Execution
{
    private static string _root = "";
    private static int _number, _failed;
    private static readonly List<object> Results = new();
    private static readonly ConfigurationTarget Me = ConfigurationTarget.LocalPlayer;
    private static readonly WeaponIdentity Gun = new("test.pack", 1);
    private static readonly string[] Caps = CapabilityRegistry.CreateDefaults().Stats.Keys.Select(id => "stat." + id)
        .Concat(new[] { "condition.always", "condition.aiming", "condition.crouched", "condition.shots_fired",
            "condition.consecutive_hits", "condition.weakpoint_hits", "condition.kills",
            "mechanism.armor_pierce", "mechanism.ammo_return_to_clip", "mechanism.ammo_return_to_reserve" }).ToArray();
    private static int Main(string[] args)
    {
        _root = args[1]; Directory.CreateDirectory(_root);
        Run("实例读取跟随上游且重复无倍率积累", () => {
            var f = Open(Part()); var b = Bind(f);
            Near(b.Calculate("damage", 20, 0), 22); Near(b.Calculate("damage", 30, 1), 33);
            for (int i = 0; i < 30; i++) Near(b.Calculate("damage", 30, 2), 33);
            Check(b.UpstreamVersion == 32);
        });
        Run("读单属性不会报其他属性上游缺失", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage"), Mod("magazine_capacity") } }); var b = Bind(f);
            Near(b.Calculate("damage", 20, 0), 22); Check(b.LastCalculationIssues.Count == 0);
        });
        Run("动态撤销保留新上游", () => {
            var f = Open(Part()); var b = Bind(f); Near(b.Calculate("damage", 20, 0), 22);
            var d = f.Manager.BeginEdit(Me, Gun); d.ResetToOriginal(); Check(d.Apply().Success);
            Near(b.Calculate("damage", 35, 1), 35);
        });
        Run("四对象同枪独立实例", () => {
            var f = Open(Part()); Apply(f, Me);
            foreach (var target in ConfigurationTarget.All) {
                var b = new RuntimeBinding(f.Catalog, f.Manager, f.Calculator, target, Gun, "instance:" + target.StableKey, Caps);
                Near(b.Calculate("damage", 100, 0), target == Me ? 110 : 100);
            }
        });
        Run("不同枪不会继承加成", () => {
            var f = Open(Part()); Bind(f);
            var b = new RuntimeBinding(f.Catalog, f.Manager, f.Calculator, Me, new("test.pack", 2), "instance:b", Caps);
            Near(b.Calculate("damage", 100, 0), 100);
        });
        Run("能力缺失整件停用且保留请求", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage"), Mod("hit_radius", 10, PercentOperation.IncreasePercent) } });
            Apply(f, Me);
            var b = new RuntimeBinding(f.Catalog, f.Manager, f.Calculator, Me, Gun, "instance:a", new[] { "stat.damage" });
            Near(b.Calculate("damage", 100, 0), 100); Check(b.UnavailableSelections.Count == 1);
            Check(f.Manager.GetCommitted(Me, Gun).Selections.Any(item => item.RequestedId == "test.part"));
            b.SetCapabilities(Caps); Near(b.Calculate("damage", 100, 0), 110);
        });
        Run("速度加成倒数转换原生时长", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("reload_speed", 50) } }); var b = Bind(f);
            Near(b.Calculate("reload_speed", 2, 0, form: StatValueForm.DurationSeconds), 4.0 / 3);
        });
        Run("容量最终取整", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("magazine_capacity", 13) } }); var b = Bind(f);
            Near(b.Calculate("magazine_capacity", 10, 0), 11);
        });
        foreach (string id in new[] { "aiming", "crouched" }) Run("姿态条件：" + id, () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition(id) } } }); var b = Bind(f);
            Near(b.Calculate("damage", 100, 0), 100);
            Near(b.Calculate("damage", 100, 1, aiming: id == "aiming", crouched: id == "crouched"), 110);
        });
        Run("累计射击计数和取消射击", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("shots_fired", 2) } } }); var b = Bind(f);
            b.BeginShot(1); b.FinishShot(); Near(b.Calculate("damage", 100, 1), 100);
            b.BeginShot(2); b.CancelShot(); Near(b.Calculate("damage", 100, 2), 100);
            b.BeginShot(3); b.FinishShot(); Near(b.Calculate("damage", 100, 3), 110);
        });
        Run("重复取消只撤回同一次射击", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("shots_fired", 2) } } }); var b = Bind(f);
            b.BeginShot(1); b.FinishShot(); b.BeginShot(2); b.CancelShot(); b.CancelShot();
            b.BeginShot(3); b.FinishShot(); Near(b.Calculate("damage", 100, 3), 110);
        });
        Run("同修订号重新载入也刷新实例选择", () => {
            var f = Open(Part()); var b = Bind(f); long revision = f.Manager.Revision;
            Near(b.Calculate("damage", 100, 0), 110);
            File.WriteAllText(f.Path, LoadoutJson.Serialize(f.Manager.Export() with { Loadouts = Array.Empty<SavedLoadout>() }));
            f.Manager.Reload(); Check(f.Manager.Revision == revision);
            Near(b.Calculate("damage", 100, 1), 100);
        });
        Run("连续命中遇未命中重置", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("consecutive_hits", 2) } } }); var b = Bind(f);
            Hit(b, 1); Hit(b, 2); Near(b.Calculate("damage", 100, 2), 110);
            b.BeginShot(3); b.FinishShot(); Near(b.Calculate("damage", 100, 3), 100);
        });
        Run("霰弹同一发命中与弱点只计一次", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("weakpoint_hits", 2) } } }); var b = Bind(f);
            b.BeginShot(1); b.RecordHit(1, false); long hit = b.LastEventSerial(RuntimeEvent.Hit);
            b.RecordHit(1, true); long weak = b.LastEventSerial(RuntimeEvent.WeakpointHit);
            for (int i = 0; i < 10; i++) b.RecordHit(1, true);
            Check(b.LastEventSerial(RuntimeEvent.Hit) == hit && b.LastEventSerial(RuntimeEvent.WeakpointHit) == weak);
            b.FinishShot(); Near(b.Calculate("damage", 100, 1), 100); Hit(b, 2, true); Near(b.Calculate("damage", 100, 2), 110);
        });
        foreach (string id in new[] { "shots_fired", "weakpoint_hits", "kills", "consecutive_hits" })
            Run("时间窗口进出：" + id, () => {
                var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition(id, 2, 3) } } }); var b = Bind(f);
                Hit(b, 1, true); b.RecordKill(1); Hit(b, 2, true); b.RecordKill(2);
                Near(b.Calculate("damage", 100, 2), 110); Near(b.Calculate("damage", 100, 6), 100);
            });
        Run("其他对象修改不重置本枪条件", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("shots_fired", 2) } } }); var b = Bind(f);
            Hit(b, 1); Hit(b, 2); Apply(f, ConfigurationTarget.ForBot(1));
            Near(b.Calculate("damage", 100, 3), 110);
        });
        Run("时钟倒退清理旧关卡计数", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("shots_fired", 2) } } }); var b = Bind(f);
            Hit(b, 10); Hit(b, 11); Near(b.Calculate("damage", 100, 11), 110); Near(b.Calculate("damage", 100, 1), 100);
        });
        Run("显式实例恢复清理触发", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("kills", 1) } } }); var b = Bind(f);
            b.RecordKill(1); Near(b.Calculate("damage", 100, 1), 110); b.ResetTriggers(); Near(b.Calculate("damage", 100, 2), 100);
        });
        Run("大窗口计数拒绝而非静默失效", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("shots_fired", 8193, 10) } } }); var b = Bind(f);
            Check(!b.HasSelection && b.UnavailableSelections.Single().Contains("TIMED_COUNT_LIMIT"));
        });
        Run("永久计数不受窗口历史上限影响", () => {
            var f = Open(Part() with { Modifiers = new[] { Mod("damage") with { Condition = Condition("shots_fired", 8200) } } }); var b = Bind(f);
            for (int i = 0; i < 8200; i++) { b.BeginShot(i); b.FinishShot(); }
            Near(b.Calculate("damage", 100, 8200), 110);
        });
        Run("穿甲只改变这次伤害的装甲倍率", () => {
            var f = Open(Part() with { Modifiers = Array.Empty<StatModifierDefinition>(), Mechanisms = new[] { Mechanism("armor_pierce", 50) } }); var b = Bind(f);
            Near(b.ArmorMultiplier(0.2, 0), 0.6); Near(b.ArmorMultiplier(0, 0), 0.5);
            Near(b.ArmorMultiplier(1, 0), 1); Near(b.ArmorMultiplier(2, 0), 2); Near(b.ArmorMultiplier(-1, 0), -1);
        });
        Run("多件穿甲在剩余减伤上合成", () => {
            var a = Part() with { Modifiers = Array.Empty<StatModifierDefinition>(), Mechanisms = new[] { Mechanism("armor_pierce", 50) } };
            var f = Open(a, a with { Id = "test.second", Slot = AttachmentSlot.Barrel }); var b = Bind(f);
            var d = f.Manager.BeginEdit(Me, Gun); Check(d.Select(AttachmentSlot.Barrel, "test.second").Success && d.Apply().Success);
            Near(b.ArmorMultiplier(0.2, 0), 0.8);
        });
        foreach (string mechanism in new[] { "ammo_return_to_clip", "ammo_return_to_reserve" })
            Run("返弹容量上限及事件幂等：" + mechanism, () => {
                var f = Open(Part() with { Modifiers = Array.Empty<StatModifierDefinition>(), Mechanisms = new[] { Mechanism(mechanism, 5) } }); var b = Bind(f);
                var ammo = new Ammo { Clip = 9, Reserve = 19 }; b.BeginShot(1);
                Check(b.ReturnAmmo(RuntimeEvent.Shot, b.ShotSerial, ammo, 1, false, false, () => 0) == 1);
                Check(b.ReturnAmmo(RuntimeEvent.Shot, b.ShotSerial, ammo, 1, false, false, () => 0) == 0);
                Check(ammo.Clip <= 10 && ammo.Reserve <= 20);
            });
        Run("概率边界和无免费重试", () => {
            var m = Mechanism("ammo_return_to_clip", 1) with { Parameters = new Dictionary<string, double> { ["rounds"] = 1, ["chance_percent"] = 50 } };
            var f = Open(Part() with { Modifiers = Array.Empty<StatModifierDefinition>(), Mechanisms = new[] { m } }); var b = Bind(f); var ammo = new Ammo();
            b.BeginShot(1); Check(b.ReturnAmmo(RuntimeEvent.Shot, b.ShotSerial, ammo, 1, false, false, () => .5) == 0);
            Check(b.ReturnAmmo(RuntimeEvent.Shot, b.ShotSerial, ammo, 1, false, false, () => 0) == 0);
            b.BeginShot(2); Check(b.ReturnAmmo(RuntimeEvent.Shot, b.ShotSerial, ammo, 2, false, false, () => .49) == 1);
        });
        Run("延迟多击杀返弹以各自事件计数取模", () => {
            var f = Open(Part() with { Modifiers = Array.Empty<StatModifierDefinition>(),
                Mechanisms = new[] { Mechanism("ammo_return_to_clip", 1) with { Condition = Condition("kills", 2) } } }); var b = Bind(f); var ammo = new Ammo();
            b.RecordKill(1); long first = b.EventSerial; b.RecordKill(1); long second = b.EventSerial;
            Check(b.ReturnAmmo(RuntimeEvent.Kill, first, ammo, 1, false, false, () => 0) == 0);
            Check(b.ReturnAmmo(RuntimeEvent.Kill, second, ammo, 1, false, false, () => 0) == 1);
        });
        Run("相同命中多次信号不重复返弹", () => {
            var f = Open(Part() with { Modifiers = Array.Empty<StatModifierDefinition>(),
                Mechanisms = new[] { Mechanism("ammo_return_to_clip", 1) with { Condition = Condition("consecutive_hits", 1) } } }); var b = Bind(f); var ammo = new Ammo();
            b.BeginShot(1); b.RecordHit(1, false); long id = b.LastEventSerial(RuntimeEvent.Hit); b.RecordHit(1, true);
            Check(b.ReturnAmmo(RuntimeEvent.Hit, id, ammo, 1, false, false, () => 0) == 1);
            Check(b.ReturnAmmo(RuntimeEvent.Hit, b.LastEventSerial(RuntimeEvent.Hit), ammo, 1, false, false, () => 0) == 0);
        });
        Run("未命中不会触发命中返弹", () => {
            var f = Open(Part() with { Modifiers = Array.Empty<StatModifierDefinition>(),
                Mechanisms = new[] { Mechanism("ammo_return_to_clip", 1) with { Condition = Condition("consecutive_hits", 1) } } }); var b = Bind(f); var ammo = new Ammo();
            b.BeginShot(1); b.FinishShot(); Check(b.ReturnAmmo(RuntimeEvent.Shot, b.ShotSerial, ammo, 1, false, false, () => 0) == 0);
        });
        Run("撤销只还原自身字段写入", () => {
            var owned = new OwnedValue(); double value = owned.Apply(100, v => v * 1.5); Near(value, 150);
            for (int i = 0; i < 30; i++) value = owned.Apply(value, v => v * 1.5);
            Near(value, 150); Near(owned.Withdraw(value), 100);
        });
        Run("新的外部字段写入成为上游", () => {
            var owned = new OwnedValue(); owned.Apply(100, v => v * 1.5);
            Near(owned.Apply(200, v => v * 1.5), 300); Near(owned.Withdraw(300), 200);
        });
        Run("外部抢写后撤销保留对方值", () => {
            var owned = new OwnedValue(); owned.Apply(100, v => v * 1.5); Near(owned.Withdraw(250), 250);
        });
        Run("字段撤销和重新应用幂等", () => {
            var owned = new OwnedValue(); Near(owned.Apply(100, v => v * 1.5), 150);
            Near(owned.Apply(150, v => v), 100); Near(owned.Withdraw(100), 100); Near(owned.Apply(100, v => v * 2), 200);
        });
        Run("非有限实例写入拒绝", () => {
            bool threw = false; try { new OwnedValue().Apply(100, _ => double.NaN); } catch (ArgumentException) { threw = true; } Check(threw);
        });
        Run("原生浮点字段反复写入不积累舍入误差", () => {
            var owned = new OwnedValue(); float value = 100.1f;
            for (int i = 0; i < 100; i++) value = owned.ApplySingle(value, baseline => baseline + 0.2);
            Check(value == (float)(100.1f + 0.2)); Check((float)owned.Withdraw(value) == 100.1f);
        });
        Run("原生浮点字段外部更新成为新上游", () => {
            var owned = new OwnedValue(); owned.ApplySingle(100.1f, baseline => baseline + 0.2);
            float value = owned.ApplySingle(200.1f, baseline => baseline + 0.2);
            Check(value == (float)(200.1f + 0.2)); Check((float)owned.Withdraw(value) == 200.1f);
        });
        Run("超出原生浮点范围的写入拒绝", () => {
            bool threw = false; try { new OwnedValue().ApplySingle(1, _ => double.MaxValue); } catch (ArgumentException) { threw = true; } Check(threw);
        });
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { Success = _failed == 0, Tests = _number, Passed = _number - _failed, Failed = _failed, Results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Stage5 execution: {_number - _failed}/{_number} passed."); return _failed == 0 ? 0 : 1;
    }
    private sealed record Fixture(AttachmentCatalog Catalog, LoadoutManager Manager, ModifierCalculator Calculator, string Path);
    private static Fixture Open(params AttachmentDefinition[] parts)
    {
        var registry = CapabilityRegistry.CreateDefaults();
        var read = CatalogJson.Read(CatalogJson.Serialize(CatalogDocument.Empty() with { Attachments = parts }), registry);
        Check(read.Success, string.Join(";", read.Issues.Select(i => i.Code + ":" + i.Message)));
        var catalog = read.Catalog!; string path = Path.Combine(_root, Guid.NewGuid() + ".json");
        return new(catalog, LoadoutManager.Open(catalog, new JsonLoadoutStorage(path, catalog.CatalogId)), new ModifierCalculator(catalog, registry), path);
    }
    private static void Apply(Fixture f, ConfigurationTarget target)
    {
        var d = f.Manager.BeginEdit(target, Gun); Check(d.Select(AttachmentSlot.Muzzle, "test.part").Success); Check(d.Apply().Success);
    }
    private static RuntimeBinding Bind(Fixture f) { Apply(f, Me); return new(f.Catalog, f.Manager, f.Calculator, Me, Gun, "instance:a", Caps); }
    private static AttachmentDefinition Part() => new() { Id = "test.part", DisplayName = "临时开发定义", Slot = AttachmentSlot.Muzzle,
        Applicability = new() { Targets = new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All }, Modifiers = new[] { Mod("damage") } };
    private static StatModifierDefinition Mod(string stat, double percent = 10, PercentOperation operation = PercentOperation.IncreasePercent) => new() { Stat = stat, Percent = percent, Operation = operation };
    private static ConditionDefinition Condition(string id, int? count = null, double? window = null)
    {
        var parameters = new Dictionary<string, double>(); if (count.HasValue) parameters["count"] = count.Value; if (window.HasValue) parameters["window_seconds"] = window.Value;
        return new() { Id = id, Parameters = parameters };
    }
    private static MechanismDefinition Mechanism(string capability, double value) => new() { Capability = capability,
        Parameters = new Dictionary<string, double> { [capability == "armor_pierce" ? "ignored_armor_percent" : "rounds"] = value } };
    private static void Hit(RuntimeBinding b, double time, bool weak = false) { b.BeginShot(time); b.RecordHit(time, weak); b.FinishShot(); }
    private static void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-8, $"Expected {expected}, got {actual}");
    private static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Run(string name, Action test)
    {
        _number++; try { test(); Results.Add(new { Name = name, Success = true }); }
        catch (Exception error) { _failed++; Results.Add(new { Name = name, Success = false, Error = error.ToString() }); Console.WriteLine(name + ": " + error.Message); }
    }
    private sealed class Ammo : IAmmoEndpoint
    {
        public int Clip { get; set; } public int ClipCapacity => 10;
        public double Reserve { get; set; } public int ReserveCapacity => 20;
    }
}
