using System.IO;
using BepInEx;

namespace PreLuna.WeaponAttachments.Core;

internal sealed class BootstrapPaths
{
    public string GameRoot { get; } = Path.GetFullPath(Paths.GameRootPath);
    public string FrameworkRoot { get; } = Path.GetFullPath(Paths.BepInExRootPath);
    public string ConfigurationFile { get; } = Path.Combine(Paths.ConfigPath, PluginMetadata.Guid + ".cfg");
    public string DataDirectory { get; } = Path.Combine(Paths.ConfigPath, PluginMetadata.Guid);
    public string CatalogFile => Path.Combine(DataDirectory, "attachments.catalog.json");
    public string LoadoutsFile => Path.Combine(DataDirectory, "loadouts.json");
    public string StartupReport { get; } = Path.Combine(Paths.BepInExRootPath, "WeaponAttachments", "diagnostics", "startup.json");

    public string ReadGameRevision()
    {
        string path = Path.Combine(GameRoot, "revision.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
    }
}
