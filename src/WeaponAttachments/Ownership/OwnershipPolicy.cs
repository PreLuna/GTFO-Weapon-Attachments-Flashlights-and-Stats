using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Ownership;

public enum OwnershipMode { Ignored, LocalHost, LocalClient, HostBot }
public sealed record OwnerSnapshot(bool IsHost, int LocalSlot, int OwnerSlot, bool OwnerIsLocal,
    bool OwnerIsBot, bool LocallyOwned, bool Usable = true, bool InRoster = true);
public sealed record OwnershipDecision(ConfigurationTarget? Target, OwnershipMode Mode, string Reason);

public static class OwnershipPolicy
{
    public static int TeamSlot(int botSlot, int localSlot) => localSlot is >= 0 and <= 3 && botSlot is >= 1 and <= 3
        ? (botSlot - 1 >= localSlot ? botSlot : botSlot - 1) : -1;
    public static int BotSlot(int teamSlot, int localSlot) => localSlot is >= 0 and <= 3 && teamSlot is >= 0 and <= 3 && teamSlot != localSlot
        ? (teamSlot < localSlot ? teamSlot + 1 : teamSlot) : -1;
    public static OwnershipDecision Resolve(OwnerSnapshot value)
    {
        OwnershipDecision Reject(string reason) => new(null, OwnershipMode.Ignored, reason);
        if (!value.Usable || !value.InRoster || value.LocalSlot is < 0 or > 3 || value.OwnerSlot is < 0 or > 3) return Reject("OWNER_UNAVAILABLE");
        if (value.OwnerIsLocal && !value.OwnerIsBot && value.OwnerSlot == value.LocalSlot && value.LocallyOwned)
            return new(ConfigurationTarget.LocalPlayer, value.IsHost ? OwnershipMode.LocalHost : OwnershipMode.LocalClient, "LOCAL_PLAYER");
        if (!value.IsHost || !value.OwnerIsBot || value.OwnerIsLocal) return Reject("REMOTE_OR_UNMANAGED");
        int bot = BotSlot(value.OwnerSlot, value.LocalSlot);
        return bot < 0 ? Reject("INVALID_BOT_SLOT") : new(ConfigurationTarget.ForBot(bot), OwnershipMode.HostBot, "HOST_MANAGED_BOT");
    }
    // This first release enables client-local getter/visual paths only. Host-authoritative
    // combat, sound, supply and event effects require separate mixed-room acceptance.
    private static readonly HashSet<string> ClientCapabilities = new(StringComparer.Ordinal)
    {
        "stat.magazine_capacity", "stat.reload_speed", "stat.recoil", "stat.fire_rate", "stat.hip_spread", "stat.aim_spread",
        "condition.always", "condition.aiming", "condition.crouched", "condition.shots_fired", "flashlight.original_profile"
    };
    public static IReadOnlyList<string> Capabilities(OwnershipMode mode, IEnumerable<string> available) =>
        Array.AsReadOnly(available.Where(value => mode switch
        {
            OwnershipMode.LocalHost => true,
            OwnershipMode.HostBot => value is not ("stat.recoil" or "stat.charge_speed" or "stat.wake_up_radius" or "stat.alert_radius" or "stat.alert_buildup"),
            OwnershipMode.LocalClient => ClientCapabilities.Contains(value),
            _ => false
        }).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
}
