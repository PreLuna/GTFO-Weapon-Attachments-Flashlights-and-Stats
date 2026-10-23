using System;
using System.Collections.Generic;
using System.Linq;
using AssetShards;
using GameData;
using Gear;
using GTFO.API;
using PreLuna.WeaponAttachments.Definitions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PreLuna.WeaponAttachments.Native;

public sealed class NativeFlashlights
{
    public sealed record Profile(uint SettingsId, float Range, float Angle, float Intensity, Color Color, string CookieName)
    {
        public Texture? Cookie { get; internal set; }
    }
    private readonly NativeRuntime _runtime;
    private readonly Dictionary<uint, Profile> _profiles = new();
    private readonly Dictionary<uint, uint> _parts = new();
    private readonly HashSet<uint> _pending = new();
    private readonly HashSet<NativeBinding> _waiting = new();
    private readonly Dictionary<IntPtr, (NativeBinding Binding, Profile Profile, PlayerInventoryBase Inventory)> _lights = new();
    private readonly Dictionary<IntPtr, (NativeBinding Binding, Profile Profile)> _inventories = new();
    private bool _refreshing, _restoring;
    private int _dataGeneration;
    public string LastResolution { get; private set; } = "NotSelected";
    public int CapturedProfiles => _profiles.Count;
    internal NativeFlashlights(NativeRuntime runtime) { _runtime = runtime; }
    internal void ReloadProfiles()
    {
        _dataGeneration++; _profiles.Clear(); _parts.Clear(); _pending.Clear(); CaptureProfiles();
    }
    internal void StartupAssetsReady()
    {
        foreach (var binding in _waiting.ToArray()) Refresh(binding);
    }
    public void CaptureProfiles()
    {
        // Captured before FlashlightCustom's LoadOfflineGearDatas postfix mutates the shared settings.
        if (_profiles.Count != 0) return;
        try
        {
            foreach (var block in FlashlightSettingsDataBlock.GetAllBlocks())
                _profiles[block.persistentID] = new(block.persistentID, block.range, block.angle, block.intensity, block.color, block.cookie);
        }
        catch (Exception error) { _runtime.Log.LogDebug("手电原版配置尚未就绪：" + error.Message); }
    }
    private bool PartSettings(uint id, out uint settingsId)
    {
        if (_parts.TryGetValue(id, out settingsId)) return true;
        var block = GearFlashlightPartDataBlock.GetBlock(id);
        if (block == null) { LastResolution = "UnknownPart"; return false; }
        bool Read()
        {
            var prefab = AssetAPI.GetLoadedAsset<GameObject>(block.General.Model);
            var part = prefab?.GetComponent<GearPartFlashlight>() ?? prefab?.GetComponentInChildren<GearPartFlashlight>(true);
            if (part == null) return false;
            _parts[id] = part.m_settingsID; return true;
        }
        if (Read()) { settingsId = _parts[id]; return true; }
        LastResolution = "LoadingOriginalPart";
        if (_pending.Add(id))
        {
            int generation = _dataGeneration;
            bool started = AssetShardManager.LoadShardAsync(block.General.AssetBundle, block.General.BundleShard,
                (Action<AsyncOperation>)(_ =>
                {
                    if (generation != _dataGeneration || NativeRuntime.Current != _runtime) return;
                    _pending.Remove(id);
                    if (Read()) foreach (var value in _waiting.Concat(_inventories.Values.Select(item => item.Binding)).Distinct().ToArray()) Refresh(value);
                }), LoadSceneMode.Additive);
            if (!started) _pending.Remove(id);
        }
        settingsId = 0; return false;
    }
    public Profile? Resolve(FlashlightReference reference)
    {
        if (reference.DataPackId != _runtime.DataPackId) { LastResolution = "DifferentDataPack"; return null; }
        CaptureProfiles();
        uint? part = reference.SourcePartId;
        if (reference.SourceWeapon != null)
        {
            var source = reference.SourceWeapon;
            if (source.DataPackId != _runtime.DataPackId || source.OfflineGearId == null) { LastResolution = "UnresolvedSourceWeapon"; return null; }
            var block = PlayerOfflineGearDataBlock.GetBlock(source.OfflineGearId.Value);
            if (block == null) { LastResolution = "UnknownSourceWeapon"; return null; }
            var range = new GearIDRange(block.GearJSON);
            uint weaponPart = range.GetCompID(eGearComponent.FlashlightPart);
            if (part.HasValue && part != weaponPart) { LastResolution = "ConflictingSources"; return null; }
            part = weaponPart;
        }
        uint? settings = reference.SettingsId;
        if (part.HasValue)
        {
            if (!PartSettings(part.Value, out uint resolved)) return null;
            if (settings.HasValue && settings != resolved) { LastResolution = "ConflictingSources"; return null; }
            settings = resolved;
        }
        if (!settings.HasValue || !_profiles.TryGetValue(settings.Value, out var profile)) { LastResolution = "UnknownSettings"; return null; }
        if (!string.IsNullOrEmpty(profile.CookieName) && profile.Cookie == null)
        {
            if (PlayerInventoryBase.m_flashlightCookies?.ContainsKey(profile.CookieName) == true)
                profile.Cookie = PlayerInventoryBase.m_flashlightCookies[profile.CookieName];
            profile.Cookie ??= AssetAPI.GetLoadedAsset<Texture>(profile.CookieName);
            if (profile.Cookie == null) { LastResolution = "LoadingOriginalCookie"; return null; }
        }
        LastResolution = "ResolvedOriginalProfile"; return profile;
    }
    internal uint? BeforeWield(PlayerInventoryBase inventory, ItemEquippable item, GearPartFlashlight flashlight)
    {
        if (_restoring) { UnregisterInventory(inventory); return null; }
        var gun = item?.TryCast<BulletWeapon>();
        var binding = gun == null ? null : _runtime.Get(gun);
        if (binding == null || flashlight == null) { UnregisterInventory(inventory); return null; }
        var reference = binding.Core.Definitions.Select(definition => definition.Flashlight).FirstOrDefault(value => value != null);
        if (reference == null) { UnregisterInventory(inventory); return null; }
        var profile = Resolve(reference);
        if (profile == null) { if (LastResolution is "LoadingOriginalPart" or "LoadingOriginalCookie") _waiting.Add(binding); UnregisterInventory(inventory); return null; }
        _waiting.Remove(binding);
        UnregisterInventory(inventory);
        _inventories[inventory.Pointer] = (binding, profile);
        RegisterLight(inventory.m_flashlight, binding, profile, inventory);
        RegisterLight(binding.Gun.m_lt, binding, profile, inventory);
        uint old = flashlight.m_settingsID;
        // Only the instance's lookup ID is temporarily switched; the visible part and alignment are reused.
        flashlight.m_settingsID = profile.SettingsId;
        return old;
    }
    internal void AfterWield(PlayerInventoryBase inventory)
    {
        if (!_inventories.TryGetValue(inventory.Pointer, out var selected)) return;
        var profile = selected.Profile;
        // Resolve verified the original cookie before we registered this inventory.
        RegisterLight(inventory.m_flashlight, selected.Binding, profile, inventory);
        RegisterLight(selected.Binding.Gun.m_lt, selected.Binding, profile, inventory);
        foreach (var entry in _lights.Where(pair => pair.Value.Inventory.Pointer == inventory.Pointer).ToArray())
        {
            var light = entry.Key == inventory.m_flashlight?.Pointer ? inventory.m_flashlight : selected.Binding.Gun.m_lt;
            if (light != null) Apply(light, profile);
        }
    }
    private void RegisterLight(Light? light, NativeBinding binding, Profile profile, PlayerInventoryBase inventory)
    {
        if (light != null) _lights[light.Pointer] = (binding, profile, inventory);
    }
    private static void Apply(Light light, Profile profile)
    {
        light.range = profile.Range; light.spotAngle = profile.Angle; light.intensity = profile.Intensity; light.color = profile.Color;
        if (profile.Cookie != null || string.IsNullOrEmpty(profile.CookieName)) light.cookie = profile.Cookie;
    }
    internal Profile? ForLight(Light light)
    {
        if (!_lights.TryGetValue(light.Pointer, out var value)) return null;
        if (!value.Binding.IsCurrent)
        { return null; } // Keep the registration until the ownership audit restores the original inventory lighting.
        return value.Profile;
    }
    internal void Refresh(NativeBinding binding)
    {
        if (_refreshing || binding.Gun == null || binding.Gun.Owner == null) return;
        var inventory = binding.Gun.Owner.Inventory;
        if (inventory == null || inventory.WieldedItem?.Pointer != binding.Gun.Pointer || binding.Gun.AttachedFlashlight == null) return;
        _refreshing = true;
        try { inventory.OnItemEquippableFlashlightWielded(binding.Gun, binding.Gun.AttachedFlashlight); }
        finally { _refreshing = false; }
    }
    private void UnregisterInventory(PlayerInventoryBase inventory)
    {
        _inventories.Remove(inventory.Pointer);
        foreach (var key in _lights.Where(pair => pair.Value.Inventory.Pointer == inventory.Pointer).Select(pair => pair.Key).ToArray()) _lights.Remove(key);
    }
    internal void Remove(NativeBinding binding)
    {
        _waiting.Remove(binding);
        var inventory = _lights.Values.Where(value => value.Binding == binding).Select(value => value.Inventory).FirstOrDefault();
        foreach (var key in _lights.Where(pair => pair.Value.Binding == binding).Select(pair => pair.Key).ToArray()) _lights.Remove(key);
        foreach (var key in _inventories.Where(pair => pair.Value.Binding == binding).Select(pair => pair.Key).ToArray()) _inventories.Remove(key);
        if (inventory != null && binding.Gun != null && inventory.WieldedItem?.Pointer == binding.Gun.Pointer && binding.Gun.AttachedFlashlight != null)
        {
            _restoring = true;
            try { inventory.OnItemEquippableFlashlightWielded(binding.Gun, binding.Gun.AttachedFlashlight); }
            finally { _restoring = false; }
        }
    }
    internal void Clear() { _lights.Clear(); _inventories.Clear(); _waiting.Clear(); NativePatches.Wielded = null; NativePatches.CurrentFrame = null; }
}
