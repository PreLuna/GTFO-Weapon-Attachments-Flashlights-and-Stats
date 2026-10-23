using System;
using System.Globalization;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Modifiers;

namespace PreLuna.WeaponAttachments.Interface;

public static class PanelText
{
    public static string Stat(string id) => id switch
    {
        "damage" => "伤害", "weakspot_multiplier" => "弱点倍率", "stagger_multiplier" => "硬直倍率",
        "max_range" => "最大射程", "falloff_start" => "衰减起点", "falloff_end" => "衰减终点",
        "fire_rate" => "射速", "reload_speed" => "换弹时间", "charge_speed" => "蓄力速度", "backstab_multiplier" => "背刺倍率",
        "magazine_capacity" => "弹匣容量", "reserve_capacity" => "储备上限", "ammo_refill" => "弹药补给",
        "hip_spread" => "腰射散布", "aim_spread" => "瞄准散布", "recoil" => "后坐力", "hit_radius" => "命中半径",
        "wake_up_radius" => "惊醒范围", "alert_radius" => "警觉范围", "alert_buildup" => "警觉累积", _ => id
    };
    public static string Target(ConfigurationTarget target) => target.Kind == ConfigurationTargetKind.LocalPlayer ? "我" : "人机 " + target.BotSlot;
    public static string Condition(ConditionDefinition? value) => value?.Id switch
    {
        null or "always" => "常驻", "aiming" => "瞄准时", "crouched" => "蹲下时",
        "shots_fired" => "射击", "consecutive_hits" => "连续命中", "weakpoint_hits" => "弱点命中", "kills" => "击杀", _ => "条件增益"
    } + (value?.Parameters.TryGetValue("count", out var count) == true ? " " + count.ToString("0", CultureInfo.InvariantCulture) + " 次" : "")
      + (value?.Parameters.TryGetValue("window_seconds", out var window) == true ? " / " + window.ToString("0.##", CultureInfo.InvariantCulture) + " 秒内" : "");
    public static string Describe(AttachmentDefinition definition)
    {
        if (definition.Id.StartsWith("original.", StringComparison.Ordinal)) return "保留原装配置";
        if (definition.Flashlight != null) return "选用原版照明与光斑，保留外壳与挂点";
        return string.Join("；", definition.Modifiers.Select(value =>
            (value.Stat == "reload_speed" ? "换弹速度" : Stat(value.Stat)) + (value.Operation == PercentOperation.DecreasePercent ? "减少 " : "提高 ") +
            value.Percent!.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%（" + Condition(value.Condition) + "）")
            .Concat(definition.Mechanisms.Select(value => (value.Capability switch
            { "armor_pierce" => "忽略剩余装甲减伤", "ammo_return_to_clip" => "返弹至弹匣", _ => "返弹至储备" }) + "（" + Condition(value.Condition) + "）")));
    }
    public static string Value(double? value, string unit, StatValueForm form) => value.HasValue && double.IsFinite(value.Value) ?
        value.Value.ToString(unit == "rounds" ? "0" : "0.##", CultureInfo.InvariantCulture) +
        (form == StatValueForm.DurationSeconds ? " 秒" : unit switch { "meters" => " 米", "rounds" => " 发", "rounds_per_minute" => " /分", _ => "" }) : "—";
}
