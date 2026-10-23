using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PreLuna.WeaponAttachments.Definitions;

public static class DefinitionIds
{
    private static readonly Regex Pattern = new(@"\A[a-z][a-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant);
    public static bool IsValid(string? value) => value != null && Pattern.IsMatch(value);
}

public sealed class WeaponIdentity : IEquatable<WeaponIdentity>
{
    public string DataPackId { get; }
    public uint? OfflineGearId { get; }
    public string? VariantKey { get; }
    public uint? ArchetypeId { get; }

    public WeaponIdentity(string dataPackId, uint? offlineGearId, string? variantKey = null, uint? archetypeId = null)
    {
        if (!DefinitionIds.IsValid(dataPackId)) throw new ArgumentException("无效的数据包标识。", nameof(dataPackId));
        if (offlineGearId == 0) throw new ArgumentException("原版武器记录 ID 必须大于零。", nameof(offlineGearId));
        if (variantKey != null && !DefinitionIds.IsValid(variantKey)) throw new ArgumentException("无效的武器变体标识。", nameof(variantKey));
        if (offlineGearId == null && variantKey == null) throw new ArgumentException("武器身份需要 OfflineGearId 或稳定 VariantKey，不能只使用 ArchetypeId。");
        if (archetypeId == 0) throw new ArgumentException("ArchetypeId 必须大于零。", nameof(archetypeId));
        DataPackId = dataPackId;
        OfflineGearId = offlineGearId;
        VariantKey = variantKey;
        ArchetypeId = archetypeId;
    }

    // ArchetypeId is descriptive, not a unique identity. Display names never enter this key.
    public string StableKey => DataPackId + "|offline:" + (OfflineGearId?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        "|variant:" + (VariantKey == null ? "absent" : "value:" + VariantKey);

    public bool Equals(WeaponIdentity? other) => other != null && StableKey == other.StableKey;
    public override bool Equals(object? other) => other is WeaponIdentity identity && Equals(identity);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(StableKey);
}

public sealed record ConfigurationTarget
{
    public ConfigurationTargetKind Kind { get; }
    public int? BotSlot { get; }
    public string StableKey { get; }
    public string DisplayName { get; }

    private ConfigurationTarget(ConfigurationTargetKind kind, int? botSlot, string key, string displayName)
    {
        Kind = kind;
        BotSlot = botSlot;
        StableKey = key;
        DisplayName = displayName;
    }

    public static ConfigurationTarget LocalPlayer { get; } = new(ConfigurationTargetKind.LocalPlayer, null, "local_player", "我");
    public static IReadOnlyList<ConfigurationTarget> All { get; } = Array.AsReadOnly(new[]
        { LocalPlayer, ForBot(1), ForBot(2), ForBot(3) });

    public static ConfigurationTarget ForBot(int slot)
    {
        if (slot < 1 || slot > 3) throw new ArgumentOutOfRangeException(nameof(slot), "人机配置槽位只能是 1、2、3。");
        return new(ConfigurationTargetKind.HostBot, slot, "host_bot:" + slot.ToString(CultureInfo.InvariantCulture), "人机 " + slot);
    }
}
