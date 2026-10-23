using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;

namespace PreLuna.WeaponAttachments.Core;

internal sealed class DependencyStatus
{
    public string Guid { get; init; } = "";
    public string MinimumVersion { get; init; } = "";
    public bool Required { get; init; }
    public bool Present { get; init; }
    public string? InstalledVersion { get; init; }
    public bool? EwcInitialized { get; init; }
}

internal static class DependencyProbe
{
    public static IReadOnlyList<DependencyStatus> Inspect(IReadOnlyDictionary<string, PluginInfo> plugins)
    {
        var results = new List<DependencyStatus>();
        Add(PluginMetadata.GtfoApi, PluginMetadata.GtfoApiVersion);
        Add(PluginMetadata.Mtfo, PluginMetadata.MtfoVersion);
        Add(PluginMetadata.ModifierApi, PluginMetadata.ModifierApiVersion);
        Add(PluginMetadata.AmorLib, PluginMetadata.AmorLibVersion);
        Add(PluginMetadata.Ewc, PluginMetadata.EwcVersion);
        Add(PluginMetadata.StatShower, PluginMetadata.StatShowerVersion);
        Add(PluginMetadata.PartialData, "", false);
        return results.AsReadOnly();

        void Add(string guid, string minimumVersion, bool required = true)
        {
            bool present = plugins.TryGetValue(guid, out PluginInfo? info);
            results.Add(new DependencyStatus
            {
                Guid = guid,
                MinimumVersion = minimumVersion,
                Required = required,
                Present = present,
                InstalledVersion = present ? info!.Metadata.Version.ToString() : null,
                EwcInitialized = guid == PluginMetadata.Ewc && present ? ReadEwcState(info!) : null
            });
        }
    }

    private static bool? ReadEwcState(PluginInfo info)
    {
        // EWC can return from Load() without activating. Registry presence is insufficient.
        try
        {
            PropertyInfo? property = info.Instance?.GetType().GetProperty("Loaded", BindingFlags.Public | BindingFlags.Static);
            return property?.PropertyType == typeof(bool) ? (bool?)property.GetValue(null) : null;
        }
        catch (Exception error) when (error is TargetInvocationException or MemberAccessException)
        {
            return null;
        }
    }
}
