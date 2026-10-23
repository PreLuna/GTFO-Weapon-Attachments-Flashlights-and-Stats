using System;
using CellMenu;
using TMPro;
using UnityEngine;

namespace PreLuna.WeaponAttachments.Interface;

internal static class CellMenuControls
{
    private static Sprite? _pixel;
    internal static void DisposeResources() { if (_pixel != null) UnityEngine.Object.Destroy(_pixel); _pixel = null; }
    internal static TextMeshPro Text(Transform parent, CM_Item template, string content, float x, float y, float width, float height, float size = 26)
    {
        // Clone the initialized native text, including its renderer/material references.
        var source = template.m_texts[0];
        var go = UnityEngine.Object.Instantiate(source.gameObject, parent); go.name = "WA.Text"; go.layer = LayerManager.LAYER_UI;
        go.SetActive(true); go.transform.localPosition = new(x, y, -3); go.transform.localScale = Vector3.one; go.transform.localRotation = Quaternion.identity;
        foreach (var collider in go.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
        foreach (var item in go.GetComponentsInChildren<CM_Item>(true)) { item.enabled = false; item.OnBtnPressCallback = null; }
        foreach (var sprite in go.GetComponentsInChildren<SpriteRenderer>(true)) sprite.enabled = false;
        var text = go.GetComponent<TextMeshPro>(); text.enabled = true; text.fontSize = size;
        text.color = Color.white; text.enableWordWrapping = true; text.isOrthographic = true;
        text.alignment = TextAlignmentOptions.TopLeft; text.richText = false; text.overflowMode = TextOverflowModes.Ellipsis;
        text.rectTransform.pivot = new(0, 1); text.rectTransform.sizeDelta = new(width, height);
        text.sortingLayerID = source.sortingLayerID; text.sortingOrder = 80; text.SetText(content);
        return text;
    }
    internal static CM_Item Button(Transform parent, CM_Item template, string label, float x, float y, float width, float height, Action action, bool enabled = true)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent); go.name = "WA.Button." + label;
        go.SetActive(true); go.layer = LayerManager.LAYER_UI;
        go.transform.localPosition = new(x + width / 2, y - height / 2, -4); go.transform.localScale = Vector3.one;
        var item = go.GetComponent<CM_Item>(); item.m_forcePopupLayer = true; item.m_clickBlink = false;
        item.OnBtnPressCallback = null; item.add_OnBtnPressCallback((Action<int>)(_ => { if (enabled) action(); }));
        if (item.m_collider == null) item.m_collider = go.AddComponent<BoxCollider2D>();
        item.m_collider.size = new(width, height); item.m_collider.offset = Vector2.zero; item.m_collider.enabled = enabled;
        foreach (var sprite in go.GetComponentsInChildren<SpriteRenderer>(true))
        { sprite.enabled = false; }
        foreach (var text in item.m_texts) text.enabled = false;
        Text(go.transform, template, label, -width / 2 + 10, height / 2 - 4, width - 20, height, 26).color = enabled ? new(.86f, .91f, .91f, 1) : new(.42f, .47f, .48f, 1);
        return item;
    }
    internal static SpriteRenderer Background(Transform parent, CM_Item template, float x, float y, float width, float height, Color color)
    {
        var go = new GameObject("WA.Background"); go.layer = LayerManager.LAYER_UI; go.transform.SetParent(parent, false);
        go.transform.localPosition = new(x + width / 2, y - height / 2, 4); go.transform.localScale = new(width, height, 1);
        var renderer = go.AddComponent<SpriteRenderer>();
        _pixel ??= Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f), 1);
        renderer.sprite = _pixel;
        renderer.color = color; renderer.sortingLayerID = template.m_texts[0].sortingLayerID; renderer.sortingOrder = 60;
        return renderer;
    }
}
