using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PreLuna.WeaponAttachments.Core;
using PreLuna.WeaponAttachments.Modules;

namespace PreLuna.WeaponAttachments.Diagnostics;

internal sealed class StartupReport
{
    public int SchemaVersion { get; } = 8;
    public string ReleaseValidationScope { get; } = "StartupOnly";
    public string PluginGuid { get; } = PluginMetadata.Guid;
    public string PluginVersion { get; } = PluginMetadata.Version;
    public int ImplementedStage { get; } = PluginMetadata.ImplementedStage;
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public int ProcessId { get; } = Environment.ProcessId;
    public string ManagedRuntime { get; } = RuntimeInformation.FrameworkDescription;
    public string Status { get; init; } = "InfrastructureLoaded";
    public string GameRevision { get; init; } = "unknown";
    public BootstrapPaths Paths { get; init; } = null!;
    public IReadOnlyList<DependencyStatus> Dependencies { get; init; } = Array.Empty<DependencyStatus>();
    public IReadOnlyList<ModuleDescriptor> Modules { get; } = ModuleCatalog.All;
    public CatalogDiagnostic Catalog { get; init; } = new();
    public LoadoutDiagnostic Loadouts { get; init; } = new();
    public CalculationDiagnostic Calculation { get; init; } = new();
    public NativeDiagnostic Native { get; init; } = new();
    public bool WeaponEffectsImplemented { get; init; }
    public int GameInterfaceHooks { get; init; }
    public string GameInterfaceError { get; init; } = "";
    public string OwnershipPolicy { get; init; } = "Disabled";
    public int OwnershipAuditIntervalMs { get; init; }
    public int WeaponPatchesRegistered => Native.InstalledPatches.Count;
    public int CustomNetworkMessagesRegistered { get; } = 0;
    public bool ModelsOrAnimationsChanged { get; } = false;
    public int FormalAttachmentCount => Catalog.CustomCount;
}
