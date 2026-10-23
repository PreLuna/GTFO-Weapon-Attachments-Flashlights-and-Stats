using System;
using System.Collections.Generic;
using System.Linq;
using EWC.CustomWeapon;
using EWC.CustomWeapon.Properties.Traits;
using Gear;
using Player;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Execution;
using PreLuna.WeaponAttachments.Modifiers;
using PreLuna.WeaponAttachments.Ownership;
using UnityEngine;

namespace PreLuna.WeaponAttachments.Native;

public sealed class NativeBinding : IDisposable
{
    internal readonly NativeRuntime Runtime;
    public BulletWeapon Gun { get; }
    public IntPtr Owner { get; }
    public RuntimeBinding Core { get; }
    public OwnershipMode OwnershipMode { get; }
    internal string OwnershipKey { get; }
    private readonly OwnedValue _reserveCap = new(), _resourceCap = new();
    private InventorySlotAmmo? _slot;
    private bool _refreshing, _disposed;
    private NativeTail? _tail;
    private EWC.CustomWeapon.WeaponContext.ContextController? _controller;
    private CustomWeaponComponent? _createdComponent;
    private readonly List<(RuntimeEvent Kind, long Serial)> _queued = new();
    private long _lastReconciledRevision = -1;
    private long _lastReloadGeneration = -1;
    internal bool IsCurrent => !_disposed && Runtime.IsCurrent(this);
    public NativeBinding(NativeRuntime runtime, BulletWeapon gun, ConfigurationTarget target, WeaponIdentity identity,
        OwnershipMode mode, string ownershipKey)
    {
        Runtime = runtime; Gun = gun; Owner = gun.Owner.Pointer; OwnershipMode = mode; OwnershipKey = ownershipKey;
        Core = new(runtime.Catalog, runtime.Loadouts, runtime.Calculator, target, identity,
            "weapon:" + gun.Pointer.ToInt64().ToString("x"), Capabilities(null));
    }
    private string[] Capabilities(CustomWeaponComponent? cwc)
    {
        if (Runtime.Errors.Count != 0) return Array.Empty<string>();
        var result = NativeRuntime.StandardCapabilities.ToList();
        if (Runtime.EwcActive)
            result.AddRange(new[] { "stat.charge_speed", "stat.backstab_multiplier", "mechanism.armor_pierce" });
        else
            result.RemoveAll(value => value is "condition.kills" or "condition.weakpoint_hits" or "condition.consecutive_hits");
        if (cwc?.GetTrait<Silence>() != null && OwnershipMode == OwnershipMode.LocalHost)
            result.AddRange(new[] { "stat.wake_up_radius", "stat.alert_radius", "stat.alert_buildup" });
        if (cwc?.GetTrait<ThickBullet>() != null) result.Add("stat.hit_radius");
        return OwnershipPolicy.Capabilities(OwnershipMode, result).ToArray();
    }
    public void Refresh()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            var cwc = Runtime.EwcActive ? Gun.GetComponent<CustomWeaponComponent>() : null;
            Core.SetCapabilities(Capabilities(cwc));
            Core.Refresh();
            if (Core.HasSelection && Runtime.EwcActive) EnsureTail(cwc);
            else DetachTail();
            if (_lastReconciledRevision != Runtime.Loadouts.Revision || _lastReloadGeneration != Runtime.Loadouts.ReloadGeneration)
            {
                _lastReconciledRevision = Runtime.Loadouts.Revision;
                _lastReloadGeneration = Runtime.Loadouts.ReloadGeneration;
                RefreshReserve();
                ReconcileClip();
                Runtime.Flashlights.Refresh(this);
            }
        }
        finally { _refreshing = false; }
    }
    internal void EnsureTail(CustomWeaponComponent? cwc = null)
    {
        if (!Runtime.EwcActive || !Core.HasSelection || _disposed) return;
        cwc ??= Gun.GetComponent<CustomWeaponComponent>();
        if (cwc == null)
        {
            cwc = Gun.gameObject.AddComponent<CustomGunComponent>();
            cwc.Register(new CustomWeaponData { ArchetypeID = Gun.ArchetypeID });
            _createdComponent = cwc;
        }
        var controller = cwc.GetContextController();
        if (!ReferenceEquals(_controller, controller) && _tail != null) _controller?.Unregister(_tail);
        _controller = controller;
        _tail ??= new NativeTail(this);
        _tail.CWC = cwc;
        controller.Register(_tail); // HashSet registration makes this idempotent; upstream properties are not rebuilt.
    }
    public float Value(string stat, float upstream, StatValueForm form = StatValueForm.Direct)
    {
        if (!IsCurrent) return upstream;
        double value = Core.Calculate(stat, upstream, Now, Aiming, Crouched, form);
        return double.IsFinite(value) && value >= 0 && value <= float.MaxValue ? (float)value : upstream;
    }
    internal bool Aiming => OwnershipMode == OwnershipMode.HostBot || (Gun.Owner?.Inventory?.WieldedItem?.Pointer == Gun.Pointer && Gun.AimButtonHeld);
    internal bool Crouched => Gun.Owner?.Locomotion?.m_crouch > 0.5f;
    internal static double Now => Math.Max(0, Clock.Time);
    public InventorySlotAmmo? AmmoSlot
    {
        get
        {
            var owner = Gun.Owner?.Owner;
            if (owner == null || !PlayerBackpackManager.TryGetBackpack(owner, out var backpack)) return null;
            return backpack.AmmoStorage?.GetInventorySlotAmmo(Gun.AmmoType);
        }
    }
    public void RefreshReserve()
    {
        if (!IsCurrent) return;
        var slot = AmmoSlot;
        if (slot == null) return;
        if (_slot != null && _slot.Pointer != slot.Pointer) WithdrawReserve();
        _slot = slot;
        double baselineBullets = _reserveCap.Observe(slot.BulletsMaxCap);
        double result = Core.Calculate("reserve_capacity", baselineBullets, Now, Aiming, Crouched);
        double baseResource = _resourceCap.Observe(slot.AmmoMaxCap);
        double resourceResult = baseResource + (result - baselineBullets) * slot.CostOfBullet;
        if (!double.IsFinite(resourceResult) || resourceResult < 0 || resourceResult > float.MaxValue) return;
        double bullets = _reserveCap.Apply(slot.BulletsMaxCap, _ => result);
        float resource = _resourceCap.ApplySingle(slot.AmmoMaxCap, _ => resourceResult);
        if (slot.BulletsMaxCap != (int)bullets) slot.BulletsMaxCap = (int)bullets;
        if (slot.AmmoMaxCap != (float)resource) slot.AmmoMaxCap = (float)resource;
    }
    private void WithdrawReserve()
    {
        if (_slot == null) return;
        _slot.BulletsMaxCap = (int)_reserveCap.Withdraw(_slot.BulletsMaxCap);
        _slot.AmmoMaxCap = (float)_resourceCap.Withdraw(_slot.AmmoMaxCap);
        _slot = null;
    }
    private void ReconcileClip()
    {
        // Capacity can shrink both when removing a part and when switching between two positive bonuses.
        {
            var slot = AmmoSlot;
            int max = Gun.ClipSize;
            if (slot != null && max >= 0 && Gun.m_clip > max && slot.CostOfBullet > 0)
            {
                int overflow = Gun.m_clip - max;
                // Preserve ammunition even if the new reserve cap is smaller; spending it resolves temporary overflow.
                slot.AmmoInPack += overflow * slot.CostOfBullet;
                Gun.m_clip = max;
            }
        }
    }
    internal void Signal(RuntimeEvent kind)
    {
        if (!IsCurrent) return;
        long serial = Core.LastEventSerial(kind);
        if (NativePatches.CurrentFrame?.Binding == this) _queued.Add((kind, serial));
        else Return(kind, serial);
    }
    internal void FinishShot(bool fired)
    {
        if (!IsCurrent) { _queued.Clear(); Core.ResetTriggers(); return; }
        if (fired)
        {
            Core.FinishShot();
            foreach (var item in _queued) Return(item.Kind, item.Serial);
            Return(RuntimeEvent.Shot, Core.ShotSerial);
        }
        else if (Core.ShotSerial > 0) Core.CancelShot();
        _queued.Clear();
    }
    private void Return(RuntimeEvent kind, long serial)
    {
        if (!IsCurrent) return;
        var slot = AmmoSlot;
        if (slot == null || slot.CostOfBullet <= 0) return;
        Core.ReturnAmmo(kind, serial, new AmmoEndpoint(this, slot), Now, Aiming, Crouched, System.Random.Shared.NextDouble);
    }
    internal void EwcReset() { _controller?.Unregister(_tail!); _controller = null; _tail = null; Core.ResetTriggers(); }
    private void DetachTail()
    {
        if (_tail == null) return;
        var cwc = _tail.CWC;
        _controller?.Unregister(_tail); _tail = null; _controller = null;
        // Recompute EWC's cached rate with its remaining properties; never Clear an upstream controller.
        var gunComponent = cwc != null ? cwc.TryCast<CustomGunComponent>() : null;
        if (gunComponent != null && !gunComponent.Destroyed && Gun != null) gunComponent.UpdateStoredFireRate();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        DetachTail();
        var slot = _slot;
        WithdrawReserve(); Runtime.Flashlights.Remove(this);
        if (Gun != null && slot != null && slot.CostOfBullet > 0)
        {
            int cleanCapacity = Gun.ClipSize;
            if (cleanCapacity >= 0 && Gun.m_clip > cleanCapacity)
            { slot.AmmoInPack += (Gun.m_clip - cleanCapacity) * slot.CostOfBullet; Gun.m_clip = cleanCapacity; }
        }
        if (_createdComponent != null && !_createdComponent.Destroyed && _createdComponent.GetContextController().Properties.Count == 0)
        { _createdComponent.Clear(); UnityEngine.Object.Destroy(_createdComponent); }
        _queued.Clear(); Core.ResetTriggers();
    }
    private sealed class AmmoEndpoint : IAmmoEndpoint
    {
        private readonly NativeBinding _binding;
        private readonly InventorySlotAmmo _slot;
        public AmmoEndpoint(NativeBinding binding, InventorySlotAmmo slot) { _binding = binding; _slot = slot; }
        public int Clip { get => _binding.Gun.m_clip; set => _binding.Gun.SetCurrentClip(value); }
        public int ClipCapacity => _binding.Gun.ClipSize;
        public double Reserve
        {
            get => _slot.AmmoInPack / _slot.CostOfBullet;
            set
            {
                double resource = value * _slot.CostOfBullet;
                if (double.IsFinite(resource) && resource >= 0 && resource <= float.MaxValue) _slot.AmmoInPack = (float)resource;
            }
        }
        public int ReserveCapacity => _slot.BulletsMaxCap;
    }
}
