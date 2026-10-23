using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PreLuna.WeaponAttachments.Definitions;

public sealed class CatalogFileResult
{
    public AttachmentCatalog Catalog { get; }
    public string Status { get; }
    public IReadOnlyList<DefinitionIssue> Issues { get; }
    internal CatalogFileResult(AttachmentCatalog catalog, string status, IReadOnlyList<DefinitionIssue> issues)
        { Catalog = catalog; Status = status; Issues = issues; }
}

public static class CatalogFile
{
    public static CatalogFileResult Load(string path, CapabilityRegistry registry)
    {
        var fallback = new AttachmentCatalog(CatalogDocument.Empty());
        try
        {
            bool created = !File.Exists(path);
            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                // Never replace an existing user file, including an invalid one.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(CatalogJson.Serialize(CatalogDocument.Empty()));
            }
            if (new FileInfo(path).Length > CatalogJson.MaximumBytes)
                return new(fallback, "RejectedFallback", Array.AsReadOnly(new[]
                    { new DefinitionIssue(IssueSeverity.Error, "DOCUMENT_SIZE", "$", "目录超过 1 MiB，未读取或覆盖原文件。") }));
            var loaded = CatalogJson.Read(File.ReadAllText(path), registry);
            return loaded.Success ? new(loaded.Catalog!, created ? "CreatedDefaults" : "Loaded", loaded.Issues) :
                new(fallback, "RejectedFallback", loaded.Issues);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(fallback, "IoFallback", Array.AsReadOnly(new[]
                { new DefinitionIssue(IssueSeverity.Error, "CATALOG_IO", "$", error.Message) }));
        }
    }
}
