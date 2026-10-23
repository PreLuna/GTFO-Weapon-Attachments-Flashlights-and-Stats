using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.WeaponAttachments.Loadouts;

namespace PreLuna.WeaponAttachments.Diagnostics;

internal sealed class LoadoutDiagnostic
{
    public string Status { get; init; } = "Disabled";
    public string FilePath { get; init; } = "";
    public bool CanWrite { get; init; }
    public long Revision { get; init; }
    public int SavedLoadoutCount { get; init; }
    public int ConfigurationTargetCount { get; } = 4;
    public int InvalidSelectionCount { get; init; }
    public int PendingExecutorSelectionCount { get; init; }
    public bool GameplayApplied { get; } = false;
    public IReadOnlyList<LoadoutIssue> Issues { get; init; } = Array.Empty<LoadoutIssue>();

    public static LoadoutDiagnostic From(LoadoutManager manager, string path)
    {
        var selections = manager.Export().Loadouts.SelectMany(item =>
            manager.GetCommitted(item.Target.ToTarget(), item.Weapon.ToIdentity()).Selections).ToArray();
        return new()
        {
            Status = manager.Status, FilePath = path, CanWrite = manager.CanWrite, Revision = manager.Revision,
            SavedLoadoutCount = manager.SavedLoadoutCount,
            InvalidSelectionCount = selections.Count(item => !item.Applicable),
            PendingExecutorSelectionCount = selections.Count(item => item.Applicable && !item.RuntimeReady),
            Issues = manager.Issues
        };
    }
}
