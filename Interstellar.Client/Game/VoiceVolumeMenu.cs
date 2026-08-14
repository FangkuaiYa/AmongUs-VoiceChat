#pragma warning disable CS8618, CS8602, CS8603, CS8604
using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using static Interstellar.Voice.TranslationHelper;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

[HarmonyPatch]
public static class VoiceVolumeMenu
{
    private static GameObject? _popUp;
    private static ToggleButtonBehaviour? _btnPrefab;
    private static TextMeshPro? _titleTmp;
    private static float _scrollOffset;

    private const float RowH = 0.72f;
    private const float SliderW = 1.80f;
    private const float VMin = 0f;
    private const float VMax = 2f;

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    static void MainMenuManager_Start()
    {
        if (!_titleTmp)
        {
            var go = new GameObject("VCVolTitle");
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = 4;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.transform.localPosition += Vector3.left * 0.2f;
            _titleTmp = Object.Instantiate(tmp);
            _titleTmp.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(_titleTmp);
        }

        if (_popUp) { Object.Destroy(_popUp); _popUp = null; }
        _scrollOffset = 0f;
    }

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
    [HarmonyPostfix]
    static void OptionsMenu_Start(OptionsMenuBehaviour __instance)
    {
        if (!__instance.CensorChatButton) return;
        if (!_btnPrefab)
        {
            _btnPrefab = Object.Instantiate(__instance.CensorChatButton);
            Object.DontDestroyOnLoad(_btnPrefab);
            _btnPrefab.name = "VCVol_BtnPrefab";
            _btnPrefab.gameObject.SetActive(false);
        }
    }

    public static void Toggle()
    {
        if (!_popUp) Build();
        if (!_popUp || !_btnPrefab) return;

        if (_popUp.activeSelf) { _popUp.SetActive(false); return; }

        if (HudManager.Instance)
        {
            _popUp.transform.SetParent(HudManager.Instance.transform);
            _popUp.transform.localPosition = new Vector3(0f, 0f, -860f);
        }
        else
        {
            _popUp.transform.SetParent(null);
            Object.DontDestroyOnLoad(_popUp);
        }

        CheckSetTitle();
        RefreshRows();
        _popUp.SetActive(true);
    }

    public static void Close()
    {
        if (_popUp) _popUp.SetActive(false);
    }

    private static void Build()
    {
        if (!_btnPrefab) return;
        var src = Object.FindObjectOfType<OptionsMenuBehaviour>();
        if (!src) return;

        _popUp = Object.Instantiate(src.gameObject);
        Object.DontDestroyOnLoad(_popUp);
        var t = _popUp.transform;
        var p = t.localPosition; p.z = -860f; t.localPosition = p;
        Object.Destroy(_popUp.GetComponent<OptionsMenuBehaviour>());

        for (int i = t.childCount - 1; i >= 0; i--)
        {
            var ch = t.GetChild(i).gameObject;
            if (ch.name is not ("Background" or "CloseButton"))
                Object.Destroy(ch);
        }

        var bg = t.Find("Background")?.GetComponent<SpriteRenderer>();
        if (bg) { bg.color = new Color32(12, 14, 22, 242); bg.sortingOrder = 32766; }

        var close = _popUp.GetComponentInChildren<PassiveButton>();
        if (close)
        {
            close.OnClick = new ButtonClickedEvent();
            close.OnClick.AddListener((Action)(() => _popUp!.SetActive(false)));
        }

        // Scroll arrows
        SmallBtn("▲", new Vector3(2.5f, 2.1f, -0.5f), () => { _scrollOffset = Mathf.Max(0f, _scrollOffset - RowH); RefreshRows(); });
        SmallBtn("▼", new Vector3(2.5f, -2.3f, -0.5f), () => { _scrollOffset += RowH; RefreshRows(); });

        _popUp.SetActive(false);
    }

    private static void CheckSetTitle()
    {
        if (!_popUp || !_titleTmp || _popUp.transform.Find("VCVolTitle")) return;
        var t = Object.Instantiate(_titleTmp, _popUp.transform);
        t.GetComponent<RectTransform>().localPosition = Vector3.up * 2.3f;
        t.gameObject.SetActive(true);
        t.text = Get("vc.volume.title", "Player Volumes");
        t.name = "VCVolTitle";
        t.sortingOrder = 32767;
    }

    private static void RefreshRows()
    {
        if (!_popUp || !_btnPrefab) return;
        var content = _popUp.transform;

        // Destroy old rows (keep shell: Background, CloseButton, VCVolTitle, arrow buttons)
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var n = content.GetChild(i).name;
            if (n is "Background" or "CloseButton" or "VCVolTitle" || n.StartsWith("Btn_")) continue;
            Object.Destroy(content.GetChild(i).gameObject);
        }

        var players = CollectPlayers();

        const float visibleH = 3.8f;
        int maxRows = Mathf.FloorToInt(visibleH / RowH);
        if (maxRows < 1) maxRows = 5;
        int startIdx = Mathf.FloorToInt(_scrollOffset / RowH);
        startIdx = Mathf.Clamp(startIdx, 0, Mathf.Max(0, players.Count - maxRows));
        _scrollOffset = startIdx * RowH;

        float y = 1.6f;
        for (int i = startIdx; i < players.Count && i < startIdx + maxRows; i++)
        {
            BuildRow(content, players[i], y);
            y -= RowH;
        }

        if (players.Count == 0)
        {
            var btn = Object.Instantiate(_btnPrefab!, content);
            btn.transform.localPosition = new Vector3(0f, 0f, -0.5f);
            btn.transform.localScale = new Vector3(0.9f, 0.8f, 1f);
            btn.Text.text = Get("vc.volume.noPlayers", "No players in room");
            btn.Text.fontSizeMin = btn.Text.fontSizeMax = 1.5f;
            btn.Text.color = new Color32(140, 160, 200, 200);
            btn.Background.color = Color.clear;
            btn.onState = false;
            btn.gameObject.SetActive(true);
            foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
                sr.size = new Vector2(3f, 0.5f);
            var pb = btn.GetComponent<PassiveButton>();
            pb.OnClick = new ButtonClickedEvent();
        }
    }

    private static void BuildRow(Transform parent, PlayerEntry entry, float y)
    {
        var rowGO = new GameObject($"Row_{entry.PlayerId}");
        rowGO.transform.SetParent(parent, false);
        rowGO.transform.localPosition = new Vector3(0f, y, 0f);

        // Name button (using TOR button prefab)
        var nameBtn = Object.Instantiate(_btnPrefab!, rowGO.transform);
        nameBtn.transform.localPosition = new Vector3(-1.85f, 0f, -0.1f);
        nameBtn.transform.localScale = new Vector3(0.7f, 0.8f, 1f);
        nameBtn.Text.text = entry.Name.Length > 12 ? entry.Name[..10] + "…" : entry.Name;
        nameBtn.Text.fontSizeMin = nameBtn.Text.fontSizeMax = 1.3f;
        nameBtn.Text.alignment = TextAlignmentOptions.Left;
        nameBtn.Text.color = Color.white;
        nameBtn.Background.color = Color.clear;
        nameBtn.onState = false;
        nameBtn.gameObject.SetActive(true);
        foreach (var sr in nameBtn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(1.6f, 0.45f);
        var namePb = nameBtn.GetComponent<PassiveButton>();
        namePb.OnClick = new ButtonClickedEvent();

        // Volume label button
        var volBtn = Object.Instantiate(_btnPrefab!, rowGO.transform);
        volBtn.transform.localPosition = new Vector3(2.2f, 0f, -0.1f);
        volBtn.transform.localScale = new Vector3(0.4f, 0.8f, 1f);
        volBtn.Background.color = Color.clear;
        volBtn.onState = false;
        volBtn.gameObject.SetActive(true);
        foreach (var sr in volBtn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(0.7f, 0.45f);
        var volPb = volBtn.GetComponent<PassiveButton>();
        volPb.OnClick = new ButtonClickedEvent();

        // Slider track (custom, TOR doesn't have sliders)
        var trackGO = new GameObject("Track");
        trackGO.transform.SetParent(rowGO.transform, false);
        trackGO.transform.localPosition = new Vector3(0.3f, 0f, -0.1f);
        var trackSr = trackGO.AddComponent<SpriteRenderer>();
        trackSr.sprite = Create1x1Sprite(new Color32(55, 65, 100, 200));
        trackSr.drawMode = SpriteDrawMode.Sliced;
        trackSr.size = new Vector2(SliderW, 0.10f);
        trackSr.sortingOrder = 32766;

        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(trackGO.transform, false);
        var fillSr = fillGO.AddComponent<SpriteRenderer>();
        fillSr.sprite = Create1x1Sprite(new Color32(80, 160, 235, 220));
        fillSr.drawMode = SpriteDrawMode.Sliced;
        fillSr.sortingOrder = 32767;

        var knobGO = new GameObject("Knob");
        knobGO.transform.SetParent(trackGO.transform, false);
        var knobSr = knobGO.AddComponent<SpriteRenderer>();
        knobSr.sprite = CreateCircleSprite();
        knobSr.color = Color.white;
        knobSr.sortingOrder = 32767;
        knobGO.transform.localScale = Vector3.one * 0.16f;

        var col = trackGO.AddComponent<BoxCollider2D>();
        col.size = new Vector2(SliderW, 0.40f);
        var pb = trackGO.AddComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();

        float currentVol = GetVolume(entry);
        void SetVolLabel(float v)
        {
            volBtn.Text.text = $"<color=#ffdd88>{Mathf.RoundToInt(v * 100f)}%</color>";
            volBtn.Text.fontSizeMin = volBtn.Text.fontSizeMax = 1.1f;
            volBtn.Text.alignment = TextAlignmentOptions.Center;
        }

        void PositionSlider(float v)
        {
            float t = Mathf.InverseLerp(VMin, VMax, v);
            knobGO.transform.localPosition = new Vector3((t - 0.5f) * SliderW, 0f, -0.12f);
            fillSr.size = new Vector2(t * SliderW, 0.10f);
            fillGO.transform.localPosition = new Vector3((t * SliderW - SliderW) * 0.5f, 0f, -0.11f);
        }

        SetVolLabel(currentVol);
        PositionSlider(currentVol);

        pb.OnClick.AddListener((Action)(() =>
        {
            var cam = Camera.main;
            if (!cam) return;
            var mWorld = cam.ScreenToWorldPoint(Input.mousePosition);
            var mLocal = trackGO.transform.InverseTransformPoint(mWorld);
            float t = Mathf.InverseLerp(-SliderW * 0.5f, SliderW * 0.5f, mLocal.x);
            float v = Mathf.Clamp((float)Math.Round(Mathf.Lerp(VMin, VMax, t), 2), VMin, VMax);
            ApplyVolume(entry, v);
            PositionSlider(v);
            SetVolLabel(v);
        }));

        var upd = trackGO.AddComponent<PlayerSliderDragUpdater>();
        upd.Init(trackGO, SliderW, VMin, VMax, v =>
        {
            v = Mathf.Clamp((float)Math.Round(v, 2), VMin, VMax);
            ApplyVolume(entry, v);
            PositionSlider(v);
            SetVolLabel(v);
        }, () => GetVolume(entry));

        // Divider
        var divGO = new GameObject("Div");
        divGO.transform.SetParent(rowGO.transform, false);
        divGO.transform.localPosition = new Vector3(0f, -RowH * 0.5f + 0.04f, -0.08f);
        var divSr = divGO.AddComponent<SpriteRenderer>();
        divSr.sprite = Create1x1Sprite(new Color32(50, 60, 90, 120));
        divSr.drawMode = SpriteDrawMode.Sliced;
        divSr.size = new Vector2(5.2f, 0.012f);
        divSr.sortingOrder = 32766;
    }

    private static float GetVolume(PlayerEntry entry)
    {
        if (VoiceRoom.Current != null)
            foreach (var c in VoiceRoom.Current.AllClients)
                if (c.PlayerName == entry.Name || c.PlayerId == entry.PlayerId)
                    return c.Volume;
        return 1f;
    }

    private static void ApplyVolume(PlayerEntry entry, float v)
    {
        if (VoiceRoom.Current != null)
            foreach (var c in VoiceRoom.Current.AllClients)
                if (c.PlayerName == entry.Name || c.PlayerId == entry.PlayerId)
                { c.SetVolume(v); break; }
    }

    private record PlayerEntry(byte PlayerId, string Name, Color Color);

    private static List<PlayerEntry> CollectPlayers()
    {
        var list = new List<PlayerEntry>();
        if (!AmongUsClient.Instance) return list;
        var seen = new HashSet<byte>();

        if (VoiceRoom.Current != null)
            foreach (var c in VoiceRoom.Current.AllClients)
            {
                if (c.PlayerId == byte.MaxValue || !seen.Add(c.PlayerId)) continue;
                var pc = FindPlayer(c.PlayerId);
                list.Add(new PlayerEntry(c.PlayerId, c.PlayerName, PaletteColor(pc)));
            }

        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (!pc || pc.Data == null || pc == PlayerControl.LocalPlayer) continue;
            if (!seen.Add(pc.PlayerId)) continue;
            list.Add(new PlayerEntry(pc.PlayerId, pc.Data.PlayerName, PaletteColor(pc)));
        }
        return list;
    }

    private static PlayerControl? FindPlayer(byte id)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc && pc.PlayerId == id) return pc;
        return null;
    }

    private static Color PaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f);
        int cid = pc.Data.DefaultOutfit.ColorId;
        return cid >= 0 && cid < Palette.PlayerColors.Length ? Palette.PlayerColors[cid] : Color.white;
    }

    private static void SmallBtn(string label, Vector3 pos, Action onClick)
    {
        var btn = Object.Instantiate(_btnPrefab!, _popUp!.transform);
        btn.name = $"Btn_{label}";
        btn.transform.localPosition = pos;
        btn.transform.localScale = new Vector3(0.35f, 0.7f, 1f);
        btn.Text.text = label;
        btn.Text.fontSizeMin = btn.Text.fontSizeMax = 1.6f;
        btn.Text.alignment = TextAlignmentOptions.Center;
        btn.Text.color = new Color32(200, 215, 255, 255);
        btn.Background.color = new Color32(52, 64, 98, 255);
        btn.onState = false;
        btn.gameObject.SetActive(true);
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(0.5f, 0.4f);
        var pb = btn.GetComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => onClick()));
    }

    private static Sprite Create1x1Sprite(Color32 c)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, c); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }

    private static Sprite? _circleSprite;
    private static Sprite CreateCircleSprite()
    {
        if (_circleSprite) return _circleSprite;
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        float r = S * 0.5f;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = x - r + 0.5f, dy = y - r + 0.5f;
                tex.SetPixel(x, y, Color.white with { a = Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) * 2f) });
            }
        tex.Apply();
        _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        return _circleSprite;
    }
}

public class PlayerSliderDragUpdater : MonoBehaviour
{
    private float _min, _max, _trackW;
    private bool _dragging;
    private Action<float>? _onChange;
    private Func<float>? _getCurrent;
    private float _lastApplied;

    public void Init(GameObject track, float trackW, float min, float max,
        Action<float> onChange, Func<float> getCurrent)
    {
        _trackW = trackW; _min = min; _max = max;
        _onChange = onChange; _getCurrent = getCurrent;
    }

    void OnMouseDown() => _dragging = true;
    void OnMouseUp() => _dragging = false;

    void Update()
    {
        if (!_dragging) return;
        var cam = Camera.main;
        if (!cam) return;
        var mWorld = cam.ScreenToWorldPoint(Input.mousePosition);
        var mLocal = transform.InverseTransformPoint(mWorld);
        float t = Mathf.InverseLerp(-_trackW * 0.5f, _trackW * 0.5f, mLocal.x);
        float v = Mathf.Lerp(_min, _max, t);
        v = (float)Math.Round(v, 2);
        if (Math.Abs(v - _lastApplied) > 0.01f)
        {
            _lastApplied = v;
            _onChange?.Invoke(v);
        }
    }
}
