using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PreLuna.WeaponAttachments.Definitions;

public static class CatalogJson
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumAttachments = 2048;

    public static string Serialize(CatalogDocument document) => JsonSerializer.Serialize(document, Options());

    public static CatalogReadResult Read(string json, CapabilityRegistry registry)
    {
        registry.Seal();
        var issues = new List<DefinitionIssue>();
        if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            return Failure("DOCUMENT_SIZE", "$", "目录为空或超过 1 MiB。", issues);
        try
        {
            json = json.TrimStart('\uFEFF');
            using var tree = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            ScanDocument(tree.RootElement, issues);
            if (issues.Any(item => item.Severity == IssueSeverity.Error)) return new(null, issues.AsReadOnly());
            var document = JsonSerializer.Deserialize<CatalogDocument>(json, Options());
            if (document == null) return Failure("DOCUMENT_NULL", "$", "目录必须是 JSON 对象。", issues);
            issues.AddRange(CatalogValidator.Validate(document, registry));
            return new(issues.Any(item => item.Severity == IssueSeverity.Error) ? null : new AttachmentCatalog(document), issues.AsReadOnly());
        }
        catch (JsonException error)
        {
            return Failure("INVALID_JSON", error.Path ?? "$", error.Message, issues);
        }
    }

    private static CatalogReadResult Failure(string code, string path, string message, List<DefinitionIssue> issues)
    {
        issues.Add(new(IssueSeverity.Error, code, path, message));
        return new(null, issues.AsReadOnly());
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true, MaxDepth = 32, PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new StrictEnumConverter<AttachmentSlot>());
        options.Converters.Add(new StrictEnumConverter<ConfigurationTargetKind>());
        options.Converters.Add(new StrictEnumConverter<WeaponScope>());
        options.Converters.Add(new StrictEnumConverter<PercentOperation>());
        return options;
    }

    private sealed class StrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("枚举必须使用名称字符串。");
            string? value = reader.GetString();
            if (value == null || !Enum.GetNames<T>().Contains(value, StringComparer.Ordinal)) throw new JsonException("未知枚举名称：" + value);
            return Enum.Parse<T>(value, false);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private static bool Object(JsonElement value, string path, List<DefinitionIssue> issues, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new(IssueSeverity.Error, "OBJECT_EXPECTED", path, "此处必须是对象。"));
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) issues.Add(new(IssueSeverity.Error, "DUPLICATE_PROPERTY", path + "." + property.Name, "属性重复，拒绝采用最后一个值覆盖前值。"));
            if (allowed.Length > 0 && !allowed.Contains(property.Name, StringComparer.Ordinal))
                issues.Add(new(IssueSeverity.Error, "UNKNOWN_PROPERTY", path + "." + property.Name, "未知字段，可能存在拼写错误。"));
        }
        return true;
    }

    private static void ForArray(JsonElement parent, string name, string path, Action<JsonElement, string> action)
    {
        if (!parent.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return;
        int index = 0;
        foreach (var value in values.EnumerateArray()) action(value, path + "." + name + "[" + index++ + "]");
    }

    private static void OptionalObject(JsonElement parent, string name, string path, Action<JsonElement, string> action)
    {
        if (parent.TryGetProperty(name, out var child) && child.ValueKind != JsonValueKind.Null) action(child, path + "." + name);
    }

    private static void ScanDocument(JsonElement root, List<DefinitionIssue> issues)
    {
        if (!Object(root, "$", issues, "SchemaVersion", "CatalogId", "Attachments")) return;
        if (!root.TryGetProperty("Attachments", out _))
            issues.Add(new(IssueSeverity.Error, "MISSING_ATTACHMENTS", "$.Attachments", "必须提供 Attachments 数组；无自定义配件时使用空数组。"));
        if (root.TryGetProperty("Attachments", out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > MaximumAttachments)
        {
            issues.Add(new(IssueSeverity.Error, "TOO_MANY_ATTACHMENTS", "$.Attachments", "目录定义数超过上限。"));
            return;
        }
        ForArray(root, "Attachments", "$", (entry, path) =>
        {
            if (!Object(entry, path, issues, "Id", "DisplayName", "Slot", "Applicability", "Modifiers", "Mechanisms", "Flashlight", "VisualBinding")) return;
            OptionalObject(entry, "Applicability", path, (value, at) =>
            {
                if (!Object(value, at, issues, "Targets", "WeaponScope", "Weapons", "ExcludedWeapons", "RequiredWeaponCapabilities")) return;
                ForArray(value, "Weapons", at, (weapon, key) => ScanWeapon(weapon, key, issues));
                ForArray(value, "ExcludedWeapons", at, (weapon, key) => ScanWeapon(weapon, key, issues));
            });
            ForArray(entry, "Modifiers", path, (value, at) =>
            {
                if (!Object(value, at, issues, "Stat", "Operation", "Percent", "Condition")) return;
                OptionalObject(value, "Condition", at, (condition, key) => ScanCondition(condition, key, issues));
            });
            ForArray(entry, "Mechanisms", path, (value, at) =>
            {
                if (!Object(value, at, issues, "Capability", "Parameters", "Condition")) return;
                OptionalObject(value, "Parameters", at, (parameters, key) => Object(parameters, key, issues));
                OptionalObject(value, "Condition", at, (condition, key) => ScanCondition(condition, key, issues));
            });
            OptionalObject(entry, "Flashlight", path, (value, at) =>
            {
                if (!Object(value, at, issues, "DataPackId", "SourcePartId", "SettingsId", "SourceWeapon")) return;
                OptionalObject(value, "SourceWeapon", at, (weapon, key) => ScanWeapon(weapon, key, issues));
            });
            OptionalObject(entry, "VisualBinding", path, (value, at) => Object(value, at, issues,
                "Enabled", "BindingId", "MountPointId", "FirstPersonAsset", "ThirdPersonAsset"));
        });
    }

    private static void ScanWeapon(JsonElement value, string path, List<DefinitionIssue> issues) =>
        Object(value, path, issues, "DataPackId", "OfflineGearId", "VariantKey", "ArchetypeId");

    private static void ScanCondition(JsonElement value, string path, List<DefinitionIssue> issues)
    {
        if (!Object(value, path, issues, "Id", "Parameters")) return;
        OptionalObject(value, "Parameters", path, (parameters, key) => Object(parameters, key, issues));
    }
}
