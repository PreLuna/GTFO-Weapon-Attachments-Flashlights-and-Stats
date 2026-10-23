using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;

internal static class Stage4Calculations
{
    private static string _root = "";
    private static int _number, _failed;
    private static readonly List<object> Results = new();
    private static readonly ConfigurationTarget Me = ConfigurationTarget.LocalPlayer;
    private static readonly WeaponIdentity Gun = new("test.pack", 1);
    private static int Main(string[] args)
    {
        _root = args[1]; Directory.CreateDirectory(_root);
        string originalCatalog = File.ReadAllText(args[2]);
        foreach (var descriptor in CapabilityRegistry.CreateDefaults().Stats.Values)
        {
            var copy = descriptor;
            Run("属性计算方向：" + copy.Id, () =>
            {
                var fixture = Open(Part("test.part", AttachmentSlot.Muzzle, copy.Id, 20));
                var draft = fixture.Manager.BeginEdit(Me, Gun); Select(draft, AttachmentSlot.Muzzle, "test.part");
                var result = Calculate(fixture, draft, new StatInput(copy.Id, 100, 50));
                Check(result.Success); Near(Row(result, copy.Id).Value, copy.BeneficialOperation == PercentOperation.IncreasePercent ? 120 : 80);
                Check(Row(result, copy.Id).BaseValue == 50 && Row(result, copy.Id).UpstreamValue == 100);
            });
        }
        Run("同属性永久百分比先求和而非逐件乘", () =>
        {
            var f = Open(Part("test.a", AttachmentSlot.Muzzle, "damage", 10), Part("test.b", AttachmentSlot.Barrel, "damage", 20));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            var r = Calculate(f, d, new StatInput("damage", 20)); Near(Row(r, "damage").Value, 26);
            Check(Row(r, "damage").Layers.Single().Contributions.Count == 2);
        });
        Run("读取上游新值并撤销时保留上游变化", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Check(d.Apply().Success);
            var source = new Source(20);
            Near(f.Calculator.Preview(d, source, "instance:a").Rows.Single().CurrentValue, 22);
            source.Damage = 30; source.Version++;
            var changed = f.Calculator.Preview(d, source, "instance:a");
            Near(changed.Rows.Single().CurrentValue, 33); Check(changed.Current!.UpstreamVersion == 2);
            d.ResetToOriginal(); var preview = f.Calculator.Preview(d, source, "instance:a");
            Near(preview.Rows.Single().CurrentValue, 33); Near(preview.Rows.Single().PreviewValue, 30);
            Check(d.Apply().Success); Near(f.Calculator.Preview(d, source, "instance:a").Rows.Single().CurrentValue, 30);
        });
        Run("反复预览和应用不产生重复倍率", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var source = new Source(20);
            for (int i = 0; i < 10; i++) { Near(f.Calculator.Preview(d, source, "instance:a").Rows.Single().PreviewValue, 22); Check(d.Apply().Success); }
            Check(source.Damage == 20 && f.Manager.Revision == 1);
        });
        Run("已经包含自身层的输入明确拒绝", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var snapshot = new UpstreamSnapshot(Me, Gun, "instance:a", "test.source", 1, new[] { new StatInput("damage", 22) }, new[] { ModifierCalculator.LayerId });
            var r = f.Calculator.Calculate(d.Snapshot, snapshot); Check(!r.Success && r.Issues.Any(i => i.Code == "OWN_LAYER_IN_UPSTREAM") && r.Stats.Count == 0);
        });
        Run("其他MOD层标识可保留且输入不会被改变", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var input = new UpstreamSnapshot(Me, Gun, "instance:a", "test.source", 1, new[] { new StatInput("damage", 20) }, new[] { "other.mod.layer" });
            Near(Row(f.Calculator.Calculate(d.Snapshot, input), "damage").Value, 22);
            Check(input.Values["damage"].UpstreamValue == 20 && input.AppliedLayerIds.Single() == "other.mod.layer");
        });
        Run("当前与草稿预览共用一次上游读取", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var source = new Source(20); var r = f.Calculator.Preview(d, source, "instance:a");
            Check(source.Reads == 1 && source.ExcludedLayer == ModifierCalculator.LayerId && r.Success);
            Near(r.Rows.Single().CurrentValue, 20); Near(r.Rows.Single().PreviewValue, 22);
        });
        Run("预览不写入配装存档或上游来源", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); string before = File.ReadAllText(f.Path);
            Select(d, AttachmentSlot.Muzzle, "test.a"); var source = new Source(20); f.Calculator.Preview(d, source, "instance:a");
            Check(File.ReadAllText(f.Path) == before && f.Manager.Revision == 0 && d.IsDirty && source.Damage == 20);
        });
        Run("四对象同枪的预览互不串配置", () =>
        {
            var parts = ConfigurationTarget.All.Select((_, i) => Part("test.p" + i, AttachmentSlot.Muzzle, "damage", 10 * (i + 1))).ToArray();
            var f = Open(parts);
            for (int i = 0; i < 4; i++)
            {
                var d = f.Manager.BeginEdit(ConfigurationTarget.All[i], Gun); Select(d, AttachmentSlot.Muzzle, "test.p" + i); Check(d.Apply().Success);
            }
            for (int i = 0; i < 4; i++)
            {
                var d = f.Manager.BeginEdit(ConfigurationTarget.All[i], Gun);
                var input = Snapshot(d, new StatInput("damage", 100)); Near(Row(f.Calculator.Calculate(d.Snapshot, input), "damage").Value, 110 + 10 * i);
            }
        });
        Run("不同武器和实例不能借用来源", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun);
            var source = new Source(20) { WrongWeapon = true };
            Check(!f.Calculator.Preview(d, source, "instance:a").Success);
            source.WrongWeapon = false; source.WrongInstance = true;
            Check(f.Calculator.Preview(d, source, "instance:a").Issues.Any(i => i.Code == "SOURCE_CONTEXT"));
        });
        Run("纯计算也拒绝跨对象快照", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun);
            var input = new UpstreamSnapshot(ConfigurationTarget.ForBot(1), Gun, "instance:a", "test.source", 1, new[] { new StatInput("damage", 20) });
            Check(f.Calculator.Calculate(d.Snapshot, input).Issues.Any(i => i.Code == "SOURCE_CONTEXT"));
        });
        Run("来源读取失败不生成假预览数值", () =>
        {
            var f = Open(); var d = f.Manager.BeginEdit(Me, Gun); var r = f.Calculator.Preview(d, new Source(20) { Throw = true }, "instance:a");
            Check(!r.Success && r.Rows.Count == 0 && r.Issues.Any(i => i.Code == "SOURCE_READ_FAILED"));
        });
        Run("换弹射速蓄力速度均按倒数缩短时间", () =>
        {
            foreach (string stat in new[] { "reload_speed", "fire_rate", "charge_speed" })
            {
                var f = Open(Part("test.a", AttachmentSlot.Muzzle, stat, 50)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
                var r = Calculate(f, d, new StatInput(stat, 2, 3, StatValueForm.DurationSeconds));
                Near(Row(r, stat).Value, 4.0 / 3); Check(Row(r, stat).Unit == "seconds");
            }
        });
        Run("伤害不允许伪装成秒数计算", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            Check(Calculate(f, d, new StatInput("damage", 20, Form: StatValueForm.DurationSeconds)).Issues.Any(i => i.Code == "VALUE_FORM"));
        });
        Run("零时间和未知表示类型被识别", () =>
        {
            var f = Open(); var d = f.Manager.BeginEdit(Me, Gun);
            Check(!Calculate(f, d, new StatInput("reload_speed", 0, Form: StatValueForm.DurationSeconds)).Success);
            Check(!Calculate(f, d, new StatInput("damage", 20, Form: (StatValueForm)99)).Success);
        });
        Run("永久与条件层按命名层合成", () =>
        {
            var aim = Condition("aiming"); var crouch = Condition("crouched");
            var f = Open(Part("test.a", AttachmentSlot.Muzzle, "damage", 10),
                Part("test.b", AttachmentSlot.Barrel, "damage", 20, aim), Part("test.c", AttachmentSlot.Stock, "damage", 30, crouch));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b"); Select(d, AttachmentSlot.Stock, "test.c");
            var states = Conditions((aim, true), (crouch, true));
            var r = f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("damage", 100)), states);
            Near(Row(r, "damage").Value, 171.6); Check(Row(r, "damage").Layers.Count == 3);
        });
        Run("同一个条件的多个配件先汇总百分比", () =>
        {
            var aim = Condition("aiming");
            var f = Open(Part("test.a", AttachmentSlot.Muzzle, "damage", 10, aim), Part("test.b", AttachmentSlot.Barrel, "damage", 20, aim));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            var r = f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("damage", 100)), Conditions((aim, true)));
            Near(Row(r, "damage").Value, 130); Check(Row(r, "damage").Layers.Count == 1);
        });
        Run("条件未知时明确标注且不假定触发", () =>
        {
            var f = Open(Part("test.a", condition: Condition("aiming"))); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("damage", 20));
            Near(Row(r, "damage").Value, 20); Check(r.Success && !r.ConditionsFullyKnown && r.Issues.Any(i => i.Code == "CONDITION_UNKNOWN"));
        });
        Run("已知未激活的条件保持上游", () =>
        {
            var aim = Condition("aiming"); var f = Open(Part("test.a", condition: aim)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("damage", 20)), Conditions((aim, false)));
            Near(Row(r, "damage").Value, 20); Check(r.ConditionsFullyKnown && !Row(r, "damage").Changed);
        });
        Run("always合并到永久层而不额外相乘", () =>
        {
            var f = Open(Part("test.a"), Part("test.b", AttachmentSlot.Barrel, "damage", 20, Condition("always")));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            Near(Row(Calculate(f, d, new StatInput("damage", 100)), "damage").Value, 130);
        });
        Run("同种条件不同参数拥有不同层和状态", () =>
        {
            var three = Condition("kills", 3); var five = Condition("kills", 5);
            var f = Open(Part("test.a", condition: three), Part("test.b", AttachmentSlot.Barrel, "damage", 20, five));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            var r = f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("damage", 100)), Conditions((three, true), (five, false)));
            Near(Row(r, "damage").Value, 110); Check(Row(r, "damage").Layers.Count == 2);
        });
        Run("条件层键不受系统数字语言影响", () =>
        {
            var before = CultureInfo.CurrentCulture;
            var condition = Condition("kills", 3) with { Parameters = new Dictionary<string, double> { ["count"] = 3, ["window_seconds"] = 1.5 } };
            string key = ConditionSnapshot.Key(condition);
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Check(ConditionSnapshot.Key(condition) == key); }
            finally { CultureInfo.CurrentCulture = before; }
        });
        Run("条件状态输入复制后不受外部字典修改", () =>
        {
            var aim = Condition("aiming"); var source = new Dictionary<string, bool> { [ConditionSnapshot.Key(aim)] = true };
            var snapshot = new ConditionSnapshot(source); source.Clear(); Check(snapshot.Evaluate(aim) == ConditionActivity.Active);
        });
        Run("减少比例求和限制到100且不会产生负值", () =>
        {
            var f = Open(Part("test.a", stat: "recoil", percent: 60), Part("test.b", AttachmentSlot.Barrel, "recoil", 60));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            var r = Calculate(f, d, new StatInput("recoil", 10)); Near(Row(r, "recoil").Value, 0);
            Check(r.Success && r.Issues.Any(i => i.Code == "REDUCTION_CAPPED") && Row(r, "recoil").Layers.Single().RequestedPercent == 120);
        });
        Run("减少型的永久与条件层各自合成", () =>
        {
            var aim = Condition("aiming");
            var f = Open(Part("test.a", stat: "recoil", percent: 20), Part("test.b", AttachmentSlot.Barrel, "recoil", 50, aim));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            Near(Row(f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("recoil", 10)), Conditions((aim, true))), "recoil").Value, 4);
        });
        Run("容量在所有层完成后只向下取整一次", () =>
        {
            var aim = Condition("aiming");
            var f = Open(Part("test.a", stat: "magazine_capacity", percent: 15), Part("test.b", AttachmentSlot.Barrel, "magazine_capacity", 15, aim));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            Near(Row(f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("magazine_capacity", 10)), Conditions((aim, true))), "magazine_capacity").Value, 13);
        });
        Run("容量使用十进制避免浮点边界少算一发", () =>
        {
            var f = Open(Part("test.a", stat: "magazine_capacity", percent: 13)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            Near(Row(Calculate(f, d, new StatInput("magazine_capacity", 1000)), "magazine_capacity").Value, 1130);
        });
        Run("同层小数百分比容量求和仍保留整数边界", () =>
        {
            var f = Open(Part("test.a", stat: "reserve_capacity", percent: 0.1), Part("test.b", AttachmentSlot.Barrel, "reserve_capacity", 0.2));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            Near(Row(Calculate(f, d, new StatInput("reserve_capacity", 1000)), "reserve_capacity").Value, 1003);
        });
        Run("不足一发的容量加成不会减少原容量", () =>
        {
            var f = Open(Part("test.a", stat: "magazine_capacity", percent: 10)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("magazine_capacity", 3)); Near(Row(r, "magazine_capacity").Value, 3); Check(!Row(r, "magazine_capacity").Changed);
        });
        Run("容量非整数基准不会被偷偷取整", () =>
        {
            var f = Open(); var d = f.Manager.BeginEdit(Me, Gun); var r = Calculate(f, d, new StatInput("magazine_capacity", 3.5));
            Check(!r.Success); Near(Row(r, "magazine_capacity").Value, 3.5);
        });
        Run("容量溢出保持上游并报告而不回绕", () =>
        {
            var f = Open(Part("test.a", stat: "reserve_capacity", percent: 100)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("reserve_capacity", int.MaxValue));
            Check(!r.Success && r.Issues.Any(i => i.Code == "CAPACITY_RANGE")); Near(Row(r, "reserve_capacity").Value, int.MaxValue);
        });
        Run("普通补给数值允许小数且不套容量取整", () =>
        {
            var f = Open(Part("test.a", stat: "ammo_refill", percent: 10)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            Near(Row(Calculate(f, d, new StatInput("ammo_refill", 12.5)), "ammo_refill").Value, 13.75);
        });
        Run("零命中半径不伪造固定加成", () =>
        {
            var f = Open(Part("test.a", stat: "hit_radius", percent: 50)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("hit_radius", 0));
            Check(!r.Success && r.Issues.Any(i => i.Code == "ZERO_BASELINE")); Near(Row(r, "hit_radius").Value, 0);
        });
        Run("空配装保留全部上游值含零基准", () =>
        {
            var f = Open(); var d = f.Manager.BeginEdit(Me, Gun);
            var r = Calculate(f, d, new StatInput("damage", 20), new StatInput("hit_radius", 0), new StatInput("magazine_capacity", 3), new StatInput("reload_speed", 2, Form: StatValueForm.DurationSeconds));
            Check(r.Success && r.Stats.All(s => !s.Changed && s.Value == s.UpstreamValue && s.OwnMultiplier == 1));
        });
        Run("缺少上游属性明确标记不可计算", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d);
            Check(!r.Success && Row(r, "damage").Value == null && r.Issues.Any(i => i.Code == "MISSING_UPSTREAM"));
        });
        Run("NaN无穷负上游值被识别且结果可序列化", () =>
        {
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1 })
            {
                var f = Open(); var d = f.Manager.BeginEdit(Me, Gun); var r = Calculate(f, d, new StatInput("damage", value));
                Check(!r.Success && r.Issues.Any(i => i.Code == "UPSTREAM_VALUE")); JsonSerializer.Serialize(r);
            }
        });
        Run("展示基础值无效时有效上游仍可计算", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("damage", 20, double.NaN)); Check(r.Success && Row(r, "damage").BaseValue == null);
            Near(Row(r, "damage").Value, 22);
        });
        Run("属性结果溢出不会采用部分倍率", () =>
        {
            var f = Open(Part("test.a", percent: 100)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("damage", double.MaxValue)); Check(!r.Success); Near(Row(r, "damage").Value, double.MaxValue);
        });
        Run("时间下溢为零时保持原时间", () =>
        {
            var f = Open(Part("test.a", stat: "reload_speed", percent: 100)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("reload_speed", double.Epsilon, Form: StatValueForm.DurationSeconds));
            Check(!r.Success && Row(r, "reload_speed").Value == double.Epsilon);
        });
        Run("同层百分比求和溢出可诊断并序列化", () =>
        {
            var f = Open(Part("test.a", percent: 1e308), Part("test.b", AttachmentSlot.Barrel, "damage", 1e308));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            var r = Calculate(f, d, new StatInput("damage", 20));
            Check(!r.Success && r.Issues.Any(i => i.Code == "PERCENT_OVERFLOW")); JsonSerializer.Serialize(r);
        });
        Run("条件层相乘溢出不生成无穷结果", () =>
        {
            var aim = Condition("aiming");
            var f = Open(Part("test.a", percent: 1e200), Part("test.b", AttachmentSlot.Barrel, "damage", 1e200, aim));
            var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a"); Select(d, AttachmentSlot.Barrel, "test.b");
            var r = f.Calculator.Calculate(d.Snapshot, Snapshot(d, new StatInput("damage", 20)), Conditions((aim, true)));
            Check(!r.Success && r.Issues.Any(i => i.Code == "MULTIPLIER_OVERFLOW")); JsonSerializer.Serialize(r);
        });
        Run("衰减起点超出终点时保持上游并标记", () =>
        {
            var f = Open(Part("test.a", stat: "falloff_start", percent: 200)); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var r = Calculate(f, d, new StatInput("falloff_start", 10), new StatInput("falloff_end", 20));
            Check(!r.Success && r.Issues.Any(i => i.Code == "FALLOFF_ORDER"));
            Near(Row(r, "falloff_start").Value, 10); Near(Row(r, "falloff_end").Value, 20);
        });
        Run("手电和穿甲机制单列待接入而不套伤害倍率", () =>
        {
            var mechanism = Part("test.armor") with { Modifiers = Array.Empty<StatModifierDefinition>(),
                Mechanisms = new[] { new MechanismDefinition { Capability = "armor_pierce", Parameters = new Dictionary<string, double> { ["ignored_armor_percent"] = 50 } } } };
            var flashlight = Part("test.light", AttachmentSlot.Flashlight) with { Modifiers = Array.Empty<StatModifierDefinition>(),
                Flashlight = new FlashlightReference { DataPackId = "test.pack", SourcePartId = 1 } };
            var f = Open(mechanism, flashlight); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.armor"); Select(d, AttachmentSlot.Flashlight, "test.light");
            var r = Calculate(f, d, new StatInput("damage", 20)); Near(Row(r, "damage").Value, 20);
            Check(r.DeferredEffects.Count == 2 && !r.GameplayApplied);
        });
        Run("删除的配件按原装预览且保留原因", () =>
        {
            var f = Open(); var fake = new LoadoutSnapshot(Me, Gun, 0, new[] { new SelectionState(AttachmentSlot.Muzzle, "test.absent", "test.absent", "test.absent", true, true, "AVAILABLE", Array.Empty<string>()) });
            var r = f.Calculator.Calculate(fake, new UpstreamSnapshot(Me, Gun, "instance:a", "test.source", 1, new[] { new StatInput("damage", 20) }));
            Near(Row(r, "damage").Value, 20); Check(r.Issues.Any(i => i.Code == "UNKNOWN_ATTACHMENT"));
        });
        Run("伪造适用状态不能绕过对象武器规则", () =>
        {
            var f = Open(Part("test.a") with { Applicability = new ApplicabilityDefinition { Targets = new[] { ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All } });
            var fake = new LoadoutSnapshot(Me, Gun, 0, new[] { new SelectionState(AttachmentSlot.Muzzle, "test.a", "test.a", "test.a", true, true, "AVAILABLE", Array.Empty<string>()) });
            var r = f.Calculator.Calculate(fake, new UpstreamSnapshot(Me, Gun, "instance:a", "test.source", 1, new[] { new StatInput("damage", 20) }));
            Near(Row(r, "damage").Value, 20); Check(r.Issues.Any(i => i.Code == "WRONG_TARGET"));
        });
        Run("重复槽位的快照不重复计算贡献", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); var snapshot = d.Snapshot with { Selections = d.Snapshot.Selections.Concat(d.Snapshot.Selections.Take(1)).ToArray() };
            Check(f.Calculator.Calculate(snapshot, Snapshot(d, new StatInput("damage", 20))).Issues.Any(i => i.Code == "SELECTION_STRUCTURE"));
        });
        Run("上游快照容器和计算结果容器不可原位修改", () =>
        {
            var f = Open(Part("test.a")); var d = f.Manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            var snapshot = Snapshot(d, new StatInput("damage", 20)); var r = f.Calculator.Calculate(d.Snapshot, snapshot);
            Throws<NotSupportedException>(() => ((IDictionary<string, StatInput>)snapshot.Values).Clear());
            Throws<NotSupportedException>(() => ((IList<CalculatedStat>)r.Stats).Clear());
            Throws<NotSupportedException>(() => ((IList<ModifierContribution>)r.Stats[0].Layers[0].Contributions).Clear());
        });
        Run("未知属性不冒充已经支持的计算规则", () =>
        {
            var f = Open(); var d = f.Manager.BeginEdit(Me, Gun);
            Check(Calculate(f, d, new StatInput("test.unknown", 20)).Issues.Any(i => i.Code == "UNKNOWN_STAT"));
        });
        Run("扩展属性可声明自身计算类型", () =>
        {
            var registry = CapabilityRegistry.CreateDefaults();
            registry.RegisterStat(new("test.custom", PercentOperation.IncreasePercent, "units"));
            var json = CatalogJson.Serialize(CatalogDocument.Empty() with { Attachments = new[] { Part("test.a", stat: "test.custom") } });
            var catalog = CatalogJson.Read(json, registry).Catalog!;
            var calc = new ModifierCalculator(catalog, registry); var manager = LoadoutManager.Open(catalog, new JsonLoadoutStorage(NewPath(), catalog.CatalogId));
            var d = manager.BeginEdit(Me, Gun); Select(d, AttachmentSlot.Muzzle, "test.a");
            Near(Row(calc.Calculate(d.Snapshot, Snapshot(d, new StatInput("test.custom", 20))), "test.custom").Value, 22);
        });
        Run("无效计算类型和容量负向规则不允许注册", () =>
        {
            var registry = CapabilityRegistry.CreateDefaults();
            Throws<ArgumentException>(() => registry.RegisterStat(new("test.bad", PercentOperation.IncreasePercent, "units", CalculationKind: (StatCalculationKind)99)));
            Throws<ArgumentException>(() => registry.RegisterStat(new("test.bad", PercentOperation.DecreasePercent, "rounds", CalculationKind: StatCalculationKind.Capacity)));
        });
        bool unchanged = File.ReadAllText(args[2]) == originalCatalog;
        Run("正式配件目录未被测试写入", () => Check(unchanged && CatalogJson.Read(originalCatalog, CapabilityRegistry.CreateDefaults()).Catalog!.CustomCount == 0));
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { Total = Results.Count, Passed = Results.Count - _failed, Failed = _failed,
            Cases = Results, ProductionCatalogChanged = !unchanged }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"计算测试：{Results.Count - _failed}/{Results.Count} 通过，失败 {_failed}。");
        return _failed == 0 ? 0 : 1;
    }
    private static void Run(string name, Action action)
    {
        try { action(); Results.Add(new { Name = name, Passed = true, Error = "" }); }
        catch (Exception error) { _failed++; Results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine(name + ": " + error.Message); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("验收断言失败。"); }
    private static void Near(double? actual, double expected)
        { Check(actual.HasValue && (actual.Value == expected || Math.Abs(actual.Value - expected) <= Math.Max(1e-12, Math.Abs(expected) * 1e-12))); }
    private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("缺少预期异常 " + typeof(T).Name); }
    private static CalculatedStat Row(CalculationResult result, string stat) => result.Stats.Single(row => row.Stat == stat);
    private static void Select(LoadoutDraft draft, AttachmentSlot slot, string id) => Check(draft.Select(slot, id).Success);
    private static ConditionDefinition Condition(string id, int? count = null) => new() { Id = id,
        Parameters = count.HasValue ? new Dictionary<string, double> { ["count"] = count.Value } : new Dictionary<string, double>() };
    private static ConditionSnapshot Conditions(params (ConditionDefinition Condition, bool Active)[] states) =>
        new(states.ToDictionary(item => ConditionSnapshot.Key(item.Condition), item => item.Active));
    private static AttachmentDefinition Part(string id, AttachmentSlot slot = AttachmentSlot.Muzzle, string stat = "damage",
        double percent = 10, ConditionDefinition? condition = null) => new()
    {
        Id = id, DisplayName = "计算验收临时定义", Slot = slot,
        Applicability = new ApplicabilityDefinition { Targets = new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot }, WeaponScope = WeaponScope.All },
        Modifiers = new[] { new StatModifierDefinition { Stat = stat,
            Operation = stat is "hip_spread" or "aim_spread" or "recoil" or "wake_up_radius" or "alert_radius" or "alert_buildup" ?
                PercentOperation.DecreasePercent : PercentOperation.IncreasePercent, Percent = percent, Condition = condition } }
    };
    private static string NewPath() { string dir = Path.Combine(_root, (++_number).ToString("D3")); Directory.CreateDirectory(dir); return Path.Combine(dir, "loadouts.json"); }
    private static Fixture Open(params AttachmentDefinition[] parts)
    {
        var registry = CapabilityRegistry.CreateDefaults();
        var catalog = CatalogJson.Read(CatalogJson.Serialize(CatalogDocument.Empty() with { Attachments = parts }), registry).Catalog ?? throw new Exception("测试目录无效。");
        string path = NewPath();
        return new(new ModifierCalculator(catalog, registry), LoadoutManager.Open(catalog, new JsonLoadoutStorage(path, catalog.CatalogId)), path);
    }
    private static UpstreamSnapshot Snapshot(LoadoutDraft draft, params StatInput[] values) => new(draft.Target, draft.Weapon, "instance:a", "test.source", 1, values);
    private static CalculationResult Calculate(Fixture fixture, LoadoutDraft draft, params StatInput[] values) =>
        fixture.Calculator.Calculate(draft.Snapshot, Snapshot(draft, values));
    private sealed record Fixture(ModifierCalculator Calculator, LoadoutManager Manager, string Path);
    private sealed class Source : IUpstreamStatSource
    {
        public double Damage; public long Version = 1; public int Reads; public string? ExcludedLayer;
        public bool Throw, WrongWeapon, WrongInstance;
        public Source(double value) { Damage = value; }
        public UpstreamSnapshot Read(UpstreamReadRequest request)
        {
            Reads++; ExcludedLayer = request.ExcludedLayerId;
            if (Throw) throw new IOException("测试来源暂不可读。");
            return new(request.Target, WrongWeapon ? new WeaponIdentity("test.pack", 2) : request.Weapon,
                WrongInstance ? "other.instance" : request.InstanceId, "test.source", Version, new[] { new StatInput("damage", Damage, 10) });
        }
    }
}
