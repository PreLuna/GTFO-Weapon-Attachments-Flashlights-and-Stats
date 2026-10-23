using System;
using System.Collections.Generic;
using System.Linq;
using GameData;
using Gear;
using Player;
using SNetwork;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Modifiers;
using PreLuna.WeaponAttachments.Native;
using PreLuna.WeaponAttachments.Ownership;

namespace PreLuna.WeaponAttachments.Interface;

public sealed class GamePanelPort : IAttachmentPanelPort
{
    private readonly NativeRuntime _native;
    private readonly AttachmentCatalog? _catalog;
    private long _version;
    public GamePanelPort(NativeRuntime native, AttachmentCatalog? catalog = null) { _native = native; _catalog = catalog; }
    public IReadOnlyList<PanelWeapon> Weapons
    {
        get
        {
            var list = new List<PanelWeapon>();
            foreach (var block in PlayerOfflineGearDataBlock.GetAllBlocks())
            {
                if (string.IsNullOrWhiteSpace(block.GearJSON)) continue;
                var range = new GearIDRange(block.GearJSON);
                var item = ItemDataBlock.GetBlock(range.GetCompID(eGearComponent.BaseItem));
                if (item?.inventorySlot is not (InventorySlot.GearStandard or InventorySlot.GearSpecial)) continue;
                var identity = _native.ResolveIdentity(range);
                if (identity != null) list.Add(new(identity, range.PublicGearName.ToString()));
            }
            return list.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        }
    }
    public ConfigurationTarget? TargetOf(SNet_Player? player)
    {
        if (player == null || player.IsLocal) return ConfigurationTarget.LocalPlayer;
        if (!player.IsBot || !SNet.IsMaster || SNet.LocalPlayer?.PlayerSlot == null) return null;
        int bot = OwnershipPolicy.BotSlot(player.PlayerSlot?.index ?? -1, SNet.LocalPlayer.PlayerSlot.index);
        return bot < 0 ? null : ConfigurationTarget.ForBot(bot);
    }
    private SNet_Player? PlayerFor(ConfigurationTarget target)
    {
        if (target.Kind == ConfigurationTargetKind.LocalPlayer) return SNet.LocalPlayer;
        int host = SNet.LocalPlayer?.PlayerSlot?.index ?? -1;
        if (host < 0 || host > 3) return null;
        return SNet.Slots?.GetPlayerInSlot(OwnershipPolicy.TeamSlot(target.BotSlot!.Value, host));
    }
    public PanelContext Read(ConfigurationTarget target, WeaponIdentity weapon)
    {
        var data = PlayerOfflineGearDataBlock.GetBlock(weapon.OfflineGearId!.Value) ?? throw new InvalidOperationException("当前武器已经不可用");
        var range = new GearIDRange(data.GearJSON);
        var category = GearCategoryDataBlock.GetBlock(range.GetCompID(eGearComponent.Category));
        var arch = ArchetypeDataBlock.GetBlock(GearBuilder.GetArchetypeID(category, (eWeaponFireMode)range.GetCompID(eGearComponent.FireMode)));
        var values = new Dictionary<string, StatInput>(StringComparer.Ordinal);
        void Base(string id, double value, StatValueForm form = StatValueForm.Direct) { if (double.IsFinite(value) && value >= 0) values[id] = new(id, value, value, form); }
        if (arch != null)
        {
            Base("damage", arch.Damage); Base("weakspot_multiplier", arch.PrecisionDamageMulti); Base("stagger_multiplier", arch.StaggerDamageMulti);
            Base("magazine_capacity", arch.DefaultClipSize); Base("reload_speed", arch.DefaultReloadTime, StatValueForm.DurationSeconds);
            Base("falloff_start", arch.DamageFalloff.x); Base("falloff_end", arch.DamageFalloff.y);
            Base("hip_spread", arch.HipFireSpread); Base("aim_spread", arch.AimSpread);
            if (arch.ShotDelay > 0) Base("fire_rate", 60 / arch.ShotDelay);
        }
        var owner = PlayerFor(target);
        bool canEdit = target.Kind == ConfigurationTargetKind.LocalPlayer || (SNet.IsMaster && (owner == null || owner.IsBot));
        string notice = !canEdit ? "只有主机可以配置人机，真人槽位不可编辑" : "基础数据预览；条件增益按实际状态计算";
        NativeBinding? binding = null;
        if (owner != null && PlayerBackpackManager.TryGetBackpack(owner, out var backpack))
            foreach (var slot in new[] { InventorySlot.GearStandard, InventorySlot.GearSpecial })
                if (backpack.TryGetBackpackItem(slot, out var item))
                {
                    var gun = item.Instance?.TryCast<BulletWeapon>();
                    if (gun != null && _native.ResolveIdentity(gun.GearIDRange)?.Equals(weapon) == true) binding = _native.Get(gun);
                }
        var caps = NativeRuntime.StandardCapabilities.ToList();
        if (_native.EwcActive) caps.AddRange(new[] { "stat.charge_speed", "stat.backstab_multiplier", "mechanism.armor_pierce" });
        else caps.RemoveAll(value => value is "condition.kills" or "condition.weakpoint_hits" or "condition.consecutive_hits");
        var mode = target.Kind == ConfigurationTargetKind.HostBot ? (canEdit ? OwnershipMode.HostBot : OwnershipMode.Ignored) :
            SNet.IsMaster ? OwnershipMode.LocalHost : OwnershipMode.LocalClient;
        caps = OwnershipPolicy.Capabilities(mode, caps).ToList();
        if (canEdit && mode == OwnershipMode.LocalClient) notice = "非主机：仅开放本机数值与显示能力；战斗、消音和补给效果待验证";
        if (_native.Errors.Count > 0) caps.Clear();
        var conditions = ConditionSnapshot.Unknown;
        string instance = "catalog:" + weapon.StableKey;
        if (binding != null)
        {
            using (binding.Core.ExcludeOwnEffects()) { _ = binding.Gun.ClipSize; _ = binding.Gun.ReloadTime; }
            foreach (var input in binding.Core.ObservedUpstream)
                values[input.Stat] = input with { BaseValue = values.GetValueOrDefault(input.Stat)?.BaseValue };
            caps = binding.Core.ImplementedCapabilities.ToList();
            var pendingConditions = _catalog?.Entries.SelectMany(entry => entry.Definition.Modifiers.Select(value => value.Condition)
                .Concat(entry.Definition.Mechanisms.Select(value => value.Condition))).Where(value => value != null).Cast<ConditionDefinition>();
            conditions = binding.Core.Conditions(NativeBinding.Now, binding.Aiming, binding.Crouched, pendingConditions);
            instance = binding.Core.InstanceId;
            if (canEdit) notice = "当前实例上游；部分属性取最近一次计算值，条件按当前状态预览";
        }
        return new(new(target, weapon, instance, binding == null ? "gtfo.catalog" : "gtfo.instance", ++_version, values.Values),
            Array.AsReadOnly(caps.ToArray()), conditions, canEdit, notice);
    }
    public string? UnavailableResource(AttachmentDefinition definition)
    {
        if (definition.Flashlight == null) return null;
        if (_native.Flashlights.Resolve(definition.Flashlight) != null) return null;
        return _native.Flashlights.LastResolution is "LoadingOriginalPart" or "LoadingOriginalCookie" ? "正在加载原版手电资源" : "原版手电来源不可用";
    }
    public void Committed(ConfigurationTarget target, WeaponIdentity weapon) => _native.RefreshBindings();
}
