#pragma warning disable CS8602, CS8603
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

/// <summary>
/// Dropdown option list - mimics TheOtherRoles PresetManager's action-button rows:
/// dark rounded panel + white border + title + vertical option buttons (current highlighted).
/// Click outside to close.
/// </summary>
public static class VCDropdown
{
    private static GameObject _drop;

    public static bool IsShowing => _drop != null;

    public static void Show(string title, string[] options, int currentIndex, Action<int> onSelect)
    {
        Hide();

        var canvas = VCUiKit.EnsureCanvas();
        _drop = new GameObject("VC_Dropdown");
        _drop.transform.SetParent(canvas.transform, false);
        var rt = _drop.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Full-screen dim (click outside to close)
        var dim = VCUiKit.CreateImage(_drop.transform, "Dim", Vector2.zero, Vector2.zero, VCUiKit.PixelSprite, new Color(0f, 0f, 0f, 0.32f));
        var dimRt = (RectTransform)dim.transform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = Vector2.zero;
        dimRt.offsetMax = Vector2.zero;
        var dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener((Action)(() => Hide()));

        // Panel
        const float rowH = 64f;
        const float titleH = 56f;
        const float pad = 24f;
        float pw = 520f;
        float ph = titleH + options.Length * rowH + pad * 2f;
        var panel = VCUiKit.CreatePanel(_drop.transform, "Panel", new Vector2(pw, ph),
            new Color(0.88f, 0.94f, 1f, 1f), new Color(0.07f, 0.10f, 0.16f, 0.97f), 6f);

        // Title
        VCUiKit.CreateText(panel, "Title", title, new Vector2(0f, ph * 0.5f - 34f), new Vector2(pw - 60f, 40f),
            26f, new Color(0.92f, 0.95f, 1f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        // Options
        float topY = ph * 0.5f - titleH - 10f;
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            float y = topY - rowH * i - rowH * 0.5f;
            bool selected = i == currentIndex;
            VCUiKit.CreateButton(panel,
                (selected ? ">  " : "     ") + options[i],
                new Vector2(0f, y), new Vector2(pw - 80f, rowH - 10f),
                selected ? new Color(0.20f, 0.45f, 0.85f, 1f) : new Color(0.16f, 0.20f, 0.28f, 1f),
                () => { onSelect?.Invoke(idx); Hide(); },
                24f, new Color(0.92f, 0.96f, 1f, 1f), FontStyles.Bold);
        }
    }

    public static void Hide()
    {
        if (_drop != null) Object.Destroy(_drop);
        _drop = null;
    }
}
