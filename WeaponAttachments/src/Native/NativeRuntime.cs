using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using EWC.CustomWeapon;
using EWC.API;
using EWC.CustomWeapon.ComponentWrapper.WeaponComps;
using EWC.CustomWeapon.Properties.Traits;
using GameData;
using Gear;
using GTFO.API;
using HarmonyLib;
using Player;
using SNetwork;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Execution;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;
using PreLuna.WeaponAttachments.Ownership;
using UnityEngine;

namespace PreLuna.WeaponAttachments.Native;

public sealed record NativePatchInfo(string Target, string Kind, int Order, string Owner, string Method,
    int Priority, IReadOnlyList<string> Before, IReadOnlyList<string> After);

public sealed partial class NativeRuntime : IDisposable
{
    public static NativeRuntime? Current { get; private set; }
    private readonly Harmony _harmony = new("PreLuna.WeaponAttachments.Native");
    internal readonly AttachmentCatalog Catalog;
    internal readonly LoadoutManager Loadouts;
    internal readonly ModifierCalculator Calculator;
    internal readonly ManualLogSource Log;
    private readonly Dictionary<IntPtr, NativeBinding> _bindings = new();
    private readonly HashSet<IntPtr> _detaching = new();
    private readonly List<string> _patches = new();
    private readonly List<string> _errors = new();
    private readonly ConfigEntry<string> _dataPack;
    private readonly Dictionary<uint, GearIDRange> _offline = new();
    public bool EwcActive { get; }
    public IReadOnlyList<string> InstalledPatches => _patches.AsReadOnly();
    public IReadOnlyList<string> Errors => _errors.AsReadOnly();
    public int BoundInstances => _bindings.Count;
    public string DataPackId => _dataPack.Value;
    public NativeFlashlights Flashlights { get; }
    public IReadOnlyList<NativePatchInfo> GetPatchOrder()
    {
        var result = new List<NativePatchInfo>();
        foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null || !info.Owners.Contains(_harmony.Id)) continue;
            foreach (var (kind, patches) in new[] { ("Prefix", info.Prefixes), ("Postfix", info.Postfixes), ("Finalizer", info.Finalizers) })
            {
                var sorted = PatchProcessor.GetSortedPatchMethods(method, patches.ToArray());
                for (int i = 0; i < sorted.Count; i++)
                {
                    var patch = patches.First(item => item.PatchMethod == sorted[i]);
                    result.Add(new(method.DeclaringType!.FullName + "." + method.Name, kind, i, patch.owner,
                        sorted[i].DeclaringType!.FullName + "." + sorted[i].Name, patch.priority,
                        Array.AsReadOnly(patch.before), Array.AsReadOnly(patch.after)));
                }
            }
        }
        return result.AsReadOnly();
    }
    internal static readonly string[] StandardCapabilities = new[]
    {
        "stat.damage","stat.weakspot_multiplier","stat.stagger_multiplier","stat.falloff_start","stat.falloff_end","stat.max_range",
        "stat.fire_rate","stat.reload_speed","stat.magazine_capacity","stat.reserve_capacity","stat.ammo_refill","stat.hip_spread","stat.aim_spread","stat.recoil",
        "condition.always","condition.crouched","condition.aiming","condition.consecutive_hits","condition.weakpoint_hits","condition.kills","condition.shots_fired",
        "mechanism.ammo_return_to_clip","mechanism.ammo_return_to_reserve","flashlight.original_profile"
    };
    public NativeRuntime(AttachmentCatalog catalog, LoadoutManager loadouts, ModifierCalculator calculator,
        ConfigFile configuration, ManualLogSource log, bool ewcActive)
    {
        Catalog = catalog; Loadouts = loadouts; Calculator = calculator; Log = log; EwcActive = ewcActive;
        _dataPack = configuration.Bind("Runtime", "DataPackId", "gtfo.current",
            "当前数据包的稳定身份。切换使用相同武器ID的不同数据包时请使用不同标识；不使用显示名称作为武器键。");
        if (!DefinitionIds.IsValid(_dataPack.Value)) throw new ArgumentException("Runtime.DataPackId 无效。");
        Flashlights = new(this);
    }
    public void Install()
    {
        Current = this;
        Add(typeof(BulletWeapon), "get_ClipSize", postfix: nameof(NativePatches.ClipSize));
        Add(typeof(BulletWeapon), nameof(BulletWeapon.GetMaxClip), postfix: nameof(NativePatches.MaxClip));
        Add(typeof(BulletWeapon), "get_ReloadTime", postfix: nameof(NativePatches.ReloadTime));
        Add(typeof(BulletWeapon), nameof(BulletWeapon.OnGearSpawnComplete), postfix: nameof(NativePatches.Spawn));
        Add(typeof(BulletWeapon), nameof(BulletWeapon.OnWield), postfix: nameof(NativePatches.Wield));
        Add(typeof(BulletWeapon), nameof(BulletWeapon.OnUnWield), postfix: nameof(NativePatches.Unwield));
        Add(typeof(BulletWeapon), nameof(BulletWeapon.OnDestroy), prefix: nameof(NativePatches.Destroy));
        foreach (var type in new[] { typeof(BulletWeapon), typeof(BulletWeaponSynced), typeof(Shotgun), typeof(ShotgunSynced) })
            Add(type, "Fire", nameof(NativePatches.FirePrefix), nameof(NativePatches.FirePostfix), true, finalizer: nameof(NativePatches.ShotFinalizer));
        foreach (string name in new[] { "Gear.BWA_Auto", "Gear.BWA_Burst", "Gear.BWA_Semi" })
            Add(typeof(BulletWeapon).Assembly.GetType(name)!, "OnFireShot", nameof(NativePatches.ArchPrefix), nameof(NativePatches.ArchPostfix), true, finalizer: nameof(NativePatches.ShotFinalizer));
        foreach (var type in new[] { typeof(BulletWeaponArchetype), typeof(BWA_Auto), typeof(BWA_Burst) })
            Add(type, "OnStartFiring", nameof(NativePatches.StartFiring), early: true);
        foreach (string name in new[] { nameof(BulletWeaponArchetype.ShotDelay), nameof(BulletWeaponArchetype.BurstDelay), nameof(BulletWeaponArchetype.CooldownDelay) })
            Add(typeof(BulletWeaponArchetype), name, postfix: nameof(NativePatches.ShotDelay));
        Add(typeof(BulletWeaponArchetype), nameof(BulletWeaponArchetype.ChargeupDelay), postfix: nameof(NativePatches.ChargeDelay));
        Add(typeof(Weapon), "CastWeaponRay", nameof(NativePatches.Ray), parameters:
            new[] { typeof(Transform), typeof(Weapon.WeaponHitData).MakeByRefType(), typeof(Vector3), typeof(int) });
        Add(typeof(BulletWeapon), nameof(BulletWeapon.BulletHit), nameof(NativePatches.VanillaHit));
        Add(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.BulletDamage), nameof(NativePatches.ArmorPrefix),
            nameof(NativePatches.ArmorPostfix), finalizer: nameof(NativePatches.ArmorFinalizer));
        Add(typeof(FPS_RecoilSystem), nameof(FPS_RecoilSystem.ApplyRecoil), postfix: nameof(NativePatches.Recoil));
        Add(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.PickupAmmo), nameof(NativePatches.Refill));
        Add(typeof(PlayerBackpack), nameof(PlayerBackpack.SetupAmmoStorageForItem), postfix: nameof(NativePatches.BackpackAmmo));
        Add(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.GetBulletMaxCap), postfix: nameof(NativePatches.ReserveRead));
        Add(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.OnItemEquippableFlashlightWielded),
            nameof(NativePatches.LightWieldPrefix), nameof(NativePatches.LightWieldPostfix), finalizer: nameof(NativePatches.LightWieldFinalizer));
        Add(typeof(GearManager), "LoadOfflineGearDatas", nameof(NativePatches.CaptureLights), early: true);
        Add(typeof(Light), "set_range", nameof(NativePatches.LightRange));
        Add(typeof(Light), "set_intensity", nameof(NativePatches.LightIntensity));
        Add(typeof(Light), "set_spotAngle", nameof(NativePatches.LightAngle));
        Add(typeof(Light), "set_color", nameof(NativePatches.LightColor));
        Add(typeof(Light), "set_cookie", nameof(NativePatches.LightCookie));
        if (EwcActive) NativeEwc.Install(this);
        GameDataAPI.OnGameDataInitialized += DataReady;
        LevelAPI.OnLevelCleanup += Reset;
        EventAPI.OnCheckpointReloaded += Checkpoint;
        LevelAPI.OnEnterLevel += Rebind;
        GearAPI.OnGearSpawned += GearSpawned;
        AssetAPI.OnStartupAssetsLoaded += Flashlights.StartupAssetsReady;
    }
    internal void Add(Type type, string name, string? prefix = null, string? postfix = null, bool early = false, Type[]? parameters = null, string? finalizer = null)
    {
        var method = parameters == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, parameters);
        Add(method, prefix, postfix, early, finalizer: finalizer);
    }
    internal void Add(MethodBase? method, string? prefix, string? postfix, bool early = false, Type? patchType = null, string? finalizer = null)
    {
        if (method == null) { _errors.Add("MISSING_PATCH_TARGET"); return; }
        try
        {
            HarmonyMethod? Patch(string? name)
            {
                if (name == null) return null;
                return new HarmonyMethod(patchType ?? typeof(NativePatches), name)
                {
                    priority = early ? Priority.First : Priority.Last,
                    before = early ? new[] { "ExtraWeaponCustomization" } : Array.Empty<string>(),
                    after = early ? Array.Empty<string>() : new[] { "ExtraWeaponCustomization", "PreLuna.FlashlightCustom", "Dinorush.ExtraRecoilData" }
                };
            }
            _harmony.Patch(method, Patch(prefix), Patch(postfix), finalizer: Patch(finalizer));
            _patches.Add(method.DeclaringType!.FullName + "." + method.Name);
        }
        catch (Exception error) { _errors.Add(method.Name + ":" + error.Message); Log.LogWarning("接点未安装：" + method.Name + " " + error.Message); }
    }
    private void DataReady()
    {
        Reset(); _offline.Clear(); Flashlights.ReloadProfiles();
        Catalog.SetSystemFlashlights(DataPackId, GearFlashlightPartDataBlock.GetAllBlocks().Where(part => part.persistentID > 0)
            .Select(part => (part.persistentID, "原版手电 · " + part.name)));
    }
    public void RefreshBindings() { foreach (var value in _bindings.Values.ToArray()) Get(value.Gun); }
    private void GearSpawned(ItemEquippable item, CustomWeaponComponent? component)
    {
        var gun = item.TryCast<BulletWeapon>();
        if (gun != null) Get(gun)?.EnsureTail(component);
    }
    public NativeBinding? Get(BulletWeapon? gun)
    {
        if (gun == null || _detaching.Contains(gun.Pointer)) return null;
        if (gun.Owner == null) { Remove(gun); return null; }
        var lease = DescribeOwner(gun.Owner);
        var target = lease.Decision.Target;
        if (target == null) { Remove(gun); return null; }
        if (_bindings.TryGetValue(gun.Pointer, out var existing))
        {
            if (existing.OwnershipKey == lease.Key && existing.Owner == gun.Owner.Pointer &&
                existing.Core.Weapon.DataPackId == DataPackId && existing.Core.Weapon.OfflineGearId is uint id &&
                _offline.TryGetValue(id, out var original) && gun.GearIDRange != null && original.IsEqual(gun.GearIDRange))
            { existing.Refresh(); return existing; }
            Remove(gun);
        }
        var identity = ResolveIdentity(gun.GearIDRange);
        if (identity == null) return null;
        var binding = new NativeBinding(this, gun, target, identity, lease.Decision.Mode, lease.Key);
        _bindings.Add(gun.Pointer, binding);
        binding.Refresh(); return binding;
    }
    internal ConfigurationTarget? ResolveTarget(PlayerAgent owner) => DescribeOwner(owner).Decision.Target;
    public WeaponIdentity? ResolveIdentity(GearIDRange? range)
    {
        if (range == null) return null;
        if (_offline.Count == 0)
            foreach (var data in PlayerOfflineGearDataBlock.GetAllBlocks())
                if (!string.IsNullOrWhiteSpace(data.GearJSON)) _offline[data.persistentID] = new GearIDRange(data.GearJSON);
        var ids = _offline.Where(pair => pair.Value.IsEqual(range)).Select(pair => pair.Key).ToArray();
        if (ids.Length != 1) return null; // Ambiguous records cannot silently share a saved key.
        return new(DataPackId, ids[0]);
    }
    internal NativeBinding? FromComponent(CustomWeaponComponent? component)
    {
        if (component?.Weapon is LocalGunComp local) return Get(local.Value);
        if (component?.Weapon is SyncedGunComp synced) return Get(synced.Value);
        return null;
    }
    internal NativeBinding? FromAmmo(PlayerAmmoStorage storage, AmmoType type)
    {
        if (storage.m_playerBackpack == null) return null;
        InventorySlot slot = type == AmmoType.Standard ? InventorySlot.GearStandard : type == AmmoType.Special ? InventorySlot.GearSpecial : InventorySlot.None;
        return slot != InventorySlot.None && storage.m_playerBackpack.TryGetBackpackItem(slot, out var item) ? Get(item.Instance?.TryCast<BulletWeapon>()) : null;
    }
    public void Remove(BulletWeapon gun)
    {
        RemovePointer(gun.Pointer);
    }
    private void RemovePointer(IntPtr pointer)
    {
        if (!_bindings.Remove(pointer, out var old)) return;
        _detaching.Add(pointer);
        try { old.Dispose(); }
        finally { _detaching.Remove(pointer); }
    }
    public void Reset()
    {
        foreach (var pointer in _bindings.Keys.ToArray()) RemovePointer(pointer);
        Flashlights.Clear();
    }
    private void Checkpoint() { Reset(); Rebind(); }
    private void Rebind()
    {
        if (PlayerBackpackManager.Current == null) return;
        foreach (var backpack in PlayerBackpackManager.Current.m_backpacks.Values)
            foreach (var slot in new[] { InventorySlot.GearStandard, InventorySlot.GearSpecial })
                if (backpack.TryGetBackpackItem(slot, out var item))
                {
                    var binding = Get(item.Instance?.TryCast<BulletWeapon>());
                    if (binding != null) { binding.Refresh(); Flashlights.Refresh(binding); }
                }
    }
    public void Dispose()
    {
        Reset();
        GameDataAPI.OnGameDataInitialized -= DataReady; LevelAPI.OnLevelCleanup -= Reset;
        EventAPI.OnCheckpointReloaded -= Checkpoint; LevelAPI.OnEnterLevel -= Rebind; GearAPI.OnGearSpawned -= GearSpawned;
        AssetAPI.OnStartupAssetsLoaded -= Flashlights.StartupAssetsReady;
        _harmony.UnpatchSelf(); Current = null;
    }
}
