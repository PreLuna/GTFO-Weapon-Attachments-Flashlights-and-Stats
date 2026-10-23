using System;
using System.Collections.Generic;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Loadouts;

public sealed record LoadoutDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string CatalogId { get; init; } = "";
    public long Revision { get; init; }
    public IReadOnlyList<SavedLoadout> Loadouts { get; init; } = Array.Empty<SavedLoadout>();
}

public sealed record SavedTarget
{
    public ConfigurationTargetKind Kind { get; init; }
    public int? BotSlot { get; init; }
    public ConfigurationTarget ToTarget() => Kind switch
    {
        ConfigurationTargetKind.LocalPlayer when BotSlot == null => ConfigurationTarget.LocalPlayer,
        ConfigurationTargetKind.HostBot when BotSlot.HasValue => ConfigurationTarget.ForBot(BotSlot.Value),
        _ => throw new ArgumentException("配置对象只能是本人，或槽位为 1—3 的主机人机。")
    };
    public static SavedTarget From(ConfigurationTarget value) => new() { Kind = value.Kind, BotSlot = value.BotSlot };
}

public sealed record SavedLoadout
{
    public SavedTarget Target { get; init; } = new();
    public WeaponReference Weapon { get; init; } = new();
    public IReadOnlyList<SavedSelection> Selections { get; init; } = Array.Empty<SavedSelection>();
}

public sealed record SavedSelection
{
    public AttachmentSlot Slot { get; init; }
    public string AttachmentId { get; init; } = "";
}

public sealed record LoadoutIssue(string Code, string Path, string Message);
public sealed record LoadoutOperation(bool Success, string Code, string Message);
public sealed record SelectionState(AttachmentSlot Slot, string RequestedId, string SelectedId, string EffectiveId,
    bool Applicable, bool RuntimeReady, string Code, IReadOnlyList<string> MissingCapabilities);
public sealed record LoadoutSnapshot(ConfigurationTarget Target, WeaponIdentity Weapon, long Revision,
    IReadOnlyList<SelectionState> Selections);

public sealed class LoadoutReadResult
{
    public LoadoutDocument? Document { get; }
    public IReadOnlyList<LoadoutIssue> Issues { get; }
    public bool Success => Document != null;
    internal LoadoutReadResult(LoadoutDocument? document, IReadOnlyList<LoadoutIssue> issues)
        { Document = document; Issues = issues; }
}

public sealed record LoadoutFileState(LoadoutDocument Document, string Status, bool CanWrite,
    string Fingerprint, IReadOnlyList<LoadoutIssue> Issues);
public sealed record LoadoutWriteResult(bool Success, string Code, string Message, string Fingerprint = "");

// Persistence is injectable so an unsuccessful disk write can never be mistaken for a committed loadout.
public interface ILoadoutStorage
{
    LoadoutFileState Read();
    LoadoutWriteResult TryWrite(LoadoutDocument document, string expectedFingerprint);
}
