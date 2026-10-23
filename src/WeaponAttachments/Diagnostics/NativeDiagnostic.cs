using System;
using System.Collections.Generic;
using PreLuna.WeaponAttachments.Native;

namespace PreLuna.WeaponAttachments.Diagnostics;

internal sealed class NativeDiagnostic
{
    public string Status { get; init; } = "Disabled";
    public string DataPackId { get; init; } = "";
    public bool EwcActive { get; init; }
    public IReadOnlyList<string> InstalledPatches { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PatchErrors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<NativePatchInfo> PatchOrder { get; init; } = Array.Empty<NativePatchInfo>();
    public bool ModifiesSharedDataBlocks { get; } = false;
    public bool RebuildsExistingEwcProperties { get; } = false;
    public string NativeScope { get; } = "Owned hitscan firearms; runtime prerequisites checked per instance";
    public IReadOnlyList<string> ConditionalSupport { get; } = new[]
    {
        "Silence percentages require an existing positive EWC Silence baseline; BOT Silence is unavailable",
        "Hit radius requires an existing positive EWC ThickBullet baseline",
        "Armor, kill/weakpoint/consecutive conditions, backstab and charge need active EWC",
        "Timed event thresholds above 8192 are not enabled",
        "BOT camera recoil and local-only EWC charge contexts are unavailable",
        "Any missing native hook disables custom effects for that runtime",
        "Flashlight part IDs are resolved from original prefabs; unresolved resources remain pending",
        "Unknown projectile/explosive event paths and arbitrary unknown late writers are not claimed"
    };
}
