using System.Collections.Generic;

namespace PreLuna.WeaponAttachments.Modules;

internal sealed class ModuleDescriptor
{
    public string Id { get; init; } = "";
    public string Purpose { get; init; } = "";
    public int PlannedStage { get; init; }
    public string Status { get; init; } = "Reserved";
}

internal static class ModuleCatalog
{
    // Runtime adapters are installed; capability and resource checks still apply to each instance.
    public static IReadOnlyList<ModuleDescriptor> All { get; } = new[]
    {
        new ModuleDescriptor { Id = "Definitions", Purpose = "槽位、定义格式、目录读取与校验", PlannedStage = 2, Status = "DefinitionFormatReady" },
        new ModuleDescriptor { Id = "Loadouts", Purpose = "本人及三个人机的独立配装与存档", PlannedStage = 3, Status = "LocalPersistenceReady" },
        new ModuleDescriptor { Id = "Modifiers", Purpose = "基于上游当前值的百分比计算与预览", PlannedStage = 4, Status = "PercentageCalculationReady" },
        new ModuleDescriptor { Id = "Weapons", Purpose = "武器实例后置接入与恢复", PlannedStage = 5, Status = "NativeAdaptersInstalled" },
        new ModuleDescriptor { Id = "Flashlights", Purpose = "原版照明与光斑选装，保留外壳", PlannedStage = 5, Status = "OriginalProfileAdapterInstalled" },
        new ModuleDescriptor { Id = "Interface", Purpose = "游戏内入口与四对象配置面板", PlannedStage = 6, Status = "GamePanelInstalled" },
        new ModuleDescriptor { Id = "Ownership", Purpose = "本人/BOT归属过滤与真人接管清理", PlannedStage = 7, Status = "NativeOwnershipIsolationInstalled" },
        new ModuleDescriptor { Id = "Compatibility", Purpose = "启动兼容检查与交付打包", PlannedStage = 8, Status = "StartupVerificationScope" }
    };
}
