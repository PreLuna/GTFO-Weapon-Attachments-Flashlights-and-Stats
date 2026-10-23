using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;

internal static class Stage3Loadouts
{
    private static readonly List<object> Results = new();
    private static int _failed;
    private static string _root = "";
    private static int _sequence;
    private static readonly ConfigurationTarget Me = ConfigurationTarget.LocalPlayer;
    private static readonly WeaponIdentity Gun = new("test.pack", 1, archetypeId: 18);
    private static readonly AttachmentSlot Muzzle = AttachmentSlot.Muzzle;
    private static readonly string[] Ids = { "test.one", "test.two", "test.three", "test.four" };
    private static AttachmentCatalog Catalog => MakeCatalog(Ids.Select(id => Part(id)).Concat(new[]
    {
        Part("test.bot") with { Applicability = Rules(ConfigurationTargetKind.HostBot) },
        Part("test.player") with { Applicability = Rules(ConfigurationTargetKind.LocalPlayer) },
        Part("test.othergun") with { Applicability = new ApplicabilityDefinition
            { Targets = new[] { ConfigurationTargetKind.LocalPlayer }, WeaponScope = WeaponScope.ListedOnly,
              Weapons = new[] { new WeaponReference { DataPackId = "test.pack", OfflineGearId = 2 } } } },
        Part("test.tagged") with { Applicability = Rules() with { RequiredWeaponCapabilities = new[] { "test.tag" } } }
    }).ToArray());

    private static int Main(string[] args)
    {
        _root = args[1];
        Directory.CreateDirectory(_root);
        string formalBefore = File.ReadAllText(args[2]);
        Run("首次打开建立空存档，四对象不预填配件", () =>
        {
            var (manager, path) = Open();
            Check(manager.Status == "CreatedDefaults" && manager.CanWrite && manager.SavedLoadoutCount == 0 && manager.Revision == 0);
            Check(File.Exists(path) && manager.GetCommitted(Me, Gun).Selections.Count == 11);
        });
        Run("草稿选择不写盘也不改变已保存配装", () =>
        {
            var (manager, path) = Open(); string before = File.ReadAllText(path);
            var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]);
            Check(draft.IsDirty && State(draft.Snapshot).SelectedId == Ids[0]);
            Check(State(manager.GetCommitted(Me, Gun)).SelectedId == "original.muzzle" && File.ReadAllText(path) == before);
        });
        Run("取消草稿恢复最新已保存选择", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun);
            Select(draft, Ids[0]); Saved(draft); string before = File.ReadAllText(path);
            Select(draft, Ids[1]); draft.Cancel();
            Check(!draft.IsDirty && State(draft.Snapshot).RequestedId == Ids[0] && File.ReadAllText(path) == before);
        });
        Run("应用后重开服务恢复同一配装", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            var reopened = OpenAt(path);
            Check(reopened.Revision == 1 && State(reopened.GetCommitted(Me, Gun)).RequestedId == Ids[0] && !draft.IsDirty);
        });
        Run("同一武器四对象可并行编辑并分别保存", () =>
        {
            var (manager, path) = Open();
            var drafts = ConfigurationTarget.All.Select(target => manager.BeginEdit(target, Gun)).ToArray();
            for (int i = 0; i < 4; i++) Select(drafts[i], Ids[i]);
            for (int i = 0; i < 4; i++) Saved(drafts[i]);
            var reopened = OpenAt(path);
            Check(reopened.SavedLoadoutCount == 4);
            for (int i = 0; i < 4; i++) Check(State(reopened.GetCommitted(ConfigurationTarget.All[i], Gun)).RequestedId == Ids[i]);
        });
        Run("相同Archetype的两把武器不串配装", () =>
        {
            var (manager, _) = Open(); var other = new WeaponIdentity("test.pack", 2, archetypeId: 18);
            var a = manager.BeginEdit(Me, Gun); var b = manager.BeginEdit(Me, other);
            Select(a, Ids[0]); Select(b, Ids[1]); Saved(a); Saved(b);
            Check(State(manager.GetCommitted(Me, Gun)).RequestedId == Ids[0]);
            Check(State(manager.GetCommitted(Me, other)).RequestedId == Ids[1]);
        });
        Run("数据包与变体分别保存且Archetype描述变化可恢复", () =>
        {
            var (manager, path) = Open();
            var guns = new[] { Gun, new WeaponIdentity("other.pack", 1), new WeaponIdentity("test.pack", 1, "variant.a"), new WeaponIdentity("test.pack", null, "variant.a") };
            for (int i = 0; i < guns.Length; i++) { var draft = manager.BeginEdit(Me, guns[i]); Select(draft, Ids[i]); Saved(draft); }
            var reopened = OpenAt(path);
            for (int i = 0; i < guns.Length; i++) Check(State(reopened.GetCommitted(Me, guns[i])).RequestedId == Ids[i]);
            Check(State(reopened.GetCommitted(Me, new WeaponIdentity("test.pack", 1, archetypeId: 99))).RequestedId == Ids[0]);
        });
        Run("切换对象后草稿各自保留，取消一个不影响另一个", () =>
        {
            var (manager, _) = Open();
            var me = manager.BeginEdit(Me, Gun); var bot = manager.BeginEdit(ConfigurationTarget.ForBot(1), Gun);
            Select(me, Ids[0]); Select(bot, Ids[1]); me.Cancel();
            Check(!me.IsDirty && bot.IsDirty && State(bot.Snapshot).RequestedId == Ids[1]);
        });
        Run("恢复原装先作用于草稿，取消可撤回", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            string before = File.ReadAllText(path); draft.ResetToOriginal();
            Check(draft.IsDirty && State(draft.Snapshot).RequestedId == "original.muzzle");
            Check(File.ReadAllText(path) == before); draft.Cancel(); Check(State(draft.Snapshot).RequestedId == Ids[0]);
        });
        Run("恢复原装应用并重启后只改变指定对象武器", () =>
        {
            var (manager, path) = Open(); var me = manager.BeginEdit(Me, Gun); var bot = manager.BeginEdit(ConfigurationTarget.ForBot(2), Gun);
            Select(me, Ids[0]); Select(bot, Ids[1]); Saved(me); Saved(bot); me.ResetToOriginal(); Saved(me);
            var reopened = OpenAt(path); Check(State(reopened.GetCommitted(Me, Gun)).RequestedId == "original.muzzle");
            Check(State(reopened.GetCommitted(ConfigurationTarget.ForBot(2), Gun)).RequestedId == Ids[1]);
        });
        Run("无改动应用不重复递增修订号或写盘", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            string before = File.ReadAllText(path); Check(draft.Apply().Code == "NO_CHANGES");
            Check(manager.Revision == 1 && File.ReadAllText(path) == before);
        });
        Run("同一配装的旧编辑器不能覆盖新应用", () =>
        {
            var (manager, _) = Open(); var a = manager.BeginEdit(Me, Gun); var b = manager.BeginEdit(Me, Gun);
            Select(a, Ids[0]); Select(b, Ids[1]); Saved(a);
            Check(b.Apply().Code == "STALE_DRAFT"); b.Cancel();
            Check(State(b.Snapshot).RequestedId == Ids[0] && !b.IsDirty);
        });
        Run("显式重新读取使旧草稿失效", () =>
        {
            var (manager, _) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); manager.Reload();
            Check(draft.Apply().Code == "STALE_DRAFT"); draft.Cancel(); Select(draft, Ids[1]); Saved(draft);
        });
        Run("不同服务实例不能互相覆盖存档", () =>
        {
            var (manager, path) = Open(); var other = OpenAt(path);
            var a = manager.BeginEdit(Me, Gun); var b = other.BeginEdit(ConfigurationTarget.ForBot(1), Gun);
            Select(a, Ids[0]); Select(b, Ids[1]); Saved(a);
            Check(b.Apply().Code == "EXTERNAL_CHANGE" && b.IsDirty && other.Revision == 0);
            other.Reload(); b.Cancel(); Select(b, Ids[1]); Saved(b);
            Check(OpenAt(path).SavedLoadoutCount == 2);
        });
        Run("外部修改即使修订号相同也阻止覆盖", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]);
            string changed = File.ReadAllText(path) + "\n"; File.WriteAllText(path, changed);
            Check(draft.Apply().Code == "EXTERNAL_CHANGE" && File.ReadAllText(path) == changed && draft.IsDirty);
        });
        Run("保存失败保留旧已应用状态及未保存草稿", () =>
        {
            string path = NewPath(); var storage = new FailingStorage(new JsonLoadoutStorage(path, Catalog.CatalogId));
            var manager = LoadoutManager.Open(Catalog, storage); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]);
            string before = File.ReadAllText(path); storage.Fail = true;
            Check(!draft.Apply().Success && draft.IsDirty && manager.Revision == 0 && File.ReadAllText(path) == before);
            Check(State(manager.GetCommitted(Me, Gun)).RequestedId == "original.muzzle");
            storage.Fail = false; Saved(draft);
        });
        Run("真实文件锁冲突不会损坏存档", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]);
            string before = File.ReadAllText(path);
            using (var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Check(!draft.Apply().Success);
            Check(File.ReadAllText(path) == before && draft.IsDirty); Saved(draft);
        });
        Run("保存生成上一版有效备份且无遗留临时文件", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            Select(draft, Ids[1]); Saved(draft);
            var backup = LoadoutJson.Read(File.ReadAllText(path + ".bak"), Catalog.CatalogId);
            Check(backup.Success && backup.Document!.Revision == 1 && backup.Document.Loadouts[0].Selections.Any(s => s.AttachmentId == Ids[0]));
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length == 0);
        });
        Run("损坏主文件读取备份且禁止覆盖损坏证据", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft); Select(draft, Ids[1]); Saved(draft);
            File.WriteAllText(path, "{broken"); var recovered = OpenAt(path);
            Check(!recovered.CanWrite && recovered.Status == "BackupReadOnly" && State(recovered.GetCommitted(Me, Gun)).RequestedId == Ids[0]);
            var edit = recovered.BeginEdit(Me, Gun); Select(edit, Ids[2]); Check(edit.Apply().Code == "READ_ONLY");
            Check(File.ReadAllText(path) == "{broken");
        });
        Run("无备份的损坏存档回退原装且不覆盖", () =>
        {
            string path = NewPath(); File.WriteAllText(path, "null"); var manager = OpenAt(path);
            Check(!manager.CanWrite && manager.Status == "RejectedReadOnly" && File.ReadAllText(path) == "null");
        });
        Run("主文件丢失时读取已有备份，不创建新空档", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft); Select(draft, Ids[1]); Saved(draft);
            File.Delete(path); var reopened = OpenAt(path);
            Check(reopened.Status == "BackupReadOnly" && !File.Exists(path) && State(reopened.GetCommitted(Me, Gun)).RequestedId == Ids[0]);
        });
        Run("未来版本不降级读取旧备份也不覆盖", () =>
        {
            var (_, path) = Open(); string future = "{\"SchemaVersion\":2,\"FutureField\":true}";
            File.WriteAllText(path + ".bak", LoadoutJson.Serialize(Document()));
            File.WriteAllText(path, future); var manager = OpenAt(path);
            Check(manager.Status == "ReadOnlyUnsupported" && !manager.CanWrite && File.ReadAllText(path) == future);
        });
        Run("其他目录的存档不被当前目录接管", () =>
        {
            string path = NewPath(); string foreign = LoadoutJson.Serialize(Document() with { CatalogId = "other.catalog" });
            File.WriteAllText(path, foreign); var manager = OpenAt(path);
            Check(!manager.CanWrite && manager.Issues.Any(i => i.Code == "CATALOG_MISMATCH") && File.ReadAllText(path) == foreign);
        });
        Run("存档路径为目录时只读失败而不崩溃", () =>
        {
            string path = NewPath(); Directory.CreateDirectory(path); var manager = OpenAt(path);
            Check(!manager.CanWrite && manager.Status == "IoFallback");
        });
        Run("删除的配件保留请求记录并暂用原装", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            var reopened = OpenAt(path, MakeCatalog());
            var state = State(reopened.GetCommitted(Me, Gun));
            Check(state.RequestedId == Ids[0] && state.SelectedId == "original.muzzle" && state.Code == "UNKNOWN_ATTACHMENT");
            Check(File.ReadAllText(path).Contains(Ids[0]));
        });
        Run("配件重新出现可自动恢复选择", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            Check(!State(OpenAt(path, MakeCatalog()).GetCommitted(Me, Gun)).Applicable);
            Check(State(OpenAt(path).GetCommitted(Me, Gun)).SelectedId == Ids[0]);
        });
        Run("已保存配件改到其他槽位时不跨槽生效", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            var changed = MakeCatalog(Part(Ids[0]) with { Slot = AttachmentSlot.Barrel });
            Check(State(OpenAt(path, changed).GetCommitted(Me, Gun)).Code == "WRONG_SLOT");
        });
        Run("更新适用范围后保留选择并回退原装", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            var changed = MakeCatalog(Part(Ids[0]) with { Applicability = Rules(ConfigurationTargetKind.HostBot) });
            Check(State(OpenAt(path, changed).GetCommitted(Me, Gun)).Code == "WRONG_TARGET");
        });
        Run("无执行器可保存选择但实际解析仍为原装", () =>
        {
            var (manager, _) = Open(); var draft = manager.BeginEdit(Me, Gun);
            Check(draft.Select(Muzzle, Ids[0]).Code == "PENDING_EXECUTOR"); Saved(draft);
            var state = State(manager.GetCommitted(Me, Gun));
            Check(state.Applicable && !state.RuntimeReady && state.SelectedId == Ids[0] && state.EffectiveId == "original.muzzle");
            Check(state.MissingCapabilities.Contains("stat.damage"));
        });
        Run("执行能力由调用方提供且不能从存档伪造", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun, implementedCapabilities: new[] { "stat.damage" });
            Select(draft, Ids[0]); Check(State(draft.Snapshot).RuntimeReady); Saved(draft);
            Check(!File.ReadAllText(path).Contains("stat.damage") && !State(OpenAt(path).GetCommitted(Me, Gun)).RuntimeReady);
        });
        Run("新选择的错误对象被拒绝且草稿不变", () =>
        {
            var (manager, _) = Open(); var draft = manager.BeginEdit(Me, Gun);
            Check(draft.Select(Muzzle, "test.bot").Code == "WRONG_TARGET" && !draft.IsDirty);
            Check(manager.BeginEdit(ConfigurationTarget.ForBot(1), Gun).Select(Muzzle, "test.player").Code == "WRONG_TARGET");
        });
        Run("新选择的错误武器和槽位被拒绝", () =>
        {
            var (manager, _) = Open(); var draft = manager.BeginEdit(Me, Gun);
            Check(draft.Select(Muzzle, "test.othergun").Code == "WRONG_WEAPON");
            Check(draft.Select(AttachmentSlot.Barrel, Ids[0]).Code == "WRONG_SLOT" && !draft.IsDirty);
        });
        Run("武器能力要求可查询，缺失时不能选择", () =>
        {
            var (manager, _) = Open();
            Check(!manager.BeginEdit(Me, Gun).Select(Muzzle, "test.tagged").Success);
            Check(manager.BeginEdit(Me, Gun, new[] { "test.tag" }).Select(Muzzle, "test.tagged").Success);
        });
        Run("能力输入复制后不被外部数组更改", () =>
        {
            var (manager, _) = Open(); string[] tags = { "test.tag" };
            var draft = manager.BeginEdit(Me, Gun, tags); tags[0] = "other";
            Check(draft.Select(Muzzle, "test.tagged").Success);
        });
        Run("新选择未知ID或保留镜槽配件被拒绝", () =>
        {
            var (manager, _) = Open(); var draft = manager.BeginEdit(Me, Gun);
            Check(!draft.Select(Muzzle, "test.absent").Success && !draft.Select(AttachmentSlot.Optic, Ids[0]).Success);
            Check(!draft.Select(AttachmentSlot.Unknown, Ids[0]).Success && !draft.IsDirty);
        });
        Run("稀疏存档补齐未保存槽位的原装项", () =>
        {
            string path = NewPath(); File.WriteAllText(path, LoadoutJson.Serialize(Document(Entry(Me, Gun, Ids[0]))));
            var states = OpenAt(path).GetCommitted(Me, Gun).Selections;
            Check(states.Count == 11 && states.Count(s => s.RequestedId.StartsWith("original.")) == 10);
        });
        Run("失效项不因编辑其他槽位而从存档丢失", () =>
        {
            string path = NewPath(); File.WriteAllText(path, LoadoutJson.Serialize(Document(Entry(Me, Gun, "test.absent"))));
            var manager = OpenAt(path, MakeCatalog(Part("test.barrel") with { Slot = AttachmentSlot.Barrel }));
            var draft = manager.BeginEdit(Me, Gun); Check(draft.Select(AttachmentSlot.Barrel, "test.barrel").Success); Saved(draft);
            Check(File.ReadAllText(path).Contains("test.absent") && State(OpenAt(path).GetCommitted(Me, Gun)).RequestedId == "test.absent");
        });
        Run("导出的存档容器和快照不能原位修改", () =>
        {
            var (manager, _) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]); Saved(draft);
            Throws<NotSupportedException>(() => ((IList<SavedLoadout>)manager.Export().Loadouts).Clear());
            Throws<NotSupportedException>(() => ((IList<SavedSelection>)manager.Export().Loadouts[0].Selections).Clear());
            Throws<NotSupportedException>(() => ((IList<SelectionState>)draft.Snapshot.Selections).Clear());
        });
        Run("序列化往返保留四对象和武器变体", () =>
        {
            var entries = ConfigurationTarget.All.Select((target, i) => Entry(target, new WeaponIdentity("test.pack", null, "variant.a"), Ids[i])).ToArray();
            var result = Parse(LoadoutJson.Serialize(Document(entries)));
            Check(result.Success && result.Document!.Loadouts.Count == 4 && result.Document.Loadouts.All(e => e.Weapon.VariantKey == "variant.a"));
        });
        Run("重复对象武器即使Archetype描述不同也拒绝", () =>
            Reject(Document(Entry(Me, Gun), Entry(Me, new WeaponIdentity("test.pack", 1, archetypeId: 99))), "DUPLICATE_LOADOUT"));
        Run("重复槽位不能静默覆盖", () =>
            Reject(Document(Entry(Me, Gun) with { Selections = new[] { Selection(Ids[0]), Selection(Ids[1]) } }), "SLOT"));
        Run("未知槽位不能从存档进入框架", () =>
            Reject(Document(Entry(Me, Gun) with { Selections = new[] { Selection(Ids[0]) with { Slot = AttachmentSlot.Unknown } } }), "SLOT"));
        Run("非法BOT槽和本人与BOT槽混用被拒绝", () =>
        {
            Reject(Document(Entry(Me, Gun) with { Target = new SavedTarget { Kind = ConfigurationTargetKind.HostBot, BotSlot = 4 } }), "TARGET");
            Reject(Document(Entry(Me, Gun) with { Target = new SavedTarget { Kind = ConfigurationTargetKind.LocalPlayer, BotSlot = 1 } }), "TARGET");
        });
        Run("昵称临时身份和单独Archetype不能保存", () =>
            Reject(Document(Entry(Me, Gun) with { Weapon = new WeaponReference { DataPackId = "test.pack", ArchetypeId = 18 } }), "WEAPON_IDENTITY"));
        Run("损坏或重复JSON字段明确报错", () =>
        {
            RejectJson("{bad", "INVALID_JSON");
            RejectJson("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"CatalogId\":\"preluna.weaponattachments\",\"Revision\":0,\"Loadouts\":[]}", "DUPLICATE_FIELD");
        });
        Run("未知根字段和伪造执行器字段被拒绝", () =>
            RejectJson(LoadoutJson.Serialize(Document()).Replace("\"Revision\": 0", "\"Revision\": 0, \"RuntimeReady\": true"), "UNKNOWN_FIELD"));
        Run("嵌套未知或重复字段被拒绝", () =>
        {
            string json = LoadoutJson.Serialize(Document(Entry(Me, Gun)));
            RejectJson(json.Replace("\"Kind\": \"LocalPlayer\"", "\"Kind\": \"LocalPlayer\", \"Kind\": \"HostBot\""), "DUPLICATE_FIELD");
            RejectJson(json.Replace("\"Kind\": \"LocalPlayer\"", "\"Kind\": \"LocalPlayer\", \"Nickname\": \"bot\""), "UNKNOWN_FIELD");
        });
        Run("数值枚举和数字字符串枚举都被拒绝", () =>
        {
            string json = LoadoutJson.Serialize(Document(Entry(Me, Gun)));
            RejectJson(json.Replace("\"LocalPlayer\"", "1"), "ENUM_VALUE");
            RejectJson(json.Replace("\"LocalPlayer\"", "\"1\""), "ENUM_VALUE");
        });
        Run("缺失必要版本和字段不被当作默认成功", () =>
        {
            RejectJson("{}", "REQUIRED_FIELD");
            RejectJson(LoadoutJson.Serialize(Document()).Replace("\"Loadouts\": []", "\"Loadouts\": null"), "LOADOUT_LIST");
        });
        Run("负修订号和超界BOT整数被拒绝", () =>
        {
            RejectJson(LoadoutJson.Serialize(Document()).Replace("\"Revision\": 0", "\"Revision\": -1"), "INTEGER_RANGE");
            string json = LoadoutJson.Serialize(Document(Entry(ConfigurationTarget.ForBot(1), Gun)));
            RejectJson(json.Replace("\"BotSlot\": 1", "\"BotSlot\": 999999999999"), "INTEGER_RANGE");
        });
        Run("最大修订号可以读取但不允许溢出保存", () =>
        {
            string path = NewPath(); File.WriteAllText(path, LoadoutJson.Serialize(Document() with { Revision = long.MaxValue }));
            var manager = OpenAt(path); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]);
            Check(draft.Apply().Code == "REVISION_LIMIT" && manager.Revision == long.MaxValue);
        });
        Run("带BOM文件可以正常读取", () => Check(Parse("\uFEFF" + LoadoutJson.Serialize(Document())).Success));
        Run("超大文件与过多配装不被接受", () =>
        {
            RejectJson(new string(' ', LoadoutJson.MaximumBytes + 1), "DOCUMENT_SIZE");
            Reject(Document(Enumerable.Repeat(Entry(Me, Gun), LoadoutJson.MaximumLoadouts + 1).ToArray()), "LOADOUT_LIST");
        });
        Run("超大真实文件保持原样并只读回退", () =>
        {
            string path = NewPath(); File.WriteAllText(path, new string(' ', LoadoutJson.MaximumBytes + 1));
            Check(!OpenAt(path).CanWrite && new FileInfo(path).Length > LoadoutJson.MaximumBytes);
        });
        Run("提交前文件被改成未来版本时也不能覆盖", () =>
        {
            var (manager, path) = Open(); var draft = manager.BeginEdit(Me, Gun); Select(draft, Ids[0]);
            File.WriteAllText(path, "{\"SchemaVersion\":2}"); Check(draft.Apply().Code == "READ_ONLY");
            Check(File.ReadAllText(path) == "{\"SchemaVersion\":2}" && draft.IsDirty);
        });
        bool unchanged = File.ReadAllText(args[2]) == formalBefore;
        Run("正式配件目录没有被测试写入", () => Check(unchanged && ParseCatalog(formalBefore).CustomCount == 0));
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { Total = Results.Count, Passed = Results.Count - _failed, Failed = _failed,
            Cases = Results, ProductionCatalogChanged = !unchanged }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"配装测试：{Results.Count - _failed}/{Results.Count} 通过，失败 {_failed}。");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try { action(); Results.Add(new { Name = name, Passed = true, Error = "" }); }
        catch (Exception error) { _failed++; Results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine(name + ": " + error.Message); }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("验收断言失败。"); }
    private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("缺少预期异常 " + typeof(T).Name); }
    private static string NewPath() { string dir = Path.Combine(_root, (++_sequence).ToString("D3")); Directory.CreateDirectory(dir); return Path.Combine(dir, "loadouts.json"); }
    private static (LoadoutManager Manager, string Path) Open() { string path = NewPath(); return (OpenAt(path), path); }
    private static LoadoutManager OpenAt(string path, AttachmentCatalog? catalog = null) =>
        LoadoutManager.Open(catalog ?? Catalog, new JsonLoadoutStorage(path, (catalog ?? Catalog).CatalogId));
    private static SelectionState State(LoadoutSnapshot snapshot) => snapshot.Selections.Single(s => s.Slot == Muzzle);
    private static void Select(LoadoutDraft draft, string id) => Check(draft.Select(Muzzle, id).Success);
    private static void Saved(LoadoutDraft draft) => Check(draft.Apply().Code == "SAVED");
    private static ApplicabilityDefinition Rules(params ConfigurationTargetKind[] targets) => new()
        { Targets = targets.Length == 0 ? new[] { ConfigurationTargetKind.LocalPlayer, ConfigurationTargetKind.HostBot } : targets, WeaponScope = WeaponScope.All };
    private static AttachmentDefinition Part(string id) => new() { Id = id, DisplayName = "离线测试用", Slot = Muzzle, Applicability = Rules(),
        Modifiers = new[] { new StatModifierDefinition { Stat = "damage", Operation = PercentOperation.IncreasePercent, Percent = 10 } } };
    private static AttachmentCatalog MakeCatalog(params AttachmentDefinition[] definitions) => ParseCatalog(CatalogJson.Serialize(CatalogDocument.Empty() with { Attachments = definitions }));
    private static AttachmentCatalog ParseCatalog(string json) => CatalogJson.Read(json, CapabilityRegistry.CreateDefaults()).Catalog ?? throw new Exception("测试目录无效。");
    private static SavedSelection Selection(string id) => new() { Slot = Muzzle, AttachmentId = id };
    private static SavedLoadout Entry(ConfigurationTarget target, WeaponIdentity weapon, string? id = null) => new()
    {
        Target = SavedTarget.From(target), Weapon = new WeaponReference { DataPackId = weapon.DataPackId, OfflineGearId = weapon.OfflineGearId,
            VariantKey = weapon.VariantKey, ArchetypeId = weapon.ArchetypeId },
        Selections = id == null ? Array.Empty<SavedSelection>() : new[] { Selection(id) }
    };
    private static LoadoutDocument Document(params SavedLoadout[] entries) => new() { CatalogId = "preluna.weaponattachments", Loadouts = entries };
    private static LoadoutReadResult Parse(string json) => LoadoutJson.Read(json, "preluna.weaponattachments");
    private static void Reject(LoadoutDocument document, string code) => RejectJson(LoadoutJson.Serialize(document), code);
    private static void RejectJson(string json, string code) { var result = Parse(json); Check(!result.Success && result.Issues.Any(i => i.Code == code)); }
    private sealed class FailingStorage : ILoadoutStorage
    {
        private readonly ILoadoutStorage _inner;
        public bool Fail { get; set; }
        public FailingStorage(ILoadoutStorage inner) { _inner = inner; }
        public LoadoutFileState Read() => _inner.Read();
        public LoadoutWriteResult TryWrite(LoadoutDocument document, string expectedFingerprint) =>
            Fail ? new(false, "SIMULATED_IO", "测试写入失败。") : _inner.TryWrite(document, expectedFingerprint);
    }
}
