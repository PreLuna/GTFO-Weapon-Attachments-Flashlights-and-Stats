using System;
using System.Collections.Generic;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Diagnostics;

internal sealed class CatalogDiagnostic
{
    public string Status { get; init; } = "NotLoadedDisabled";
    public string FilePath { get; init; } = "";
    public string? CatalogId { get; init; }
    public int SlotCount { get; init; }
    public int OriginalCount { get; init; }
    public int CustomCount { get; init; }
    public int KnownStatCount { get; init; }
    public int KnownMechanismCount { get; init; }
    public int KnownConditionCount { get; init; }
    public int RuntimeEffectExecutors { get; } = 0;
    public int ConfigurationTargetCount { get; } = ConfigurationTarget.All.Count;
    public bool FlashlightBindingsVerified { get; } = false;
    public IReadOnlyList<DefinitionIssue> Issues { get; init; } = Array.Empty<DefinitionIssue>();
}
