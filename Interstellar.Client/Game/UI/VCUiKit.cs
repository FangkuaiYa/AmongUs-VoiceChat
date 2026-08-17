#pragma warning disable CS8602, CS8603, CS8618
using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

/// <summary>
/// Lightweight Among Us native uGUI toolkit.
/// Mimics TheOtherRoles MetaScreen / PresetManager look:
/// dark rounded panel + white border + bold TMP text + colored action buttons.
/// All controls are UnityEngine.UI + TextMeshProUGUI on a dedicated overlay canvas.
///
/// IL2CPP safety notes:
///  - Sprite.Create is only used with the 4-arg overload (pixelsPerUnit), never the
///    border-parameter overload (unreliable under IL2CPP interop). Outlines are drawn
///    with a second layered Image instead.
///  - TMP_Settings / font material access is fully try/catch protected.
/// </summary>
public static class VCUiKit
{
    // Shared drag lock: only one of our windows may be dragged at a time.
    public static bool AnyWindowDragging;

    // Canvas
    private static Canvas _canvas;

    public static Canvas EnsureCanvas()
    {
        if (_canvas != null) return _canvas;

        var go = new GameObject("InterstellarVC_Canvas");
        Object.DontDestroyOnLoad(go);
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 31000;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        EnsureEventSystem();
        return _canvas;
    }

    public static void EnsureEventSystem()
    {
        if (Object.FindObjectOfType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.transform.SetParent(EnsureCanvas().transform, false);
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
    }

    // Assets
    private static Sprite _panelSprite;
    private static Sprite _roundSprite;
    private static Sprite _pixelSprite;
    private static Sprite _circleSprite;
    private static TMP_FontAsset _font;
    private static Material _fontMaterial;

    /// <summary>Rounded dark panel with white outline (two layered images, no 9-slice).</summary>
    public static Sprite PanelSprite
    {
        get
        {
            if (_panelSprite != null) return _panelSprite;
            _panelSprite = CreateRoundedSprite(128, 22, new Color32(17, 23, 35, 255));
            _panelSprite.name = "VC_Panel";
            return _panelSprite;
        }
    }

    /// <summary>Rounded white sprite (tint via Image.color).</summary>
    public static Sprite RoundSprite
    {
        get
        {
            if (_roundSprite != null) return _roundSprite;
            _roundSprite = CreateRoundedSprite(128, 22, Color.white);
            _roundSprite.name = "VC_Round";
            return _roundSprite;
        }
    }

    public static Sprite PixelSprite
    {
        get
        {
            if (_pixelSprite != null) return _pixelSprite;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _pixelSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            _pixelSprite.name = "VC_Pixel";
            return _pixelSprite;
        }
    }

    public static Sprite CircleSprite
    {
        get
        {
            if (_circleSprite != null) return _circleSprite;
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            float r = S * 0.5f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - r + 0.5f, dy = y - r + 0.5f;
                    float a = Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) * 2f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
            _circleSprite.name = "VC_Circle";
            return _circleSprite;
        }
    }

    public static TMP_FontAsset Font
    {
        get
        {
            if (_font != null) return _font;
            try { _font = TMP_Settings.defaultFontAsset; } catch { _font = null; }
            if (_font == null)
            {
                try { _font = Object.FindObjectOfType<TMP_Text>()?.font; } catch { _font = null; }
            }
            return _font;
        }
    }

    public static Material FontMaterial
    {
        get
        {
            if (_fontMaterial != null) return _fontMaterial;
            try
            {
                var f = Font;
                if (f != null) _fontMaterial = f.material;
            }
            catch { _fontMaterial = null; }
            return _fontMaterial;
        }
    }

    // Sprite generation
    private static bool InRoundedRect(float x, float y, float size, float r)
    {
        float cx = Mathf.Clamp(x, r, size - r);
        float cy = Mathf.Clamp(y, r, size - r);
        float dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>Rounded-rect sprite (4-arg Sprite.Create, safe under IL2CPP).</summary>
    public static Sprite CreateRoundedSprite(int size, int radius, Color fill)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, InRoundedRect(x + 0.5f, y + 0.5f, size, radius) ? fill : Color.clear);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    // Primitives
    public static RectTransform NewRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return (RectTransform)go.transform;
    }

    public static Image CreateImage(Transform parent, string name, Vector2 anchoredPos, Vector2 size, Sprite sprite, Color color)
    {
        var rt = NewRect(parent, name);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = true;
        return img;
    }

    /// <summary>Layered panel (outline behind inner) inside one container so dragging moves both. Returns the container.</summary>
    public static RectTransform CreatePanel(Transform parent, string name, Vector2 size,
        Color border, Color fill, float thickness = 6f)
    {
        var container = NewRect(parent, name);
        container.anchorMin = container.anchorMax = new Vector2(0.5f, 0.5f);
        container.pivot = new Vector2(0.5f, 0.5f);
        container.anchoredPosition = Vector2.zero;
        container.sizeDelta = size + Vector2.one * (thickness * 2f);

        // outline (behind)
        CreateImage(container, name + "_Outline", Vector2.zero, size + Vector2.one * (thickness * 2f), PixelSprite, border);
        // inner (front)
        CreateImage(container, name, Vector2.zero, size, PixelSprite, fill);
        return container;
    }

    public static TextMeshProUGUI CreateText(Transform parent, string name, string text, Vector2 anchoredPos, Vector2 size,
        float fontSize, Color color, FontStyles style = FontStyles.Normal, TextAlignmentOptions align = TextAlignmentOptions.Left,
        bool wordWrap = false)
    {
        var rt = NewRect(parent, name);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        try
        {
            var f = Font;
            if (f != null) tmp.font = f;
            var m = FontMaterial;
            if (m != null) tmp.fontSharedMaterial = m;
        }
        catch { }
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.alignment = align;
        tmp.text = text;
        tmp.enableWordWrapping = wordWrap;
        tmp.raycastTarget = false;
        return tmp;
    }

    public static Image CreateDivider(Transform parent, Vector2 anchoredPos, Vector2 size)
        => CreateImage(parent, "Divider", anchoredPos, size, PixelSprite, new Color(0.23f, 0.29f, 0.39f, 0.9f));

    // Button
    public static Button CreateButton(Transform parent, string label, Vector2 anchoredPos, Vector2 size,
        Color bg, Action onClick, float fontSize = 24f, Color? textColor = null, FontStyles style = FontStyles.Bold)
    {
        var rt = NewRect(parent, "Button");
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = PixelSprite;
        img.color = bg;

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.16f, 1.16f, 1.16f, 1f);
        cb.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        cb.selectedColor = Color.white;
        btn.colors = cb;

        CreateText(rt, "Label", label, Vector2.zero, size, fontSize,
            textColor ?? Color.white, style, TextAlignmentOptions.Center);

        btn.onClick.AddListener((Action)(() => onClick?.Invoke()));
        return btn;
    }

    // Toggle (Among Us style ON/OFF pill)
    public static Button CreateToggle(Transform parent, string label, Vector2 anchoredPos, Vector2 size,
        Func<bool> getter, Action<bool> setter, float fontSize = 22f)
    {
        var rt = NewRect(parent, "Toggle");
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = PixelSprite;

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.14f, 1.14f, 1.14f, 1f);
        cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        btn.colors = cb;

        var txt = CreateText(rt, "Label", "", Vector2.zero, size, fontSize, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);

        void ApplyVisual(bool v)
        {
            img.color = v ? new Color(0.24f, 0.62f, 0.96f, 1f) : new Color(0.26f, 0.30f, 0.38f, 1f);
            txt.text = v ? TranslationHelper.Get("vc.common.on", "ON") : TranslationHelper.Get("vc.common.off", "OFF");
        }

        ApplyVisual(getter());
        btn.onClick.AddListener((Action)(() =>
        {
            bool nv = !getter();
            setter?.Invoke(nv);
            ApplyVisual(getter());
        }));
        return btn;
    }

    // Slider - self-implemented. The UnityEngine.UI.Slider component misplaces its fill
    // under IL2CPP, so we compute the value from the mouse position ourselves.
    public static void CreateSlider(Transform parent, Vector2 anchoredPos, Vector2 size, float min, float max,
        float value, Action<float> onChange, float fillHeight = 10f, bool enabled = true)
    {
        var rt = NewRect(parent, "Slider");
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        // track
        var track = NewRect(rt, "Track");
        track.anchorMin = new Vector2(0f, 0.5f);
        track.anchorMax = new Vector2(1f, 0.5f);
        track.pivot = new Vector2(0.5f, 0.5f);
        track.offsetMin = Vector2.zero;
        track.offsetMax = Vector2.zero;
        track.sizeDelta = new Vector2(0f, fillHeight);
        var trackImg = track.gameObject.AddComponent<Image>();
        trackImg.sprite = PixelSprite;
        trackImg.color = new Color(0.18f, 0.21f, 0.28f, 1f);
        trackImg.raycastTarget = false;

        // fill
        var fill = NewRect(rt, "Fill");
        fill.anchorMin = new Vector2(0f, 0.5f);
        fill.anchorMax = new Vector2(0f, 0.5f);
        fill.pivot = new Vector2(0f, 0.5f);
        var fillImg = fill.gameObject.AddComponent<Image>();
        fillImg.sprite = PixelSprite;
        fillImg.color = enabled ? new Color(0.24f, 0.62f, 0.96f, 1f) : new Color(0.30f, 0.34f, 0.42f, 1f);
        fillImg.raycastTarget = false;

        // handle
        var handle = NewRect(rt, "Handle");
        handle.anchorMin = new Vector2(0f, 0.5f);
        handle.anchorMax = new Vector2(0f, 0.5f);
        handle.pivot = new Vector2(0.5f, 0.5f);
        handle.sizeDelta = new Vector2(22f, 22f);
        var handleImg = handle.gameObject.AddComponent<Image>();
        handleImg.sprite = CircleSprite;
        handleImg.color = enabled ? Color.white : new Color(0.55f, 0.58f, 0.64f, 1f);
        handleImg.raycastTarget = false;

        // full-size transparent hit area for pointer events
        var hit = NewRect(rt, "Hit");
        hit.anchorMin = Vector2.zero;
        hit.anchorMax = Vector2.one;
        hit.offsetMin = Vector2.zero;
        hit.offsetMax = Vector2.zero;
        var hitImg = hit.gameObject.AddComponent<Image>();
        hitImg.sprite = PixelSprite;
        hitImg.color = Color.clear;
        hitImg.raycastTarget = true;

        float sliderW = size.x;

        void SyncVisuals(float v)
        {
            float tv = Mathf.Clamp01(Mathf.InverseLerp(min, max, v));
            fill.sizeDelta = new Vector2(tv * sliderW, fillHeight);
            handle.anchoredPosition = new Vector2(tv * sliderW, 0f);
        }

        float ValueFromMouse()
        {
            try
            {
                // uGUI's official method handles all canvas types; overlay canvas -> null camera.
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, null, out var local))
                {
                    float t = Mathf.Clamp01(Mathf.InverseLerp(-sliderW * 0.5f, sliderW * 0.5f, local.x));
                    return Mathf.Lerp(min, max, t);
                }
            }
            catch { }
            return Mathf.Clamp(value, min, max);
        }

        void ApplyFromMouse()
        {
            float v = ValueFromMouse();
            SyncVisuals(v);
            onChange?.Invoke(v);
        }

        if (enabled)
        {
            var et = hit.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener((Action<BaseEventData>)(data =>
            {
                // Consume the pointer event so the parent ScrollRect doesn't start scrolling
                // while dragging the slider handle.
                try
                {
                    var ped = data.TryCast<PointerEventData>();
                    if (ped != null) ped.Use();
                }
                catch { }
                ApplyFromMouse();
            }));
            et.triggers.Add(down);
            var drag = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            drag.callback.AddListener((Action<BaseEventData>)(_ => ApplyFromMouse()));
            et.triggers.Add(drag);
        }

        SyncVisuals(value);
    }

    // TMP_InputField
    public static TMP_InputField CreateTextInput(Transform parent, string name, Vector2 anchoredPos, Vector2 size,
        Color bg, string placeholder, float fontSize, int characterLimit)
    {
        var rt = NewRect(parent, name);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = PixelSprite;
        img.color = bg;

        var input = rt.gameObject.AddComponent<TMP_InputField>();
        try { var f = Font; if (f != null) input.fontAsset = f; } catch { }
        input.characterLimit = characterLimit;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.textViewport = rt;

        // text component
        var textRt = NewRect(rt, "Text");
        textRt.anchorMin = new Vector2(0f, 0f);
        textRt.anchorMax = new Vector2(1f, 1f);
        textRt.pivot = new Vector2(0.5f, 0.5f);
        textRt.offsetMin = new Vector2(14f, 6f);
        textRt.offsetMax = new Vector2(-14f, -6f);
        var text = textRt.gameObject.AddComponent<TextMeshProUGUI>();
        try
        {
            var f = Font;
            if (f != null) text.font = f;
            var m = FontMaterial;
            if (m != null) text.fontSharedMaterial = m;
        }
        catch { }
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Left;
        text.raycastTarget = false;
        input.textComponent = text;

        // placeholder
        var phRt = NewRect(rt, "Placeholder");
        phRt.anchorMin = new Vector2(0f, 0f);
        phRt.anchorMax = new Vector2(1f, 1f);
        phRt.pivot = new Vector2(0.5f, 0.5f);
        phRt.offsetMin = new Vector2(14f, 6f);
        phRt.offsetMax = new Vector2(-14f, -6f);
        var ph = phRt.gameObject.AddComponent<TextMeshProUGUI>();
        try
        {
            var f = Font;
            if (f != null) ph.font = f;
            var m = FontMaterial;
            if (m != null) ph.fontSharedMaterial = m;
        }
        catch { }
        ph.fontSize = fontSize;
        ph.color = new Color(0.62f, 0.66f, 0.74f, 1f);
        ph.alignment = TextAlignmentOptions.Left;
        ph.text = placeholder;
        ph.raycastTarget = false;
        input.placeholder = ph;

        return input;
    }
}
