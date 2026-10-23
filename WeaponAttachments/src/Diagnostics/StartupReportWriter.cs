using System.IO;
using System.Text;
using System.Text.Json;

namespace PreLuna.WeaponAttachments.Diagnostics;

internal static class StartupReportWriter
{
    public static void Write(string destination, StartupReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, destination, true);
    }
}
