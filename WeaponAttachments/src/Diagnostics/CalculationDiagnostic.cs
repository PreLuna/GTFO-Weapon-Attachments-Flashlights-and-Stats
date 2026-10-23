using PreLuna.WeaponAttachments.Modifiers;

namespace PreLuna.WeaponAttachments.Diagnostics;

internal sealed class CalculationDiagnostic
{
    public string Status { get; init; } = "Disabled";
    public bool CalculatorReady { get; init; }
    public int KnownStatCount { get; init; }
    public string LayerId { get; } = ModifierCalculator.LayerId;
    public string CapacityRounding { get; } = "FloorAfterAllLayers";
    public string PermanentStacking { get; } = "SumPercent";
    public string ConditionalStacking { get; } = "MultiplyNamedLayers";
    public int NativeUpstreamSourcesBound { get; init; }
    public int GameplayExecutors { get; init; }
    public bool GameplayApplied { get; } = false;
}
