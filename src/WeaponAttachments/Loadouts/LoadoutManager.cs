using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Loadouts;

public sealed class LoadoutManager
{
    private readonly AttachmentCatalog _catalog;
    private readonly ILoadoutStorage _storage;
    private readonly Dictionary<string, long> _recordVersions = new(StringComparer.Ordinal);
    private LoadoutFileState _file;
    private long _epoch;
    public string Status => _file.Status;
    public bool CanWrite => _file.CanWrite;
    public long Revision => _file.Document.Revision;
    public long ReloadGeneration => _epoch;
    public int SavedLoadoutCount => _file.Document.Loadouts.Count;
    public IReadOnlyList<LoadoutIssue> Issues => _file.Issues;
    public LoadoutDocument Export() => _file.Document;

    private LoadoutManager(AttachmentCatalog catalog, ILoadoutStorage storage)
    {
        _catalog = catalog;
        _storage = storage;
        _file = ReadAndInitialize();
    }

    public static LoadoutManager Open(AttachmentCatalog catalog, ILoadoutStorage storage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(storage);
        return new(catalog, storage);
    }

    private LoadoutFileState ReadAndInitialize()
    {
        var state = _storage.Read();
        if (state.Status == "MissingDefaults")
        {
            var saved = _storage.TryWrite(state.Document, state.Fingerprint);
            if (!saved.Success)
                return state with { CanWrite = false, Status = "InitializationFailed",
                    Issues = Array.AsReadOnly(new[] { new LoadoutIssue(saved.Code, "$", saved.Message) }) };
            state = _storage.Read() with { Status = "CreatedDefaults" };
        }
        return state with { Document = LoadoutJson.Freeze(state.Document) };
    }

    // Reload explicitly invalidates existing editors instead of overwriting their newer file state.
    public void Reload()
    {
        _file = ReadAndInitialize();
        _epoch++;
        _recordVersions.Clear();
    }

    public LoadoutDraft BeginEdit(ConfigurationTarget target, WeaponIdentity weapon,
        IReadOnlyCollection<string>? weaponCapabilities = null, IReadOnlyCollection<string>? implementedCapabilities = null) =>
        new(this, target, weapon, CopyCapabilities(weaponCapabilities), CopyCapabilities(implementedCapabilities));

    public LoadoutSnapshot GetCommitted(ConfigurationTarget target, WeaponIdentity weapon,
        IReadOnlyCollection<string>? weaponCapabilities = null, IReadOnlyCollection<string>? implementedCapabilities = null) =>
        Resolve(target, weapon, Requested(target, weapon), weaponCapabilities, implementedCapabilities);

    internal (long Epoch, long Version) Token(ConfigurationTarget target, WeaponIdentity weapon) =>
        (_epoch, _recordVersions.GetValueOrDefault(LoadoutJson.Key(target, weapon)));

    internal Dictionary<AttachmentSlot, string> Requested(ConfigurationTarget target, WeaponIdentity weapon)
    {
        var result = SlotCatalog.All.ToDictionary(item => item.Slot, item => SlotCatalog.OriginalId(item.Slot));
        var saved = _file.Document.Loadouts.FirstOrDefault(item =>
            LoadoutJson.Key(item.Target.ToTarget(), item.Weapon.ToIdentity()) == LoadoutJson.Key(target, weapon));
        if (saved != null) foreach (var selection in saved.Selections) result[selection.Slot] = selection.AttachmentId;
        return result;
    }

    internal SelectionState Check(AttachmentSlot slot, string id, ConfigurationTarget target, WeaponIdentity weapon,
        IReadOnlyCollection<string>? weaponCapabilities, IReadOnlyCollection<string>? implementedCapabilities)
    {
        string original = SlotCatalog.OriginalId(slot);
        var entry = _catalog.Find(id);
        if (entry == null) return new(slot, id, original, original, false, false, "UNKNOWN_ATTACHMENT", Array.Empty<string>());
        if (entry.Definition.Slot != slot) return new(slot, id, original, original, false, false, "WRONG_SLOT", Array.Empty<string>());
        var availability = _catalog.CheckAvailability(id, weapon, target, weaponCapabilities, implementedCapabilities);
        return new(slot, id, availability.Applicable ? id : original,
            availability.Applicable && availability.RuntimeReady ? id : original,
            availability.Applicable, availability.RuntimeReady, availability.Code, availability.MissingCapabilities);
    }

    internal LoadoutSnapshot Resolve(ConfigurationTarget target, WeaponIdentity weapon, IReadOnlyDictionary<AttachmentSlot, string> requested,
        IReadOnlyCollection<string>? weaponCapabilities, IReadOnlyCollection<string>? implementedCapabilities) =>
        new(target, weapon, Revision, Array.AsReadOnly(SlotCatalog.All.Select(item =>
            Check(item.Slot, requested[item.Slot], target, weapon, weaponCapabilities, implementedCapabilities)).ToArray()));

    internal LoadoutOperation Apply(LoadoutDraft draft)
    {
        if (draft.BaseToken != Token(draft.Target, draft.Weapon))
            return new(false, "STALE_DRAFT", "同一配装已改变或存档已重新读取；请取消草稿并核对最新选择。");
        if (!CanWrite) return new(false, "READ_ONLY", "当前存档处于保护性只读状态。");
        if (!draft.IsDirty) return new(true, "NO_CHANGES", "选择未改变，没有重复写入存档。");
        var selected = SlotCatalog.All.Select(item => new SavedSelection { Slot = item.Slot, AttachmentId = draft.Requested[item.Slot] }).ToArray();
        string key = LoadoutJson.Key(draft.Target, draft.Weapon);
        var entries = _file.Document.Loadouts.Where(item => LoadoutJson.Key(item.Target.ToTarget(), item.Weapon.ToIdentity()) != key).ToList();
        entries.Add(new SavedLoadout { Target = SavedTarget.From(draft.Target), Weapon = LoadoutJson.Reference(draft.Weapon),
            Selections = Array.AsReadOnly(selected) });
        entries.Sort((left, right) => StringComparer.Ordinal.Compare(
            LoadoutJson.Key(left.Target.ToTarget(), left.Weapon.ToIdentity()), LoadoutJson.Key(right.Target.ToTarget(), right.Weapon.ToIdentity())));
        if (Revision == long.MaxValue) return new(false, "REVISION_LIMIT", "存档修订号已达上限。");
        var next = LoadoutJson.Freeze(_file.Document with { Revision = Revision + 1, Loadouts = entries.AsReadOnly() });
        var saved = _storage.TryWrite(next, _file.Fingerprint);
        if (!saved.Success) return new(false, saved.Code, saved.Message);
        _file = new(next, "Saved", true, saved.Fingerprint, Array.Empty<LoadoutIssue>());
        _recordVersions[key] = Token(draft.Target, draft.Weapon).Version + 1;
        draft.Rebase();
        return new(true, "SAVED", saved.Message);
    }

    private static IReadOnlyCollection<string> CopyCapabilities(IReadOnlyCollection<string>? values)
    {
        if (values == null) return Array.Empty<string>();
        if (values.Any(item => !DefinitionIds.IsValid(item))) throw new ArgumentException("能力标签必须使用稳定标识。");
        return Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
    }
}

// Editors belong to one target and one weapon for their entire lifetime.
public sealed class LoadoutDraft
{
    private readonly LoadoutManager _manager;
    private readonly IReadOnlyCollection<string> _weaponCapabilities;
    private readonly IReadOnlyCollection<string> _implementedCapabilities;
    internal Dictionary<AttachmentSlot, string> Requested { get; private set; } = new();
    private Dictionary<AttachmentSlot, string> _baseline = new();
    internal (long Epoch, long Version) BaseToken { get; private set; }
    public ConfigurationTarget Target { get; }
    public WeaponIdentity Weapon { get; }
    public bool IsDirty => Requested.Any(item => !_baseline.TryGetValue(item.Key, out string? id) || id != item.Value);
    public LoadoutSnapshot Snapshot => _manager.Resolve(Target, Weapon, Requested, _weaponCapabilities, _implementedCapabilities);
    public LoadoutSnapshot CommittedSnapshot => _manager.GetCommitted(Target, Weapon, _weaponCapabilities, _implementedCapabilities);

    internal LoadoutDraft(LoadoutManager manager, ConfigurationTarget target, WeaponIdentity weapon,
        IReadOnlyCollection<string> weaponCapabilities, IReadOnlyCollection<string> implementedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(weapon);
        _manager = manager; Target = target; Weapon = weapon;
        _weaponCapabilities = weaponCapabilities; _implementedCapabilities = implementedCapabilities;
        Rebase();
    }

    public LoadoutOperation Select(AttachmentSlot slot, string attachmentId)
    {
        if (SlotCatalog.Find(slot) == null || !DefinitionIds.IsValid(attachmentId))
            return new(false, "INVALID_SELECTION", "未知槽位或无效配件标识。");
        var state = _manager.Check(slot, attachmentId, Target, Weapon, _weaponCapabilities, _implementedCapabilities);
        if (!state.Applicable) return new(false, state.Code, "配件不适用于此槽位、对象或武器，草稿保持原样。");
        Requested[slot] = attachmentId;
        return new(true, state.RuntimeReady ? "SELECTED" : "PENDING_EXECUTOR",
            state.RuntimeReady ? "草稿选择已更新，尚未写入。" : "草稿可保存此选择；效果执行器尚未接入，实际效果保持原装。");
    }

    public LoadoutOperation Apply() => _manager.Apply(this);
    public void Cancel() => Rebase();
    public void ResetToOriginal()
    {
        foreach (var slot in SlotCatalog.All) Requested[slot.Slot] = SlotCatalog.OriginalId(slot.Slot);
    }
    internal void Rebase()
    {
        Requested = _manager.Requested(Target, Weapon);
        _baseline = new(Requested);
        BaseToken = _manager.Token(Target, Weapon);
    }
}
