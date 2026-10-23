using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Loadouts;

public sealed class JsonLoadoutStorage : ILoadoutStorage
{
    public string FilePath { get; }
    public string BackupPath => FilePath + ".bak";
    private readonly string _catalogId;

    public JsonLoadoutStorage(string filePath, string catalogId)
    {
        if (!DefinitionIds.IsValid(catalogId)) throw new ArgumentException("无效目录标识。", nameof(catalogId));
        FilePath = Path.GetFullPath(filePath);
        _catalogId = catalogId;
    }

    public LoadoutFileState Read()
    {
        var empty = new LoadoutDocument { CatalogId = _catalogId };
        try
        {
            if (!File.Exists(FilePath))
            {
                // A directory or an orphan backup is not a pristine, never-saved profile.
                if (Directory.Exists(FilePath)) return Blocked(empty, "IoFallback", "STORE_IO", "存档路径是目录。");
                if (File.Exists(BackupPath)) return Recover(empty, "MISSING_MAIN", "主存档缺失；从备份读取，保留备份并禁止自动覆盖。");
                return new(empty, "MissingDefaults", true, "missing", Array.Empty<LoadoutIssue>());
            }
            byte[] bytes = ReadBounded(FilePath);
            var parsed = LoadoutJson.Read(Encoding.UTF8.GetString(bytes), _catalogId);
            if (parsed.Success) return new(parsed.Document!, "Loaded", true, Hash(bytes), parsed.Issues);
            if (parsed.Issues.Any(item => item.Code is "UNSUPPORTED_VERSION" or "CATALOG_MISMATCH"))
                return new(empty, "ReadOnlyUnsupported", false, Hash(bytes), parsed.Issues);
            return Recover(empty, parsed.Issues[0].Code, parsed.Issues[0].Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Blocked(empty, "IoFallback", "STORE_IO", error.Message);
        }
    }

    private LoadoutFileState Recover(LoadoutDocument empty, string code, string message)
    {
        var issues = new[] { new LoadoutIssue(code, FilePath, message) };
        if (File.Exists(BackupPath))
        {
            try
            {
                var parsed = LoadoutJson.Read(Encoding.UTF8.GetString(ReadBounded(BackupPath)), _catalogId);
                if (parsed.Success) return new(parsed.Document!, "BackupReadOnly", false, "blocked", Array.AsReadOnly(issues));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new(empty, "RejectedReadOnly", false, "blocked", Array.AsReadOnly(issues));
    }

    public LoadoutWriteResult TryWrite(LoadoutDocument document, string expectedFingerprint)
    {
        string? temporary = null;
        try
        {
            string json = LoadoutJson.Serialize(document);
            var checkedDocument = LoadoutJson.Read(json, _catalogId);
            if (!checkedDocument.Success) return new(false, "INVALID_DOCUMENT", checkedDocument.Issues[0].Message);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var gate = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read();
            if (!current.CanWrite) return new(false, "READ_ONLY", "当前存档不可写；请保留并修复原文件后重新读取。");
            if (current.Fingerprint != expectedFingerprint) return new(false, "EXTERNAL_CHANGE", "存档已被其他进程或编辑器修改，请重新读取后再应用。");
            long expectedRevision = current.Status == "MissingDefaults" ? 0 : checked(current.Document.Revision + 1);
            if (document.Revision != expectedRevision) return new(false, "REVISION", "存档修订号不连续。");
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            if (Read().Fingerprint != expectedFingerprint) return new(false, "EXTERNAL_CHANGE", "写入前发现存档变化，未替换原文件。");
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, BackupPath, true);
            else File.Move(temporary, FilePath);
            temporary = null;
            return new(true, "SAVED", "配装已写入本地存档；武器效果需由后续执行层处理。", Hash(bytes));
        }
        catch (OverflowException) { return new(false, "REVISION_LIMIT", "存档修订号已达上限。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, "STORE_IO", error.Message);
        }
        finally
        {
            // Only this invocation's uniquely named, adjacent temporary file can be removed.
            if (temporary != null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > LoadoutJson.MaximumBytes) throw new IOException("存档超过 4 MiB，未读取或覆盖。");
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > LoadoutJson.MaximumBytes) throw new IOException("读取期间存档超过上限。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private LoadoutFileState Blocked(LoadoutDocument empty, string status, string code, string message) =>
        new(empty, status, false, "blocked", Array.AsReadOnly(new[] { new LoadoutIssue(code, FilePath, message) }));
}
