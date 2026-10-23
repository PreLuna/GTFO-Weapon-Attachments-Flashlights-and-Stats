using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using CellMenu;
using Gear;
using HarmonyLib;
using UnityEngine;
using WeaponStatShower.ExtraDescription;
using WeaponStatShower.Utils;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;
using PreLuna.WeaponAttachments.Native;

namespace PreLuna.WeaponAttachments.Interface;

public sealed class AttachmentInterfaceRuntime : IDisposable
{
    public static AttachmentInterfaceRuntime? Current { get; private set; }
    private const string PageTitle = "武器自定义配件";
    private readonly Harmony _harmony = new("PreLuna.WeaponAttachments.Interface");
    private readonly NativeRuntime _native;
    private readonly ManualLogSource _log;
    private readonly GamePanelPort _port;
    private readonly LoadoutManager _loadouts;
    private DescriptionButtonHandler? _handler;
    private CM_PlayerLobbyBar? _bar;
    private CM_Item? _entry;
    private PanelWeapon? _weapon;
    private int _page = -1;
    private float _refreshAt;
    private bool _allowPopupClose;
    private bool _closePopupAfterPanel;
    [ThreadStatic] private static string? _generatedStats;
    public PanelController Controller { get; }
    public AttachmentPanelView? View { get; private set; }
    public string LastError { get; private set; } = "";
    public int HookCount { get; private set; }
    public CM_Item? EntryButton => _entry;
    public int PageIndex => _page;
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Get<T>(object handler, string name) => (T)typeof(DescriptionButtonHandler).GetField(name, Private)!.GetValue(handler)!;
    private static void Set(object handler, string name, object value) => typeof(DescriptionButtonHandler).GetField(name, Private)!.SetValue(handler, value);
    public AttachmentInterfaceRuntime(AttachmentCatalog catalog, LoadoutManager loadouts, ModifierCalculator calculator, NativeRuntime native, ManualLogSource log)
    {
        _native = native; _log = log; _loadouts = loadouts; _port = new(native, catalog);
        Controller = new(catalog, loadouts, calculator, _port);
    }
    public void Install()
    {
        Current = this;
        void Add(MethodBase? method, string? prefix = null, string? postfix = null)
        {
            if (method == null) throw new MissingMethodException("WeaponStatShower 界面接口与已留存版本不同。");
            HarmonyMethod? Patch(string? name) => name == null ? null : new HarmonyMethod(typeof(AttachmentInterfaceRuntime), name) { priority = Priority.Last };
            _harmony.Patch(method, Patch(prefix), Patch(postfix)); HookCount++;
        }
        try
        {
            Add(AccessTools.Constructor(typeof(DescriptionButtonHandler), new[] { typeof(CM_PlayerLobbyBar) }), postfix: nameof(Constructed));
            Add(AccessTools.Method(typeof(DescriptionButtonHandler), "CreateInfo"), nameof(BeforeInfo), nameof(AfterInfo));
            Add(AccessTools.Method(typeof(DescriptionButtonHandler), "CreateGeneratedStats"), postfix: nameof(GeneratedStats));
            Add(AccessTools.Method(typeof(DescriptionButtonHandler), "SetBoxText"), postfix: nameof(BoxChanged));
            Add(AccessTools.Method(typeof(DescriptionButtonHandler), "SetData"), postfix: nameof(DataChanged));
            Add(AccessTools.Method(typeof(DescriptionButtonHandler), "OnBtnPress"), nameof(FlipAllowed));
            Add(AccessTools.Method(typeof(CM_PlayerLobbyBar), "HidePopup"), nameof(HideAllowed));
            Add(AccessTools.Method(typeof(CM_PlayerLobbyBar), "SetPopupVisible"), nameof(VisibleAllowed));
        }
        catch (Exception error) { LastError = error.Message; _log.LogError("配件界面接入失败：" + error); _harmony.UnpatchSelf(); HookCount = 0; }
    }
    private static void BeforeInfo() => _generatedStats = null;
    private static void GeneratedStats((LocaleText header, LocaleText desc) __result) => _generatedStats = __result.desc.ToString();
    private static void Constructed(DescriptionButtonHandler __instance, CM_PlayerLobbyBar lobbyBar)
    {
        var current = Current; if (current == null) return;
        current.ClosePanel(true); current.DestroyEntry(); current._handler = __instance; current._bar = lobbyBar; current._page = -1;
    }
    private static void AfterInfo(DescriptionButtonHandler __instance, GearIDRange idRange, ref int descriptionIndex, ref bool __result)
    {
        var current = Current; if (current == null) return;
        try { current.InsertPage(__instance, idRange, ref descriptionIndex, ref __result); }
        catch (Exception error) { current.Fail(error); }
    }
    private void InsertPage(DescriptionButtonHandler handler, GearIDRange range, ref int descriptionIndex, ref bool hasInfo)
    {
        var item = GameData.ItemDataBlock.GetBlock(range.GetCompID(eGearComponent.BaseItem));
        if (item?.inventorySlot is not (Player.InventorySlot.GearStandard or Player.InventorySlot.GearSpecial)) return;
        var identity = _native.ResolveIdentity(range); if (identity == null) return;
        var box = Get<CM_ScrollWindowInfoBox>(handler, "_infoBox");
        var headers = hasInfo ? Get<LocaleText[]>(handler, "_headers").ToList() : new List<LocaleText> { new(box.m_infoMainTitleText.text) };
        var descriptions = hasInfo ? Get<LocaleText[]>(handler, "_descriptions").ToList() : new List<LocaleText> { new(box.m_infoDescriptionText.text) };
        int statsIndex = _generatedStats == null ? -1 : descriptions.FindIndex(value => value.ToString() == _generatedStats || value.ToString().StartsWith(_generatedStats + "\n", StringComparison.Ordinal));
        int insert = statsIndex < 0 ? descriptions.Count : statsIndex + 1;
        int oldIndex = Get<int>(handler, "_index"); bool keptOwnPage = ReferenceEquals(handler, _handler) && _page >= 0 && oldIndex == _page;
        headers.Insert(insert, new LocaleText(PageTitle)); descriptions.Insert(insert, new LocaleText("配装按对象及武器独立保存。\n请选择下方按钮进入配置。"));
        Set(handler, "_headers", headers.ToArray()); Set(handler, "_descriptions", descriptions.ToArray());
        if (keptOwnPage) Set(handler, "_index", insert); else if (oldIndex >= insert) Set(handler, "_index", oldIndex + 1);
        if (descriptionIndex >= insert) descriptionIndex++;
        _handler = handler; _page = insert; _weapon = new(identity, range.PublicGearName.ToString()); hasInfo = true;
        if (Controller.Opened) Controller.SelectWeapon(_weapon);
    }
    private static void BoxChanged(DescriptionButtonHandler __instance) { if (ReferenceEquals(Current?._handler, __instance)) Current!.UpdateEntry(); }
    private static void DataChanged(DescriptionButtonHandler __instance) { if (ReferenceEquals(Current?._handler, __instance)) Current!.UpdateEntry(); }
    private void UpdateEntry()
    {
        try
        {
            if (_handler == null || _bar == null || _weapon == null || _page < 0) return;
            bool visible = Get<int>(_handler, "_index") == _page;
            if (_entry == null)
                _entry = CellMenuControls.Button(Get<CM_ScrollWindowInfoBox>(_handler, "_infoBox").transform,
                    CM_PageLoadout.Current.m_copyLobbyIdButton, "进入配件配置", -560, -250, 500, 55, OpenPanel);
            _entry.gameObject.SetActive(visible && View == null);
            if (!visible) return;
            var box = Get<CM_ScrollWindowInfoBox>(_handler, "_infoBox");
            var target = _port.TargetOf(_bar.m_player) ?? ConfigurationTarget.LocalPlayer;
            var committed = _loadouts.GetCommitted(target, _weapon.Identity);
            int count = committed.Selections.Count(value => !value.RequestedId.StartsWith("original.", StringComparison.Ordinal));
            box.m_infoMainTitleText.SetText(PageTitle);
            box.m_infoDescriptionText.SetText(_weapon.Name + "\n" + PanelText.Target(target) + " · 已选 " + count + " 个配件\n\n十一槽逐枪保存，预览后应用。\n手电选择原版型号，保留外壳。\n切换配置对象可分别设置三个人机。");
        }
        catch (Exception error) { Fail(error); }
    }
    public void OpenPanel()
    {
        if (_weapon == null || _bar == null || _handler == null || View != null) return;
        try
        {
            Controller.Open(_weapon, _port.TargetOf(_bar.m_player) ?? ConfigurationTarget.LocalPlayer);
            _entry?.gameObject.SetActive(false);
            View = new AttachmentPanelView(Controller, _bar.m_popupScrollWindow.transform, CM_PageLoadout.Current.m_copyLobbyIdButton, () => ClosePanel(false));
        }
        catch (Exception error) { Fail(error); ClosePanel(true); }
    }
    private static bool FlipAllowed(DescriptionButtonHandler __instance)
    {
        var current = Current; if (current == null || !ReferenceEquals(current._handler, __instance)) return true;
        if (current.View != null) return false;
        if (current._entry == null || !current._entry.gameObject.activeInHierarchy || CM_PageLoadout.Current == null) return true;
        var point = current._entry.transform.InverseTransformPoint(CM_PageLoadout.Current.CursorWorldPosition);
        return Math.Abs(point.x) > 250 || Math.Abs(point.y) > 30;
    }
    private static bool HideAllowed(CM_PlayerLobbyBar __instance) => Current?.AllowHide(__instance) ?? true;
    private static bool VisibleAllowed(CM_PlayerLobbyBar __instance, bool visible) => visible || (Current?.AllowHide(__instance) ?? true);
    private bool AllowHide(CM_PlayerLobbyBar bar)
    {
        if (_allowPopupClose || _bar == null || _bar.Pointer != bar.Pointer || View == null) return true;
        if (!Controller.RequestClose()) { _closePopupAfterPanel = true; View.Refresh(true); return false; }
        ClosePanel(false); return true;
    }
    public void Tick()
    {
        if (View == null) return;
        try
        {
            if (_bar == null || !_bar.gameObject.activeInHierarchy || _bar.m_popupScrollWindow == null || !_bar.m_popupScrollWindow.gameObject.activeInHierarchy)
            { ClosePanel(true); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) Controller.RequestClose();
            if (Time.unscaledTime >= _refreshAt) { _refreshAt = Time.unscaledTime + .3f; View?.Refresh(); }
        }
        catch (Exception error) { Fail(error); ClosePanel(true); }
    }
    private void ClosePanel(bool discard)
    {
        if (discard) Controller.DiscardAndClose();
        View?.Dispose(); View = null; if (!discard) UpdateEntry();
        if (_closePopupAfterPanel && _bar != null)
        { _closePopupAfterPanel = false; _allowPopupClose = true; try { _bar.HidePopup(); } finally { _allowPopupClose = false; } }
    }
    private void DestroyEntry() { if (_entry != null) UnityEngine.Object.Destroy(_entry.gameObject); _entry = null; }
    private void Fail(Exception error) { LastError = error.Message; _log.LogError("配件界面：" + error); }
    public void Dispose() { ClosePanel(true); DestroyEntry(); _harmony.UnpatchSelf(); CellMenuControls.DisposeResources(); if (Current == this) Current = null; }
}

public sealed class AttachmentInterfaceDriver : MonoBehaviour
{
    public AttachmentInterfaceDriver(IntPtr pointer) : base(pointer) { }
    public void Update() => AttachmentInterfaceRuntime.Current?.Tick();
}
