using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Loadouts;

public static class LoadoutJson
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumLoadouts = 4096;

    public static string Serialize(LoadoutDocument document)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true, MaxDepth = 32,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return JsonSerializer.Serialize(document, options);
    }

    public static LoadoutReadResult Read(string json, string expectedCatalogId)
    {
        try
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw Bad("DOCUMENT_SIZE", "$", "存档为空或超过 4 MiB。");
            using var tree = JsonDocument.Parse(json.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 32 });
            var root = tree.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("SchemaVersion", out var version) &&
                version.TryGetInt32(out int number) && number != 1)
                throw Bad("UNSUPPORTED_VERSION", "$.SchemaVersion", "未知存档版本，保留文件并禁止覆盖。");
            var obj = Object(root, "$", "SchemaVersion", "CatalogId", "Revision", "Loadouts");
            if (Integer(Required(obj, "SchemaVersion", "$"), "$.SchemaVersion") != 1)
                throw Bad("UNSUPPORTED_VERSION", "$.SchemaVersion", "只支持存档版本 1。");
            string catalogId = Id(Required(obj, "CatalogId", "$"), "$.CatalogId");
            if (catalogId != expectedCatalogId) throw Bad("CATALOG_MISMATCH", "$.CatalogId", "存档属于另一配件目录，禁止覆盖。");
            long revision = Integer(Required(obj, "Revision", "$"), "$.Revision");
            var list = Required(obj, "Loadouts", "$");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumLoadouts)
                throw Bad("LOADOUT_LIST", "$.Loadouts", "配装必须是数组，最多 4096 条。");
            var entries = new List<SavedLoadout>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in list.EnumerateArray())
            {
                string at = "$.Loadouts[" + entries.Count + "]";
                var fields = Object(entry, at, "Target", "Weapon", "Selections");
                var targetFields = Object(Required(fields, "Target", at), at + ".Target", "Kind", "BotSlot");
                var target = new SavedTarget
                {
                    Kind = EnumValue<ConfigurationTargetKind>(Required(targetFields, "Kind", at + ".Target"), at + ".Target.Kind"),
                    BotSlot = targetFields.TryGetValue("BotSlot", out var bot) && bot.ValueKind != JsonValueKind.Null ?
                        checked((int)Integer(bot, at + ".Target.BotSlot")) : null
                };
                ConfigurationTarget actualTarget;
                try { actualTarget = target.ToTarget(); }
                catch (ArgumentException error) { throw Bad("TARGET", at + ".Target", error.Message); }
                var weaponFields = Object(Required(fields, "Weapon", at), at + ".Weapon", "DataPackId", "OfflineGearId", "VariantKey", "ArchetypeId");
                var weapon = new WeaponReference
                {
                    DataPackId = Id(Required(weaponFields, "DataPackId", at + ".Weapon"), at + ".Weapon.DataPackId"),
                    OfflineGearId = OptionalId(weaponFields, "OfflineGearId", at + ".Weapon"),
                    ArchetypeId = OptionalId(weaponFields, "ArchetypeId", at + ".Weapon"),
                    VariantKey = weaponFields.TryGetValue("VariantKey", out var variant) && variant.ValueKind != JsonValueKind.Null ?
                        Id(variant, at + ".Weapon.VariantKey") : null
                };
                WeaponIdentity identity;
                try { identity = weapon.ToIdentity(); }
                catch (ArgumentException error) { throw Bad("WEAPON_IDENTITY", at + ".Weapon", error.Message); }
                if (!keys.Add(Key(actualTarget, identity))) throw Bad("DUPLICATE_LOADOUT", at, "对象与武器组合重复，不能静默覆盖。");
                var selected = Required(fields, "Selections", at);
                if (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() > SlotCatalog.All.Count)
                    throw Bad("SELECTIONS", at + ".Selections", "每条配装最多为 11 个槽位保存选择。");
                var selections = new List<SavedSelection>();
                var slots = new HashSet<AttachmentSlot>();
                foreach (var item in selected.EnumerateArray())
                {
                    string selectionPath = at + ".Selections[" + selections.Count + "]";
                    var values = Object(item, selectionPath, "Slot", "AttachmentId");
                    var slot = EnumValue<AttachmentSlot>(Required(values, "Slot", selectionPath), selectionPath + ".Slot");
                    if (SlotCatalog.Find(slot) == null || !slots.Add(slot))
                        throw Bad("SLOT", selectionPath + ".Slot", "未知槽位或重复的槽位选择。");
                    selections.Add(new SavedSelection { Slot = slot, AttachmentId = Id(Required(values, "AttachmentId", selectionPath), selectionPath + ".AttachmentId") });
                }
                entries.Add(new SavedLoadout { Target = target, Weapon = weapon, Selections = selections.AsReadOnly() });
            }
            return new(new LoadoutDocument { CatalogId = catalogId, Revision = revision, Loadouts = entries.AsReadOnly() }, Array.Empty<LoadoutIssue>());
        }
        catch (FormatFailure error) { return Failure(error.Code, error.Path, error.Message); }
        catch (JsonException error) { return Failure("INVALID_JSON", error.Path ?? "$", error.Message); }
        catch (OverflowException) { return Failure("INTEGER_RANGE", "$", "整数超出允许范围。"); }
        catch (InvalidOperationException) { return Failure("VALUE_TYPE", "$", "字段的 JSON 类型不正确。"); }
    }

    internal static string Key(ConfigurationTarget target, WeaponIdentity weapon) => target.StableKey + "||" + weapon.StableKey;
    internal static WeaponReference Reference(WeaponIdentity weapon) => new()
        { DataPackId = weapon.DataPackId, OfflineGearId = weapon.OfflineGearId, VariantKey = weapon.VariantKey, ArchetypeId = weapon.ArchetypeId };
    internal static LoadoutDocument Freeze(LoadoutDocument document) => document with
    {
        Loadouts = Array.AsReadOnly(document.Loadouts.Select(item => item with
            { Selections = Array.AsReadOnly(item.Selections.ToArray()) }).ToArray())
    };

    private static Dictionary<string, JsonElement> Object(JsonElement value, string path, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Bad("OBJECT_REQUIRED", path, "需要 JSON 对象。");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!fields.TryAdd(field.Name, field.Value)) throw Bad("DUPLICATE_FIELD", path + "." + field.Name, "JSON 字段重复。");
            if (!allowed.Contains(field.Name, StringComparer.Ordinal)) throw Bad("UNKNOWN_FIELD", path + "." + field.Name, "未知存档字段。");
        }
        return fields;
    }
    private static JsonElement Required(Dictionary<string, JsonElement> fields, string key, string path) =>
        fields.TryGetValue(key, out var value) ? value : throw Bad("REQUIRED_FIELD", path + "." + key, "缺少必需字段。");
    private static string Id(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.String && DefinitionIds.IsValid(value.GetString()) ? value.GetString()! :
            throw Bad("IDENTIFIER", path, "需要稳定的小写标识。");
    private static long Integer(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) && result >= 0 ? result :
            throw Bad("INTEGER_RANGE", path, "需要非负 64 位整数。");
    private static uint? OptionalId(Dictionary<string, JsonElement> fields, string key, string path)
    {
        if (!fields.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        long number = Integer(value, path + "." + key);
        if (number == 0 || number > uint.MaxValue) throw Bad("WEAPON_IDENTITY", path + "." + key, "武器记录 ID 必须是正的 32 位整数。");
        return (uint)number;
    }
    private static T EnumValue<T>(JsonElement value, string path) where T : struct, Enum =>
        value.ValueKind == JsonValueKind.String && Enum.GetNames<T>().Contains(value.GetString(), StringComparer.Ordinal) &&
        Enum.TryParse<T>(value.GetString(), out var result) ? result : throw Bad("ENUM_VALUE", path, "未知枚举，必须使用精确名称。");
    private static LoadoutReadResult Failure(string code, string path, string message) =>
        new(null, Array.AsReadOnly(new[] { new LoadoutIssue(code, path, message) }));
    private static FormatFailure Bad(string code, string path, string message) => new(code, path, message);
    private sealed class FormatFailure : Exception
    {
        public string Code { get; }
        public string Path { get; }
        public FormatFailure(string code, string path, string message) : base(message) { Code = code; Path = path; }
    }
}
