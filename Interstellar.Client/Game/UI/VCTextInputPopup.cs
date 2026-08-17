#pragma warning disable CS8602, CS8603
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

/// <summary>
/// Text input popup - mimics TheOtherRoles PresetManager's PresetInputBox:
/// dark rounded panel + white border + title + TMP_InputField + Confirm(green)/Cancel(red).
/// Click outside to close.
/// </summary>
public static class VCTextInputPopup
{
    private static GameObject _popup;
    private static TMP_InputField _input;
    private static Action<string> _onSave;

    public static bool IsShowing => _popup != null && _popup.activeSelf;

    public static void Show(string title, string placeholder, string current, int charLimit, Action<string> onSave)
    {
        Hide();
        _onSave = onSave;

        var canvas = VCUiKit.EnsureCanvas();
        _popup = new GameObject("VC_InputPopup");
        _popup.transform.SetParent(canvas.transform, false);
        var rt = _popup.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Full-screen dim (click outside to close)
        var dim = VCUiKit.CreateImage(_popup.transform, "Dim", Vector2.zero, Vector2.zero, VCUiKit.PixelSprite, new Color(0f, 0f, 0f, 0.45f));
        var dimRt = (RectTransform)dim.transform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = Vector2.zero;
        dimRt.offsetMax = Vector2.zero;
        var dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener((Action)(() => Hide()));

        // Panel
        const float pw = 720f, ph = 330f;
        var panel = VCUiKit.CreatePanel(_popup.transform, "Panel", new Vector2(pw, ph),
            new Color(0.88f, 0.94f, 1f, 1f), new Color(0.07f, 0.10f, 0.16f, 0.97f), 6f);

        // Title
        VCUiKit.CreateText(panel, "Title", title, new Vector2(0f, ph * 0.5f - 46f), new Vector2(pw - 80f, 44f),
            30f, new Color(0.92f, 0.95f, 1f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        // Input
        _input = VCUiKit.CreateTextInput(panel, "Input", new Vector2(0f, 6f), new Vector2(pw - 120f, 62f),
            new Color(0.10f, 0.13f, 0.20f, 1f), placeholder, 26f, charLimit);
        _input.text = current ?? "";

        // Confirm / Cancel
        VCUiKit.CreateButton(panel, TranslationHelper.Get("vc.popup.confirm", "Confirm"), new Vector2(-130f, -118f), new Vector2(200f, 58f),
            new Color(0.20f, 0.62f, 0.33f, 1f), () => Confirm(), 26f);
        VCUiKit.CreateButton(panel, TranslationHelper.Get("vc.popup.cancel", "Cancel"), new Vector2(130f, -118f), new Vector2(200f, 58f),
            new Color(0.65f, 0.25f, 0.25f, 1f), () => Hide(), 26f);
    }

    public static void Confirm()
    {
        if (_input == null) { Hide(); return; }
        var save = _onSave;
        Hide();
        save?.Invoke(_input.text);
    }

    public static void Hide()
    {
        if (_popup != null) Object.Destroy(_popup);
        _popup = null;
        _input = null;
        _onSave = null;
    }
}
