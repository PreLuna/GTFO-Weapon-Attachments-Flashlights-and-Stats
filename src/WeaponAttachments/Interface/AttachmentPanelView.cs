using System;
using System.Collections.Generic;
using System.Linq;
using CellMenu;
using UnityEngine;
using PreLuna.WeaponAttachments.Definitions;

namespace PreLuna.WeaponAttachments.Interface;

public sealed class AttachmentPanelView : IDisposable
{
    private readonly PanelController _controller;
    private readonly CM_Item _template;
    private readonly GameObject _root;
    private readonly Action _closed;
    private readonly List<(Collider2D Collider, bool Enabled)> _blocked = new();
    private GameObject? _content;
    private string _signature = "";
    private int _choicePage, _statPage, _generation;
    private readonly List<CM_Item> _buttons = new();
    public IReadOnlyList<CM_Item> Buttons => _buttons.AsReadOnly();
    public Transform Transform => _root.transform;
    public bool Alive => _root != null;
    public AttachmentPanelView(PanelController controller, Transform parent, CM_Item template, Action closed)
    {
        _controller = controller; _template = template; _closed = closed;
        foreach (var collider in parent.GetComponentsInChildren<Collider2D>(true))
        { _blocked.Add((collider, collider.enabled)); collider.enabled = false; }
        _root = new GameObject("WeaponAttachments.Panel"); _root.layer = LayerManager.LAYER_UI;
        _root.transform.SetParent(parent, false); Position(); Refresh(true);
    }
    private void Position()
    {
        if (CM_Camera.Current?.Camera == null) return;
        var camera = CM_Camera.Current.Camera;
        float depth = Math.Max(camera.nearClipPlane + 20, camera.WorldToViewportPoint(_root.transform.parent.position).z - 3);
        var center = camera.ViewportToWorldPoint(new Vector3(.5f, .5f, depth));
        float width = Vector3.Distance(camera.ViewportToWorldPoint(new(.04f, .5f, depth)), camera.ViewportToWorldPoint(new(.96f, .5f, depth)));
        float height = Vector3.Distance(camera.ViewportToWorldPoint(new(.5f, .06f, depth)), camera.ViewportToWorldPoint(new(.5f, .94f, depth)));
        float scale = Math.Min(width / 1600, height / 900);
        _root.transform.rotation = camera.transform.rotation;
        _root.transform.position = center - camera.transform.right * (800 * scale) + camera.transform.up * (450 * scale);
        _root.transform.localScale = Vector3.one * (scale / _root.transform.parent.lossyScale.x);
    }
    public void Refresh(bool force = false)
    {
        if (!Alive) return;
        if (!_controller.Opened) { _closed(); return; }
        Position();
        var state = _controller.Read();
        string signature = state.Target.StableKey + state.Weapon.Identity.StableKey + state.Slot + state.DirtyCount + state.Notice + state.Message +
            _controller.CloseConfirmation + _choicePage + ":" + _statPage + string.Join("|", state.Choices.Select(value => value.ToString())) +
            string.Join("|", state.Preview.Rows.Select(value => value.ToString()));
        if (!force && signature == _signature) return;
        _signature = signature; _generation++; _buttons.Clear();
        if (_content != null) { _content.SetActive(false); UnityEngine.Object.Destroy(_content); }
        _content = new GameObject("WA.Content"); _content.transform.SetParent(_root.transform, false);
        var parent = _content.transform;
        CellMenuControls.Background(parent, _template, 0, 0, 1600, 900, new(.025f, .035f, .04f, .99f));
        CellMenuControls.Text(parent, _template, "武器自定义配件  /  " + state.Weapon.Name, 35, -25, 1500, 55, 35);
        if (_controller.CloseConfirmation)
        {
            CellMenuControls.Text(parent, _template, "还有 " + state.DirtyCount + " 份未应用的配装", 350, -325, 950, 60, 34);
            Button("继续编辑", 360, -445, 360, 60, () => _controller.ContinueEditing());
            Button("放弃未应用并返回", 780, -445, 440, 60, () => _controller.DiscardAndClose());
            return;
        }
        int targetIndex = 0;
        foreach (var target in ConfigurationTarget.All)
        {
            var selected = target;
            Button((selected.StableKey == state.Target.StableKey ? "[x] " : "[ ] ") + PanelText.Target(selected), 35 + targetIndex++ * 200, -95, 190, 45,
                () => { _controller.SelectTarget(selected); _choicePage = 0; });
        }
        Button("‹ 浏览上一把", 1080, -95, 220, 45, () => ChangeWeapon(-1));
        Button("浏览下一把 ›", 1320, -95, 240, 45, () => ChangeWeapon(1));
        CellMenuControls.Text(parent, _template, "配件槽位", 35, -165, 240, 40, 28);
        int slotIndex = 0;
        foreach (var slot in SlotCatalog.All)
        {
            var selected = slot.Slot;
            Button((selected == state.Slot ? "› " : "  ") + slot.DisplayName + (slot.Reserved ? " · 预留" : ""), 35, -210 - slotIndex++ * 43, 245, 40,
                () => { _controller.SelectSlot(selected); _choicePage = 0; });
        }
        CellMenuControls.Text(parent, _template, "选择" + SlotCatalog.Find(state.Slot)!.DisplayName, 320, -165, 490, 40, 28);
        _choicePage = Math.Clamp(_choicePage, 0, Math.Max(0, (state.Choices.Count - 1) / 6));
        int rowIndex = 0;
        foreach (var choice in state.Choices.Skip(_choicePage * 6).Take(6))
        {
            var option = choice;
            float y = -215 - rowIndex++ * 78;
            Button((option.Selected ? "[x] " : "[ ] ") + option.Name, 315, y, 490, 39, () => _controller.Select(option.Id), option.Enabled);
            CellMenuControls.Text(parent, _template, option.Enabled ? option.Description : option.Reason, 330, y - 39, 475, 40, 21).color = new(.6f, .7f, .7f, 1);
        }
        if (state.Choices.Count == 1 && state.Slot != AttachmentSlot.Optic)
            CellMenuControls.Text(parent, _template, "此槽暂无其他配件", 335, -315, 450, 40, 24);
        if (state.Choices.Count > 6)
        { Button("‹ 上一页", 320, -710, 220, 40, () => _choicePage--, _choicePage > 0); Button("下一页 ›", 560, -710, 220, 40, () => _choicePage++, (_choicePage + 1) * 6 < state.Choices.Count); }
        var headers = new[] { "属性", "基础", "当前", "预览" };
        for (int i = 0; i < headers.Length; i++) CellMenuControls.Text(parent, _template, headers[i], 850 + i * 172, -165, 167, 40, 28);
        _statPage = Math.Clamp(_statPage, 0, Math.Max(0, (state.Preview.Rows.Count - 1) / 13));
        int statIndex = 0;
        foreach (var row in state.Preview.Rows.Skip(_statPage * 13).Take(13))
        {
            var values = new[] { PanelText.Stat(row.Stat), PanelText.Value(row.BaseValue, row.Unit, row.Form),
                PanelText.Value(row.CanCalculate ? row.CurrentValue : null, row.Unit, row.Form), PanelText.Value(row.CanCalculate ? row.PreviewValue : null, row.Unit, row.Form) };
            for (int i = 0; i < values.Length; i++)
                CellMenuControls.Text(parent, _template, values[i], 850 + i * 172, -218 - statIndex * 35, 162, 36, 21).color = i == 3 ? new(.45f, .91f, .75f, 1) : Color.white;
            statIndex++;
        }
        if (state.Preview.Rows.Count > 13)
        { Button("‹ 更多属性", 850, -710, 290, 40, () => _statPage--, _statPage > 0); Button("更多属性 ›", 1190, -710, 290, 40, () => _statPage++, (_statPage + 1) * 13 < state.Preview.Rows.Count); }
        CellMenuControls.Text(parent, _template, state.Message.Length > 0 ? state.Message : state.Notice, 35, -765, 1520, 45, 23);
        Button(state.Dirty ? "应用配装 · 未保存" : "已应用", 35, -830, 340, 48, () => _controller.Apply(), state.CanApply);
        Button("取消当前更改", 415, -830, 340, 48, () => _controller.Cancel(), state.Dirty);
        Button("预览恢复原装", 795, -830, 340, 48, () => _controller.Reset());
        Button("返回数据页", 1175, -830, 360, 48, () => _controller.RequestClose());
    }
    private void Button(string label, float x, float y, float width, float height, Action action, bool enabled = true)
    {
        int generation = _generation;
        _buttons.Add(CellMenuControls.Button(_content!.transform, _template, label, x, y, width, height,
            () => { if (generation != _generation || !Alive) return; action(); Refresh(true); }, enabled));
    }
    private void ChangeWeapon(int delta)
    {
        var weapons = _controller.Weapons;
        if (weapons.Count == 0) return;
        int index = weapons.ToList().FindIndex(value => value.Identity.Equals(_controller.Weapon!.Identity));
        _controller.SelectWeapon(weapons[(Math.Max(0, index) + delta + weapons.Count) % weapons.Count]); _choicePage = 0; _statPage = 0;
    }
    public void Dispose()
    {
        foreach (var value in _blocked) if (value.Collider != null) value.Collider.enabled = value.Enabled;
        _blocked.Clear(); _buttons.Clear(); if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
    }
}
