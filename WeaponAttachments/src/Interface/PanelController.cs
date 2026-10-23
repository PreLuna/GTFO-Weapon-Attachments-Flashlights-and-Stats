using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;

namespace PreLuna.WeaponAttachments.Interface;

public sealed record PanelWeapon(WeaponIdentity Identity, string Name);
public sealed record PanelContext(UpstreamSnapshot Upstream, IReadOnlyCollection<string> Capabilities,
    ConditionSnapshot Conditions, bool CanEdit, string Notice);
public interface IAttachmentPanelPort
{
    IReadOnlyList<PanelWeapon> Weapons { get; }
    PanelContext Read(ConfigurationTarget target, WeaponIdentity weapon);
    string? UnavailableResource(AttachmentDefinition definition);
    void Committed(ConfigurationTarget target, WeaponIdentity weapon);
}
public sealed record PanelChoice(string Id, string Name, string Description, bool Selected, bool Enabled, string Reason);
public sealed record PanelState(ConfigurationTarget Target, PanelWeapon Weapon, AttachmentSlot Slot,
    IReadOnlyList<PanelChoice> Choices, LoadoutSnapshot Draft, LoadoutPreview Preview, bool CanApply,
    bool Dirty, int DirtyCount, string Notice, string Message);

// Contains no Unity types: every editor is keyed by both target and stable weapon identity.
public sealed class PanelController
{
    private readonly AttachmentCatalog _catalog;
    private readonly LoadoutManager _loadouts;
    private readonly ModifierCalculator _calculator;
    private readonly IAttachmentPanelPort _port;
    private readonly Dictionary<string, LoadoutDraft> _drafts = new(StringComparer.Ordinal);
    public ConfigurationTarget Target { get; private set; } = ConfigurationTarget.LocalPlayer;
    public PanelWeapon? Weapon { get; private set; }
    public AttachmentSlot Slot { get; private set; } = AttachmentSlot.Muzzle;
    public bool Opened { get; private set; }
    public bool CloseConfirmation { get; private set; }
    public string Message { get; private set; } = "";
    public int DirtyCount => _drafts.Values.Count(draft => draft.IsDirty);
    private string Key => Target.StableKey + "/" + Weapon!.Identity.StableKey;
    public IReadOnlyList<PanelWeapon> Weapons => _port.Weapons;
    public PanelController(AttachmentCatalog catalog, LoadoutManager loadouts, ModifierCalculator calculator, IAttachmentPanelPort port)
    { _catalog = catalog; _loadouts = loadouts; _calculator = calculator; _port = port; }
    public void Open(PanelWeapon weapon, ConfigurationTarget? target = null)
    {
        Weapon = weapon; Target = target ?? ConfigurationTarget.LocalPlayer; Opened = true; CloseConfirmation = false; Message = "";
        EnsureDraft(_port.Read(Target, weapon.Identity));
    }
    public void SelectTarget(ConfigurationTarget target) { Target = target; CloseConfirmation = false; Message = ""; }
    public void SelectWeapon(PanelWeapon weapon) { Weapon = weapon; CloseConfirmation = false; Message = ""; }
    public void SelectSlot(AttachmentSlot slot) { if (SlotCatalog.Find(slot) == null) throw new ArgumentException("未知槽位"); Slot = slot; }
    private LoadoutDraft EnsureDraft(PanelContext context)
    {
        if (!_drafts.TryGetValue(Key, out var draft))
            _drafts[Key] = draft = _loadouts.BeginEdit(Target, Weapon!.Identity, context.Upstream.WeaponCapabilities, context.Capabilities);
        return draft;
    }
    private SelectionState Resolve(SelectionState selected, PanelContext context)
    {
        var result = _catalog.CheckAvailability(selected.RequestedId, Weapon!.Identity, Target,
            context.Upstream.WeaponCapabilities, context.Capabilities);
        var definition = _catalog.Find(selected.RequestedId)?.Definition;
        bool ready = result.Applicable && result.RuntimeReady && definition != null && _port.UnavailableResource(definition) == null;
        return selected with { Applicable = result.Applicable, RuntimeReady = ready,
            EffectiveId = ready ? selected.RequestedId : SlotCatalog.OriginalId(selected.Slot), Code = result.Code };
    }
    private LoadoutSnapshot Effective(LoadoutSnapshot snapshot, PanelContext context) => snapshot with
    { Selections = Array.AsReadOnly(snapshot.Selections.Select(value => { var row = Resolve(value, context); return row with { RequestedId = row.EffectiveId }; }).ToArray()) };
    public PanelState Read()
    {
        if (!Opened || Weapon == null) throw new InvalidOperationException("配件面板未打开。");
        var context = _port.Read(Target, Weapon.Identity);
        var draft = EnsureDraft(context);
        var current = _loadouts.GetCommitted(Target, Weapon.Identity, context.Upstream.WeaponCapabilities, context.Capabilities);
        var preview = _calculator.PreviewResolved(Effective(current, context), Effective(draft.Snapshot, context), context.Upstream, context.Conditions);
        var requested = draft.Snapshot.Selections.First(value => value.Slot == Slot).RequestedId;
        var choices = _catalog.ForSlot(Slot).Select(entry =>
        {
            var available = _catalog.CheckAvailability(entry.Definition.Id, Weapon.Identity, Target, context.Upstream.WeaponCapabilities, context.Capabilities);
            string? resource = _port.UnavailableResource(entry.Definition);
            string reason = !context.CanEdit ? context.Notice : !available.Applicable ? "不适用于当前对象或武器" :
                !available.RuntimeReady ? "当前武器缺少所需能力" : resource ?? "";
            bool enabled = context.CanEdit && available.Applicable && available.RuntimeReady && resource == null && !SlotCatalog.Find(Slot)!.Reserved;
            return new PanelChoice(entry.Definition.Id, entry.Definition.DisplayName, PanelText.Describe(entry.Definition),
                requested == entry.Definition.Id, enabled, SlotCatalog.Find(Slot)!.Reserved ? "瞄准镜槽位预留" : reason);
        }).ToArray();
        return new(Target, Weapon, Slot, Array.AsReadOnly(choices), draft.Snapshot, preview,
            context.CanEdit && _loadouts.CanWrite && draft.IsDirty, draft.IsDirty, DirtyCount,
            !_loadouts.CanWrite ? "存档受保护，只读预览" : context.Notice, Message);
    }
    public LoadoutOperation Select(string id)
    {
        var choice = Read().Choices.FirstOrDefault(value => value.Id == id);
        if (choice == null || !choice.Enabled) return Result(false, "UNAVAILABLE", choice?.Reason ?? "未知配件");
        return Record(_drafts[Key].Select(Slot, id));
    }
    public LoadoutOperation Apply()
    {
        var context = _port.Read(Target, Weapon!.Identity); // Recheck ownership at the moment of writing.
        if (!context.CanEdit) return Result(false, "TARGET_CHANGED", context.Notice);
        var draft = EnsureDraft(context);
        var committed = draft.CommittedSnapshot;
        foreach (var value in draft.Snapshot.Selections)
            if (value.RequestedId != SlotCatalog.OriginalId(value.Slot) &&
                value.RequestedId != committed.Selections.First(row => row.Slot == value.Slot).RequestedId && !Resolve(value, context).RuntimeReady)
                return Result(false, "CAPABILITY_CHANGED", "当前选择无法执行，请核对配件或恢复原装。");
        var result = draft.Apply();
        if (result.Success) { _port.Committed(Target, Weapon.Identity); Message = "配装已应用"; }
        else Message = result.Message;
        return result;
    }
    public void Cancel() { EnsureDraft(_port.Read(Target, Weapon!.Identity)).Cancel(); Message = "当前武器的未应用更改已取消"; }
    public void Reset() { EnsureDraft(_port.Read(Target, Weapon!.Identity)).ResetToOriginal(); Message = "已预览原装配装，应用后保存"; }
    public bool RequestClose() { if (DirtyCount > 0) { CloseConfirmation = true; return false; } DiscardAndClose(); return true; }
    public void ContinueEditing() => CloseConfirmation = false;
    public void DiscardAndClose() { foreach (var draft in _drafts.Values) draft.Cancel(); _drafts.Clear(); Opened = false; CloseConfirmation = false; }
    private LoadoutOperation Record(LoadoutOperation result) { Message = result.Success ? "预览已更新，尚未应用" : result.Message; return result; }
    private LoadoutOperation Result(bool success, string code, string message) { Message = message; return new(success, code, message); }
}
