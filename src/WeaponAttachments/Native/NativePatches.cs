using System;
using EWC.CustomWeapon;
using Gear;
using GameData;
using Player;
using PreLuna.WeaponAttachments.Execution;
using PreLuna.WeaponAttachments.Modifiers;
using UnityEngine;

namespace PreLuna.WeaponAttachments.Native;

internal static class NativePatches
{
    internal sealed class ShotFrame
    {
        public NativeBinding Binding = null!;
        public ShotFrame? Parent;
        public bool Started, Fired;
    }
    internal sealed record Scope(ShotFrame? Frame, bool Owns);
    [ThreadStatic] internal static ShotFrame? CurrentFrame;
    internal static NativeBinding? Wielded;
    private static NativeRuntime? Runtime => NativeRuntime.Current;
    private static Scope Enter(BulletWeapon gun)
    {
        var binding = Runtime?.Get(gun);
        if (binding == null) return new(null, false);
        binding.EnsureTail();
        if (CurrentFrame?.Binding == binding) return new(CurrentFrame, false);
        var frame = new ShotFrame { Binding = binding, Parent = CurrentFrame };
        CurrentFrame = frame;
        return new(frame, true);
    }
    private static void Exit(Scope? scope)
    {
        if (scope?.Owns != true || scope.Frame == null) return;
        try { scope.Frame.Binding.FinishShot(scope.Frame.Fired); }
        finally { CurrentFrame = scope.Frame.Parent; }
    }
    internal static void FirePrefix(BulletWeapon __instance, ref Scope? __state)
    {
        __state = Enter(__instance);
        if (__state.Frame is { Started: false } frame) { frame.Started = true; frame.Binding.Core.BeginShot(NativeBinding.Now); }
    }
    internal static void FirePostfix(Scope? __state, bool __runOriginal)
    {
        if (__state?.Frame != null) __state.Frame.Fired |= __runOriginal;
        Exit(__state);
    }
    internal static void ArchPrefix(BulletWeaponArchetype __instance, ref Scope? __state)
    {
        if (__instance.m_weapon != null) __state = Enter(__instance.m_weapon);
    }
    internal static void ArchPostfix(Scope? __state) => Exit(__state);
    internal static Exception? ShotFinalizer(Scope? __state, Exception? __exception)
    {
        if (__exception != null && __state?.Owns == true) { __state.Frame!.Fired = false; Exit(__state); }
        return __exception;
    }
    internal static void StartFiring(BulletWeaponArchetype __instance) => Runtime?.Get(__instance.m_weapon)?.EnsureTail();
    internal static void ClipSize(BulletWeapon __instance, ref int __result)
    {
        var binding = Runtime?.Get(__instance);
        if (binding != null) __result = (int)binding.Core.Calculate("magazine_capacity", __result, NativeBinding.Now, binding.Aiming, binding.Crouched);
    }
    // Native GetMaxClip delegates to ClipSize, so applying another factor here would double the capacity.
    internal static void MaxClip(BulletWeapon __instance) => Runtime?.Get(__instance)?.RefreshReserve();
    internal static void ReloadTime(BulletWeapon __instance, ref float __result)
    {
        var binding = Runtime?.Get(__instance);
        if (binding != null) __result = binding.Value("reload_speed", __result, StatValueForm.DurationSeconds);
    }
    internal static void Spawn(BulletWeapon __instance) => Runtime?.Get(__instance);
    internal static void Wield(BulletWeapon __instance)
    {
        var binding = Runtime?.Get(__instance); binding?.EnsureTail(); binding?.RefreshReserve();
        if (__instance.Owner?.IsLocallyOwned == true && __instance.Owner.Owner?.IsBot != true) Wielded = binding;
    }
    internal static void Unwield(BulletWeapon __instance)
    {
        if (Wielded?.Gun.Pointer == __instance.Pointer) Wielded = null;
    }
    internal static void Destroy(BulletWeapon __instance)
    {
        if (Wielded?.Gun.Pointer == __instance.Pointer) Wielded = null;
        Runtime?.Remove(__instance);
    }
    internal static void ShotDelay(BulletWeaponArchetype __instance, ref float __result)
    {
        var binding = Runtime?.Get(__instance.m_weapon);
        if (binding != null && (!Runtime!.EwcActive || __instance.m_weapon.GetComponent<CustomGunComponent>() == null))
            __result = binding.Value("fire_rate", __result, StatValueForm.DurationSeconds);
    }
    internal static void ChargeDelay(BulletWeaponArchetype __instance, ref float __result)
    {
        var binding = Runtime?.Get(__instance.m_weapon);
        if (binding != null && (!Runtime!.EwcActive || __instance.m_weapon.GetComponent<CustomGunComponent>() == null))
            __result = binding.Value("charge_speed", __result, StatValueForm.DurationSeconds);
    }
    internal static void Ray(ref Weapon.WeaponHitData weaponRayData)
    {
        var binding = CurrentFrame?.Binding;
        if (binding?.IsCurrent != true || (Runtime!.EwcActive && binding.Gun.GetComponent<CustomWeaponComponent>() != null)) return;
        weaponRayData.maxRayDist = binding.Value("max_range", weaponRayData.maxRayDist);
        var range = weaponRayData.damageFalloff;
        float from = binding.Value("falloff_start", range.x), to = binding.Value("falloff_end", range.y);
        if (from <= to) weaponRayData.damageFalloff = new(from, to);
        weaponRayData.randomSpread = binding.Value(binding.Aiming ? "aim_spread" : "hip_spread", weaponRayData.randomSpread);
    }
    internal static void VanillaHit(Weapon.WeaponHitData weaponRayData, bool doDamage)
    {
        var binding = CurrentFrame?.Binding;
        if (!doDamage || binding?.IsCurrent != true || (Runtime!.EwcActive && binding.Gun.GetComponent<CustomWeaponComponent>() != null)) return;
        weaponRayData.damage = binding.Value("damage", weaponRayData.damage);
        weaponRayData.precisionMulti = binding.Value("weakspot_multiplier", weaponRayData.precisionMulti);
        weaponRayData.staggerMulti = binding.Value("stagger_multiplier", weaponRayData.staggerMulti);
    }
    internal static void Recoil(FPS_RecoilSystem __instance, RecoilDataBlock recoilData)
    {
        var binding = Wielded;
        if (binding?.IsCurrent != true) return;
        float factor = binding.Value("recoil", 1);
        if (factor == 1) return;
        var delta = __instance.recoilDir * (factor - 1);
        var force = __instance.currentRecoilForce;
        force.x -= delta.x * (1 - recoilData.worldToViewSpaceBlendVertical);
        force.y -= delta.y * (1 - recoilData.worldToViewSpaceBlendHorizontal);
        var forceVp = __instance.currentRecoilForceVP;
        forceVp.x -= delta.x * recoilData.worldToViewSpaceBlendVertical;
        forceVp.y -= delta.y * recoilData.worldToViewSpaceBlendHorizontal;
        __instance.recoilDir *= factor; __instance.currentRecoilForce = force; __instance.currentRecoilForceVP = forceVp;
    }
    internal sealed record ArmorState(Dam_EnemyDamageLimb Limb, float Upstream, float Applied);
    internal static void ArmorPrefix(Dam_EnemyDamageLimb __instance, ref ArmorState? __state)
    {
        var binding = CurrentFrame?.Binding;
        if (binding?.IsCurrent != true) return;
        // EWC has already applied its armor trait and shred debuffs; only this hit receives our layer.
        float upstream = __instance.m_armorDamageMulti;
        float value = (float)binding.Core.ArmorMultiplier(upstream, NativeBinding.Now, binding.Aiming, binding.Crouched);
        if (value == upstream) return;
        __state = new(__instance, upstream, value);
        __instance.m_armorDamageMulti = value;
    }
    internal static void ArmorPostfix(ArmorState? __state)
    {
        if (__state != null && __state.Limb != null && __state.Limb.m_armorDamageMulti == __state.Applied)
            __state.Limb.m_armorDamageMulti = __state.Upstream;
    }
    internal static Exception? ArmorFinalizer(ArmorState? __state, Exception? __exception)
    {
        if (__exception != null) ArmorPostfix(__state);
        return __exception;
    }
    internal static void Refill(PlayerAmmoStorage __instance, AmmoType ammoType, ref float ammoAmount)
    {
        var binding = Runtime?.FromAmmo(__instance, ammoType);
        if (binding != null) { binding.RefreshReserve(); ammoAmount = binding.Value("ammo_refill", ammoAmount); }
    }
    internal static void ReserveRead(PlayerAmmoStorage __instance, AmmoType ammoType) => Runtime?.FromAmmo(__instance, ammoType)?.RefreshReserve();
    internal static void BackpackAmmo(PlayerBackpack __instance, Item item)
    {
        var gun = item?.TryCast<BulletWeapon>();
        if (gun != null) Runtime?.Get(gun)?.RefreshReserve();
    }
    internal static void CaptureLights() => Runtime?.Flashlights.CaptureProfiles();
    internal static void LightWieldPrefix(PlayerInventoryBase __instance, ItemEquippable wieldedItem, GearPartFlashlight flashlight, ref uint? __state)
    {
        __state = Runtime?.Flashlights.BeforeWield(__instance, wieldedItem, flashlight);
    }
    internal static void LightWieldPostfix(PlayerInventoryBase __instance, GearPartFlashlight flashlight, uint? __state)
    {
        if (__state.HasValue) flashlight.m_settingsID = __state.Value;
        Runtime?.Flashlights.AfterWield(__instance);
    }
    internal static Exception? LightWieldFinalizer(GearPartFlashlight flashlight, uint? __state, Exception? __exception)
    {
        if (__exception != null && __state.HasValue && flashlight != null) flashlight.m_settingsID = __state.Value;
        return __exception;
    }
    internal static void LightRange(Light __instance, ref float value) { var profile = Runtime?.Flashlights.ForLight(__instance); if (profile != null) value = profile.Range; }
    internal static void LightIntensity(Light __instance, ref float value) { var profile = Runtime?.Flashlights.ForLight(__instance); if (profile != null) value = profile.Intensity; }
    internal static void LightAngle(Light __instance, ref float value) { var profile = Runtime?.Flashlights.ForLight(__instance); if (profile != null) value = profile.Angle; }
    internal static void LightColor(Light __instance, ref Color value) { var profile = Runtime?.Flashlights.ForLight(__instance); if (profile != null) value = profile.Color; }
    internal static void LightCookie(Light __instance, ref Texture value) { var profile = Runtime?.Flashlights.ForLight(__instance); if (profile != null && (profile.Cookie != null || string.IsNullOrEmpty(profile.CookieName))) value = profile.Cookie!; }
}
