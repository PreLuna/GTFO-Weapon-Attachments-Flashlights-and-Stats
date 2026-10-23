using BepInEx.Configuration;

namespace PreLuna.WeaponAttachments.Runtime;

internal sealed class PluginSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> WriteStartupReport { get; }
    public ConfigEntry<bool> VerboseDependencyLog { get; }

    public PluginSettings(ConfigFile configuration)
    {
        Enabled = configuration.Bind("General", "Enabled", true,
            "启用配件定义、配装存档与武器实例效果；只有满足当前实例能力要求的选择才执行。");
        WriteStartupReport = configuration.Bind("Diagnostics", "WriteStartupReport", true,
            "在 BepInEx/WeaponAttachments/diagnostics/startup.json 记录本次启动与依赖状态。");
        VerboseDependencyLog = configuration.Bind("Diagnostics", "VerboseDependencyLog", false,
            "在日志中逐项输出依赖版本；启动报告始终记录依赖清单。");
    }
}
