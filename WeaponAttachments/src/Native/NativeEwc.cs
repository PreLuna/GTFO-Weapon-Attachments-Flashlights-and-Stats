using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EWC.CustomWeapon;
using EWC.CustomWeapon.CustomShot;
using EWC.CustomWeapon.Enums;
using EWC.CustomWeapon.Properties;
using EWC.CustomWeapon.Properties.Traits;
using EWC.CustomWeapon.WeaponContext.Contexts;
using EWC.Utils;
using HarmonyLib;
using PreLuna.WeaponAttachments.Execution;
using UnityEngine;

namespace PreLuna.WeaponAttachments.Native;

internal static class NativeEwc
{
    private sealed record ContextOwner(NativeBinding Binding, string Stat);
    private static readonly ConditionalWeakTable<WeaponStackModContext, ContextOwner> Contexts = new();
    internal static void Track(WeaponStackModContext context, NativeBinding binding, string stat)
    {
        Contexts.Remove(context); Contexts.Add(context, new(binding, stat));
    }
    internal static void Install(NativeRuntime runtime)
    {
        void Patch(Type type, string name, string? prefix = null, string? postfix = null) =>
            runtime.Add(AccessTools.Method(type, name), prefix, postfix, patchType: typeof(NativeEwc));
        Patch(typeof(WeaponStatContext), "CalcMod", postfix: nameof(StatResult));
        Patch(typeof(WeaponStackModContext), "get_Value", postfix: nameof(StackResult));
        runtime.Add(AccessTools.Method(typeof(CustomShotComponent), "FireVanilla"), nameof(PreRay), nameof(PostRay),
            patchType: typeof(NativeEwc), finalizer: nameof(RayFinalizer));
        Patch(typeof(CustomWeaponComponent), "Register", postfix: nameof(Registered));
        Patch(typeof(CustomWeaponComponent), "Clear", postfix: nameof(Cleared));
        foreach (string name in new[] { "WakeUpRadius", "AlertRadius", "AlertAmount" })
            Patch(typeof(Silence), "get_" + name, postfix: nameof(Sound));
        Patch(typeof(ThickBullet), "get_HitSize", postfix: nameof(HitRadius));
    }
    private static void StatResult(WeaponStatContext __instance, StatType type, ref float __result)
    {
        var binding = NativeRuntime.Current?.FromComponent(__instance.ShotInfo.CWC);
        if (binding == null) return;
        string? stat = type switch { StatType.Damage => "damage", StatType.Precision => "weakspot_multiplier", StatType.Stagger => "stagger_multiplier", _ => null };
        if (stat != null) __result = binding.Value(stat, __result);
    }
    private static void StackResult(WeaponStackModContext __instance, ref float __result)
    {
        if (!Contexts.TryGetValue(__instance, out var context) || !context.Binding.IsCurrent) return;
        if (context.Stat == "fire_rate") __result = context.Binding.Value(context.Stat, __result * 60) / 60;
        else __result = context.Binding.Value(context.Stat, __result);
    }
    private sealed record RayState(HitData Data, float Range, (float start, float end) Falloff, float Spread,
        float AppliedRange, (float start, float end) AppliedFalloff, float AppliedSpread);
    private static void PreRay(HitData hitData, ref RayState? __state)
    {
        var binding = NativeRuntime.Current?.FromComponent(hitData.shotInfo.CWC) ?? NativePatches.CurrentFrame?.Binding;
        if (binding == null) return;
        float range = hitData.maxRayDist, spread = hitData.randomSpread;
        var falloff = hitData.damageFalloff;
        float newRange = binding.Value("max_range", range);
        float from = binding.Value("falloff_start", falloff.start), to = binding.Value("falloff_end", falloff.end);
        var newFalloff = from <= to ? (from, to) : falloff;
        float newSpread = binding.Value(binding.Aiming ? "aim_spread" : "hip_spread", spread);
        hitData.maxRayDist = newRange; hitData.damageFalloff = newFalloff; hitData.randomSpread = newSpread;
        __state = new(hitData, range, falloff, spread, newRange, newFalloff, newSpread);
    }
    private static void PostRay(RayState? __state)
    {
        if (__state == null) return;
        if (__state.Data.maxRayDist == __state.AppliedRange) __state.Data.maxRayDist = __state.Range;
        if (__state.Data.damageFalloff == __state.AppliedFalloff) __state.Data.damageFalloff = __state.Falloff;
        if (__state.Data.randomSpread == __state.AppliedSpread) __state.Data.randomSpread = __state.Spread;
    }
    private static Exception? RayFinalizer(RayState? __state, Exception? __exception)
    {
        if (__exception != null) PostRay(__state);
        return __exception;
    }
    private static void Registered(CustomWeaponComponent __instance) => NativeRuntime.Current?.FromComponent(__instance)?.EnsureTail(__instance);
    private static void Cleared(CustomWeaponComponent __instance) => NativeRuntime.Current?.FromComponent(__instance)?.EwcReset();
    private static void Sound(Silence __instance, System.Reflection.MethodBase __originalMethod, ref float __result)
    {
        var binding = NativeRuntime.Current?.FromComponent(__instance.CWC);
        if (binding == null) return;
        string stat = __originalMethod.Name switch { "get_WakeUpRadius" => "wake_up_radius", "get_AlertRadius" => "alert_radius", _ => "alert_buildup" };
        __result = binding.Value(stat, __result);
    }
    private static void HitRadius(ThickBullet __instance, ref float __result)
    {
        var binding = NativeRuntime.Current?.FromComponent(__instance.CWC);
        if (binding != null) __result = binding.Value("hit_radius", __result);
    }
}

internal sealed class NativeTail : WeaponPropertyBase,
    IWeaponProperty<WeaponFireRateContext>, IWeaponProperty<WeaponChargeSpeedContext>, IWeaponProperty<WeaponBackstabContext>,
    IWeaponProperty<WeaponHitDamageableContext>, IWeaponProperty<WeaponPostKillContext>
{
    private readonly NativeBinding _binding;
    public NativeTail(NativeBinding binding) { _binding = binding; }
    public void Invoke(WeaponFireRateContext context) { if (_binding.IsCurrent) NativeEwc.Track(context, _binding, "fire_rate"); }
    public void Invoke(WeaponChargeSpeedContext context) { if (_binding.IsCurrent) NativeEwc.Track(context, _binding, "charge_speed"); }
    public void Invoke(WeaponBackstabContext context) { if (_binding.IsCurrent) NativeEwc.Track(context, _binding, "backstab_multiplier"); }
    public void Invoke(WeaponHitDamageableContext context)
    {
        if (!_binding.IsCurrent || !context.DamageType.HasFlag(DamageType.Enemy) || !context.DamageType.HasFlag(DamageType.Bullet) || context.DamageType.HasFlag(DamageType.Dead)) return;
        _binding.Core.RecordHit(NativeBinding.Now, context.DamageType.HasFlag(DamageType.Weakspot));
        _binding.Signal(RuntimeEvent.Hit);
        if (context.DamageType.HasFlag(DamageType.Weakspot)) _binding.Signal(RuntimeEvent.WeakpointHit);
    }
    public void Invoke(WeaponPostKillContext context)
    {
        if (!_binding.IsCurrent || !context.DidLastHit) return;
        _binding.Core.RecordKill(NativeBinding.Now); _binding.Signal(RuntimeEvent.Kill);
    }
    public override void Serialize(Utf8JsonWriter writer)
    {
        // This property is registered directly on one instance and is never inserted into EWC's JSON or network tables.
        writer.WriteStartObject(); writer.WriteString("Name", "PreLunaRuntimeTail"); writer.WriteEndObject();
    }
}
