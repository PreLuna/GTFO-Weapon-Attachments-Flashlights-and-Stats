using System;
using System.Linq;
using Player;
using SNetwork;
using UnityEngine;
using PreLuna.WeaponAttachments.Ownership;

namespace PreLuna.WeaponAttachments.Native;

public sealed partial class NativeRuntime
{
    internal sealed record OwnerLease(OwnershipDecision Decision, string Key);
    private float _ownershipCheckAt;
    private bool _auditingOwnership;
    public string OwnershipError { get; private set; } = "";
    public int WithdrawnOwnershipBindings { get; private set; }
    internal OwnerLease DescribeOwner(PlayerAgent? owner)
    {
        var player = owner?.Owner;
        var local = SNet.LocalPlayer;
        int localSlot = local?.PlayerSlot?.index ?? -1, ownerSlot = player?.PlayerSlot?.index ?? -1;
        bool localOwner = player != null && local != null && player.Pointer == local.Pointer;
        bool roster = localOwner || (ownerSlot is >= 0 and <= 3 && SNet.Slots?.GetPlayerInSlot(ownerSlot)?.Pointer == player?.Pointer);
        var decision = OwnershipPolicy.Resolve(new(SNet.IsMaster, localSlot, ownerSlot, localOwner,
            player?.IsBot == true, owner?.IsLocallyOwned == true,
            owner != null && player != null && !owner.IsBeingDestroyed, roster));
        string key = decision.Mode + ":" + decision.Target?.StableKey + ":" + owner?.Pointer + ":" + player?.Pointer + ":" + player?.Lookup + ":" +
            local?.Pointer + ":" + local?.Lookup + ":" + localSlot + ":" + (SNet.IsMaster ? local?.Lookup : SNet.Master?.Lookup);
        return new(decision, key);
    }
    internal bool IsCurrent(NativeBinding binding)
    {
        if (binding.Gun == null || binding.Gun.Owner == null || !_bindings.TryGetValue(binding.Gun.Pointer, out var current) || !ReferenceEquals(current, binding)) return false;
        var owner = DescribeOwner(binding.Gun.Owner);
        return owner.Decision.Target != null && owner.Key == binding.OwnershipKey && binding.Core.Weapon.DataPackId == DataPackId &&
            binding.Core.Weapon.OfflineGearId is uint id && _offline.TryGetValue(id, out var original) &&
            binding.Gun.GearIDRange != null && original.IsEqual(binding.Gun.GearIDRange);
    }
    // Callbacks reject stale leases immediately. This audit also withdraws persistent
    // reserve/light state on an idle weapon, without waiting for another shot/getter.
    public void ReconcileOwnership()
    {
        if (_auditingOwnership) return;
        _auditingOwnership = true;
        try
        {
            foreach (var pair in _bindings.ToArray())
                if (pair.Value.Gun == null || !IsCurrent(pair.Value))
                { RemovePointer(pair.Key); WithdrawnOwnershipBindings++; }
        }
        catch (Exception error) { OwnershipError = error.Message; Log.LogError("配件归属检查：" + error); }
        finally { _auditingOwnership = false; }
    }
    public void TickOwnership()
    {
        if (Time.unscaledTime < _ownershipCheckAt) return;
        _ownershipCheckAt = Time.unscaledTime + .1f;
        ReconcileOwnership();
    }
}

public sealed class NativeOwnershipDriver : MonoBehaviour
{
    public NativeOwnershipDriver(IntPtr pointer) : base(pointer) { }
    public void LateUpdate() => NativeRuntime.Current?.TickOwnership();
}
