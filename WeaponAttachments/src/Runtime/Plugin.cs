using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using PreLuna.WeaponAttachments.Core;
using PreLuna.WeaponAttachments.Diagnostics;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;
using PreLuna.WeaponAttachments.Native;
using PreLuna.WeaponAttachments.Interface;

namespace PreLuna.WeaponAttachments.Runtime;

[BepInPlugin(PluginMetadata.Guid, PluginMetadata.Name, PluginMetadata.Version)]
[BepInProcess("GTFO.exe")]
[BepInDependency(PluginMetadata.GtfoApi, PluginMetadata.GtfoApiVersion)]
[BepInDependency(PluginMetadata.Mtfo, PluginMetadata.MtfoVersion)]
[BepInDependency(PluginMetadata.ModifierApi, PluginMetadata.ModifierApiVersion)]
[BepInDependency(PluginMetadata.AmorLib, PluginMetadata.AmorLibVersion)]
[BepInDependency(PluginMetadata.Ewc, PluginMetadata.EwcVersion)]
[BepInDependency(PluginMetadata.StatShower, PluginMetadata.StatShowerVersion)]
[BepInDependency(PluginMetadata.PartialData, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BasePlugin
{
    private bool _loaded;
    public LoadoutManager? Loadouts { get; private set; }
    public ModifierCalculator? Calculator { get; private set; }
    public NativeRuntime? Native { get; private set; }
    public AttachmentInterfaceRuntime? Interface { get; private set; }

    public override void Load()
    {
        if (_loaded)
            return;

        var settings = new PluginSettings(Config);
        var paths = new BootstrapPaths();
        var dependencies = DependencyProbe.Inspect(IL2CPPChainloader.Instance.Plugins);
        var registry = CapabilityRegistry.CreateDefaults();
        CatalogFileResult? catalog = settings.Enabled.Value ? Definitions.CatalogFile.Load(paths.CatalogFile, registry) : null;
        var catalogDiagnostic = catalog == null ? new CatalogDiagnostic { FilePath = paths.CatalogFile } : new CatalogDiagnostic
        {
            Status = catalog.Status, FilePath = paths.CatalogFile, CatalogId = catalog.Catalog.CatalogId,
            SlotCount = SlotCatalog.All.Count, OriginalCount = catalog.Catalog.OriginalCount, CustomCount = catalog.Catalog.CustomCount,
            KnownStatCount = registry.Stats.Count, KnownMechanismCount = registry.Mechanisms.Count,
            KnownConditionCount = registry.Conditions.Count, Issues = catalog.Issues
        };
        Loadouts = catalog == null ? null : LoadoutManager.Open(catalog.Catalog, new JsonLoadoutStorage(paths.LoadoutsFile, catalog.Catalog.CatalogId));
        Calculator = catalog == null ? null : new ModifierCalculator(catalog.Catalog, registry);
        if (catalog != null)
        {
            Native = new NativeRuntime(catalog.Catalog, Loadouts!, Calculator!, Config, Log,
                dependencies.FirstOrDefault(item => item.Guid == PluginMetadata.Ewc)?.EwcInitialized == true);
            Native.Install();
            AddComponent<NativeOwnershipDriver>();
            Interface = new(catalog.Catalog, Loadouts!, Calculator!, Native, Log);
            Interface.Install();
            if (Interface.HookCount > 0) AddComponent<AttachmentInterfaceDriver>();
        }
        var report = new StartupReport
        {
            Status = !settings.Enabled.Value ? "Disabled" : !Loadouts!.CanWrite ? "LoadoutsReadOnly" :
                catalog!.Issues.Any(item => item.Severity == IssueSeverity.Error) ? "DefinitionsFallback" : "LoadoutsLoaded",
            GameRevision = paths.ReadGameRevision(),
            Paths = paths,
            Dependencies = dependencies,
            Catalog = catalogDiagnostic,
            Loadouts = Loadouts == null ? new LoadoutDiagnostic { FilePath = paths.LoadoutsFile } : LoadoutDiagnostic.From(Loadouts, paths.LoadoutsFile),
            Calculation = Calculator == null ? new CalculationDiagnostic() : new CalculationDiagnostic
                { Status = "NativeAdaptersInstalled", CalculatorReady = true, KnownStatCount = Calculator.KnownStatCount,
                    NativeUpstreamSourcesBound = Native?.InstalledPatches.Count ?? 0, GameplayExecutors = Native == null ? 0 : 1 },
            Native = Native == null ? new NativeDiagnostic() : new NativeDiagnostic
                { Status = Native.Errors.Count == 0 ? "Installed" : "PartialInstall", DataPackId = Native.DataPackId,
                    EwcActive = Native.EwcActive, InstalledPatches = Native.InstalledPatches, PatchErrors = Native.Errors,
                    PatchOrder = Native.GetPatchOrder() },
            WeaponEffectsImplemented = Native != null,
            GameInterfaceHooks = Interface?.HookCount ?? 0, GameInterfaceError = Interface?.LastError ?? "",
            OwnershipPolicy = Native == null ? "Disabled" : "LocalPlayerAndHostBotsOnly", OwnershipAuditIntervalMs = Native == null ? 0 : 100
        };

        if (settings.VerboseDependencyLog.Value)
            foreach (var dependency in dependencies)
                Log.LogInfo($"依赖 {dependency.Guid}: {dependency.InstalledVersion ?? "未安装"}");

        if (settings.WriteStartupReport.Value)
        {
            try
            {
                StartupReportWriter.Write(paths.StartupReport, report);
                Log.LogInfo($"启动报告：{paths.StartupReport}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning($"无法写入启动报告，继续仅记录日志：{error.Message}");
            }
        }

        if (!settings.Enabled.Value)
        {
            Log.LogInfo("自定义武器配件已关闭；没有注册武器补丁或配装消息。");
            return;
        }

        foreach (var issue in catalog!.Issues)
        {
            string message = $"配件目录 {issue.Code} ({issue.Path})：{issue.Message}";
            if (issue.Severity == IssueSeverity.Error) Log.LogError(message);
            else Log.LogWarning(message);
        }
        if (catalog.Status is "RejectedFallback" or "IoFallback")
            Log.LogWarning("目录未通过校验，已回退为原装项；没有覆盖原配置文件。");
        foreach (var issue in Loadouts!.Issues)
            Log.LogWarning($"配装存档 {issue.Code} ({issue.Path})：{issue.Message}");
        if (report.Loadouts.InvalidSelectionCount > 0)
            Log.LogWarning($"配装中有 {report.Loadouts.InvalidSelectionCount} 个当前不可用的选择，保留记录并暂用原装。");

        var ewc = dependencies.FirstOrDefault(item => item.Guid == PluginMetadata.Ewc);
        if (ewc?.EwcInitialized != true)
            Log.LogWarning("EWC 尚未激活；EWC 条件、穿甲及已有 Trait 相关能力不可用，按实例能力检查限制选择。");

        _loaded = true;
        Log.LogInfo($"自定义武器配件 {PluginMetadata.Version} 第八步启动检查版本已加载；界面接点={Interface?.HookCount ?? 0}，原生接点={Native!.InstalledPatches.Count}；自定义项={catalogDiagnostic.CustomCount}，配装消息=0。");
    }

    public override bool Unload()
    {
        _loaded = false;
        Interface?.Dispose(); Interface = null;
        Native?.Dispose();
        Native = null;
        Loadouts = null;
        Calculator = null;
        return true;
    }
}
