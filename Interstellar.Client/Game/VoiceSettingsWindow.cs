#pragma warning disable CS8602, CS8603, CS8618
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Interstellar.Voice.TranslationHelper;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

/// <summary>
/// Voice Chat settings window - native uGUI implementation.
/// Mimics TheOtherRoles MetaScreen / PresetManager page style:
/// dark rounded panel + white border + bold title + list rows + colored action buttons + input popup / dropdown.
/// Uses "refresh-style" rendering (rebuilds content rows on Open / server change),
/// same as PresetManager's UpdatePresetScreen.
/// </summary>
public class VoiceSettingsWindow : MonoBehaviour
{
    public VoiceSettingsWindow(System.IntPtr ptr) : base(ptr) { }

    public static VoiceSettingsWindow? Instance { get; private set; }
    public bool ShowWindow { get; private set; }

    private const KeyCode ToggleKey = KeyCode.F1;

    // Layout constants (1920x1080 base, scaled by CanvasScaler)
    private const float WinW = 900f;
    private const float WinH = 900f;
    private const float TitleBarH = 64f;
    private const float BottomH = 56f;
    private const float RowH = 64f;
    private const float ContentW = WinW - 96f;
    private const float ContentRight = ContentW / 2f - 40f;

    private bool _isAndroid => Application.platform == RuntimePlatform.Android;
    private float F(float px) => _isAndroid ? px * 1.28f : px;

    // UI refs
    private GameObject _uiRoot;
    private RectTransform _winRt;
    private Canvas _canvas;
    private ScrollRect _scroll;
    private RectTransform _content;

    // State
    private bool _built;
    private bool _needsDeviceRefresh = true;
    private bool _dragging;
    private Vector2 _dragOffset;

    private static readonly string[] Langs = { "en", "zh_CN", "ja", "ko", "ru", "es", "pt_BR", "Other" };

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_uiRoot != null) Object.Destroy(_uiRoot);
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey)) Toggle();
        if (ShowWindow && Input.GetKeyDown(KeyCode.Escape)) Close();
        if (VCTextInputPopup.IsShowing && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            VCTextInputPopup.Confirm();
    }

    public void Toggle()
    {
        if (ShowWindow) Close(); else Open();
    }

    public void Open()
    {
        try
        {
            _dragging = false;
            VCUiKit.AnyWindowDragging = false;

            if (!_built) BuildUI();
            if (_uiRoot == null)
            {
                InterstellarPlugin.Logger?.LogError("[VC] Settings UI failed to build (_uiRoot is null).");
                return;
            }

            if (_needsDeviceRefresh)
            {
                VoiceConfig.RefreshDeviceCaches(true);
                _needsDeviceRefresh = false;
            }

            // Align our overlay canvas to the game's display (multi-monitor safety)
            try
            {
                var cam = Object.FindObjectOfType<Camera>();
                if (cam != null && _canvas != null) _canvas.targetDisplay = cam.targetDisplay;
            }
            catch { }

            // avoid stacking two windows on top of each other
            try { PublicLobbyWindow.Instance?.Close(); } catch { }

            _uiRoot.SetActive(true);
            ShowWindow = true;

            var opt = Object.FindObjectOfType<OptionsMenuBehaviour>();
            if (opt) opt.Close();

            RebuildContent();
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;

            try
            {
                InterstellarPlugin.Logger?.LogInfo(
                    $"[VC] Settings shown: canvasActive={_canvas.gameObject.activeSelf} " +
                    $"renderMode={_canvas.renderMode} sortingOrder={_canvas.sortingOrder} " +
                    $"uiRootActive={_uiRoot.activeSelf} winPos={_winRt.anchoredPosition} " +
                    $"winSize={_winRt.sizeDelta} scaleFactor={_canvas.scaleFactor}");
            }
            catch { }
        }
        catch (Exception e)
        {
            InterstellarPlugin.Logger?.LogError($"[VC] Open settings failed: {e}");
            // Reset so the next F1 retries building instead of staying broken forever.
            _built = false;
            _uiRoot = null;
        }
    }

    public void Close()
    {
        ShowWindow = false;
        _dragging = false;
        VCUiKit.AnyWindowDragging = false;
        VCTextInputPopup.Hide();
        VCDropdown.Hide();
        if (_uiRoot != null) _uiRoot.SetActive(false);
    }

    // ========================================================
    //  Window frame (built once)
    // ========================================================
    private void BuildUI()
    {
        if (_uiRoot != null) Object.Destroy(_uiRoot);
        _canvas = VCUiKit.EnsureCanvas();

        _uiRoot = new GameObject("VCSettingsUI");
        _uiRoot.transform.SetParent(_canvas.transform, false);
        var rootRt = _uiRoot.AddComponent<RectTransform>();
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = Vector2.zero;
        rootRt.offsetMax = Vector2.zero;
        _uiRoot.SetActive(false);

        // Full-screen dim (click outside to close)
        var dim = VCUiKit.CreateImage(_uiRoot.transform, "Dim", Vector2.zero, Vector2.zero, VCUiKit.PixelSprite, new Color(0f, 0f, 0f, 0.42f));
        var dimRt = (RectTransform)dim.transform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = Vector2.zero;
        dimRt.offsetMax = Vector2.zero;
        var dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener((Action)(() => Close()));

        // Window panel (white outline + dark inner, dragged together)
        _winRt = VCUiKit.CreatePanel(_uiRoot.transform, "Window", new Vector2(WinW, WinH),
            new Color(0.88f, 0.94f, 1f, 1f), new Color(0.07f, 0.10f, 0.16f, 0.97f), 6f);
        _winRt.anchorMin = _winRt.anchorMax = new Vector2(0.5f, 0.5f);
        _winRt.anchoredPosition = Vector2.zero;

        BuildTitleBar(_winRt);
        BuildScrollArea(_winRt);
        BuildBottomBar(_winRt);
        _built = true;
    }

    private void BuildTitleBar(Transform win)
    {
        // Title
        var title = VCUiKit.CreateText(win, "Title", Get("vc.settings.title", "Voice Chat Settings"),
            Vector2.zero, new Vector2(560f, 44f), F(28f), new Color(0.92f, 0.95f, 1f, 1f),
            FontStyles.Bold, TextAlignmentOptions.Left);
        var titleRt = (RectTransform)title.transform;
        titleRt.anchorMin = new Vector2(0f, 0.5f);
        titleRt.anchorMax = new Vector2(0f, 0.5f);
        titleRt.pivot = new Vector2(0f, 0.5f);
        titleRt.anchoredPosition = new Vector2(30f, WinH / 2f - TitleBarH / 2f);

        // Public Lobby button (right of title bar)
        var lobby = VCUiKit.CreateButton(win, Get("vc.settings.publicLobby", "Public Lobby"),
            Vector2.zero, new Vector2(220f, 44f), new Color(0.20f, 0.42f, 0.80f, 1f), () =>
            {
                PublicLobbyWindow.Instance?.Toggle();
            }, F(20f));
        var lobbyRt = (RectTransform)lobby.transform;
        lobbyRt.anchorMin = lobbyRt.anchorMax = new Vector2(1f, 0.5f);
        lobbyRt.anchoredPosition = new Vector2(-185f, WinH / 2f - TitleBarH / 2f);

        // Close button (top right)
        var close = VCUiKit.CreateButton(win, "X", Vector2.zero, new Vector2(44f, 44f),
            new Color(0.58f, 0.22f, 0.24f, 1f), () => Close(), F(24f));
        var closeRt = (RectTransform)close.transform;
        closeRt.anchorMin = closeRt.anchorMax = new Vector2(1f, 0.5f);
        closeRt.anchoredPosition = new Vector2(-34f, WinH / 2f - TitleBarH / 2f);
    }

    private void BuildScrollArea(Transform win)
    {
        float topY = WinH / 2f - TitleBarH - 10f;
        float bottomY = -WinH / 2f + BottomH + 10f;
        float viewH = topY - bottomY;

        var viewport = VCUiKit.NewRect(win, "Viewport");
        viewport.anchorMin = viewport.anchorMax = new Vector2(0.5f, 0.5f);
        viewport.anchoredPosition = new Vector2(0f, (topY + bottomY) / 2f);
        viewport.sizeDelta = new Vector2(ContentW + 20f, viewH);
        viewport.gameObject.AddComponent<RectMask2D>();

        _content = VCUiKit.NewRect(viewport, "Content");
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(0f, 1f);
        _content.pivot = new Vector2(0f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = new Vector2(ContentW, 10f);

        // Scroll grab background (transparent, lets you scroll on empty areas)
        var bg = VCUiKit.CreateImage(_content, "ScrollBG", Vector2.zero, _content.sizeDelta, VCUiKit.PixelSprite, Color.clear);
        var bgRt = bg.rectTransform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        bgRt.SetAsFirstSibling();

        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = _content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;
        scroll.inertia = true;
        scroll.verticalNormalizedPosition = 1f;
        _scroll = scroll;
    }

    private void BuildBottomBar(Transform win)
    {
        var ver = VCUiKit.CreateText(win, "Version", "Interstellar v" + InterstellarPlugin.PluginVersion,
            Vector2.zero, new Vector2(400f, 30f), F(16f), new Color(0.60f, 0.66f, 0.76f, 1f),
            FontStyles.Normal, TextAlignmentOptions.Left);
        var verRt = (RectTransform)ver.transform;
        verRt.anchorMin = new Vector2(0f, 0f);
        verRt.anchorMax = new Vector2(0f, 0f);
        verRt.pivot = new Vector2(0f, 0f);
        verRt.anchoredPosition = new Vector2(30f, 14f);
    }

    /// <summary>Drag the window by its title bar, polled in Update (no EventTrigger, IL2CPP-safe).</summary>
    private void UpdateDrag()
    {
        if (!ShowWindow || _winRt == null || _canvas == null) return;

        float scale = Mathf.Max(0.001f, _canvas.scaleFactor);
        // Mouse position in canvas units, relative to canvas center
        Vector2 mouseCanvas = (Vector2)Input.mousePosition - new Vector2(Screen.width, Screen.height) * 0.5f;
        mouseCanvas /= scale;

        Vector2 winPos = _winRt.anchoredPosition;
        Rect title = new Rect(winPos.x - (WinW - 120f) * 0.5f, winPos.y + WinH / 2f - TitleBarH, WinW - 120f, TitleBarH);

        if (Input.GetMouseButtonDown(0) && title.Contains(mouseCanvas) && !VCUiKit.AnyWindowDragging)
        {
            _dragging = true;
            VCUiKit.AnyWindowDragging = true;
            _dragOffset = mouseCanvas - winPos;
            VCDropdown.Hide();
        }
        if (Input.GetMouseButtonUp(0) && _dragging)
        {
            _dragging = false;
            VCUiKit.AnyWindowDragging = false;
        }
        // Safety: if the mouse is no longer held, stop dragging even if the up event was missed
        // (e.g. the window was closed and reopened while the pointer was down).
        if (_dragging && !Input.GetMouseButton(0))
        {
            _dragging = false;
            VCUiKit.AnyWindowDragging = false;
        }

        if (_dragging)
        {
            _winRt.anchoredPosition = mouseCanvas - _dragOffset;
            ClampWindow();
        }
    }

    private void ClampWindow()
    {
        var p = _winRt.anchoredPosition;
        p.x = Mathf.Clamp(p.x, -_canvas.pixelRect.width / (2f * _canvas.scaleFactor) + WinW / 2f,
            _canvas.pixelRect.width / (2f * _canvas.scaleFactor) - WinW / 2f);
        p.y = Mathf.Clamp(p.y, -_canvas.pixelRect.height / (2f * _canvas.scaleFactor) + WinH / 2f,
            _canvas.pixelRect.height / (2f * _canvas.scaleFactor) - WinH / 2f);
        _winRt.anchoredPosition = p;
    }

    // ========================================================
    //  Content rendering (refresh-style)
    // ========================================================
    private float _y;

    private void RebuildContent()
    {
        if (_content == null) return;
        float keepScroll = _scroll != null ? _scroll.verticalNormalizedPosition : 1f;

        for (int i = _content.childCount - 1; i >= 0; i--)
        {
            var child = _content.GetChild(i);
            if (child.name == "ScrollBG") continue;
            Object.Destroy(child.gameObject);
        }

        _y = 0f;
        bool isHost = (AmongUsClient.Instance?.AmHost ?? false)
            && AmongUsClient.Instance?.GameState == InnerNet.InnerNetClient.GameStates.Joined;

        try
        {
            RenderServerSection();
            AddGap(26f);
            RenderPersonalSection();
            AddGap(26f);
            RenderRoomSection(isHost);
            AddGap(26f);
            RenderPublicLobbySection(isHost);
            AddGap(26f);
            RenderAdvancedSection();
        }
        catch (Exception e)
        {
            InterstellarPlugin.Logger?.LogError($"[VC] RebuildContent sections failed: {e}");
        }

        _content.sizeDelta = new Vector2(ContentW, _y + 24f);
        if (_scroll != null) _scroll.verticalNormalizedPosition = keepScroll;
    }

    private void AddGap(float g) => _y += g;

    private RectTransform AddRow()
    {
        var row = VCUiKit.NewRect(_content, "Row");
        row.anchorMin = row.anchorMax = new Vector2(0f, 1f);
        row.pivot = new Vector2(0f, 1f);
        row.anchoredPosition = new Vector2(0f, -_y);
        row.sizeDelta = new Vector2(ContentW, RowH);
        _y += RowH;

        // Row divider (PresetManager list style)
        var div = VCUiKit.CreateDivider(row, Vector2.zero, new Vector2(ContentW - 40f, 2f));
        var divRt = div.rectTransform;
        divRt.anchorMin = new Vector2(0f, 0f);
        divRt.anchorMax = new Vector2(0f, 0f);
        divRt.pivot = new Vector2(0f, 0f);
        divRt.anchoredPosition = new Vector2(20f, 3f);
        divRt.sizeDelta = new Vector2(ContentW - 40f, 2f);
        return row;
    }

    private void AddSectionTitle(string text)
    {
        var tmp = VCUiKit.CreateText(_content, "SectionTitle", text,
            Vector2.zero, new Vector2(ContentW - 40f, 44f), F(27f),
            new Color(0.55f, 0.72f, 0.95f, 1f), FontStyles.Bold, TextAlignmentOptions.Left);
        var rt = (RectTransform)tmp.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(20f, -_y);
        rt.sizeDelta = new Vector2(ContentW - 40f, 44f);
        _y += 58f;
    }

    private TextMeshProUGUI AddLabel(Transform row, string text, float width = 360f, float fontSize = 0f)
    {
        var tmp = VCUiKit.CreateText(row, "Label", text,
            new Vector2(-ContentW / 2f + 70f + width / 2f, 0f), new Vector2(width, RowH - 12f),
            fontSize <= 0 ? F(23f) : fontSize, Color.white, FontStyles.Bold, TextAlignmentOptions.Left, true);
        return tmp;
    }

    private void AddRowToggle(Transform row, string label, Func<bool> getter, Action<bool> setter, bool enabled = true)
    {
        AddLabel(row, label);
        var tog = VCUiKit.CreateToggle(row, "T", Vector2.zero, new Vector2(110f, 44f), getter, setter, F(20f));
        tog.interactable = enabled;
        var trt = (RectTransform)tog.transform;
        trt.anchorMin = trt.anchorMax = new Vector2(1f, 0.5f);
        trt.anchoredPosition = new Vector2(-40f, 0f);
    }

    private void AddRowSlider(Transform row, string label, float min, float max, float value, Action<float> onChange,
        Func<float, string> formatter, bool enabled = true)
    {
        AddLabel(row, label);

        var valueTmp = VCUiKit.CreateText(row, "Value", formatter(value), Vector2.zero, new Vector2(70f, RowH - 12f),
            F(20f), new Color(1f, 0.86f, 0.55f, 1f), FontStyles.Bold, TextAlignmentOptions.Right);
        var vrt = (RectTransform)valueTmp.transform;
        vrt.anchorMin = vrt.anchorMax = new Vector2(1f, 0.5f);
        vrt.anchoredPosition = new Vector2(-40f, 0f);

        float sliderW = 250f;
        VCUiKit.CreateSlider(row, new Vector2(ContentRight - 40f - 70f - 20f - sliderW / 2f, 0f),
            new Vector2(sliderW, 44f), min, max, value,
            v => { onChange(v); valueTmp.text = formatter(v); }, 10f, enabled);
    }

    // -- Server ----------------------------------------------
    private void RenderServerSection()
    {
        AddSectionTitle(Get("vc.settings.server", "Server"));

        var row = AddRow();
        AddLabel(row, Get("vc.settings.server", "Server") + ":");

        var serverNames = ServerList.GetServerNames();
        int cur = VoiceConfig.SelectedServerIndex;
        string curName = cur >= 0 && cur < serverNames.Length ? serverNames[cur] : "Custom...";

        // Refresh button (rightmost)
        var refresh = VCUiKit.CreateButton(row, Get("vc.settings.refresh", "Refresh"),
            Vector2.zero, new Vector2(110f, 44f), new Color(0.30f, 0.36f, 0.48f, 1f),
            () => VoiceRoom.RestartForCurrentGame(), F(19f));
        var rrt = (RectTransform)refresh.transform;
        rrt.anchorMin = rrt.anchorMax = new Vector2(1f, 0.5f);
        rrt.anchoredPosition = new Vector2(-40f, 0f);

        // Server selector button
        var serverBtn = VCUiKit.CreateButton(row, curName + "  v", Vector2.zero, new Vector2(330f, 44f),
            new Color(0.16f, 0.21f, 0.32f, 1f), () =>
            {
                VCDropdown.Show(Get("vc.settings.server", "Server"), serverNames, cur, OnServerSelected);
            }, F(19f));
        var srt = (RectTransform)serverBtn.transform;
        srt.anchorMin = srt.anchorMax = new Vector2(1f, 0.5f);
        srt.anchoredPosition = new Vector2(-40f - 110f - 14f - 165f, 0f);

        // Custom URL row
        if (cur >= serverNames.Length - 1)
        {
            var urlRow = AddRow();
            AddLabel(urlRow, Get("vc.settings.url", "URL") + ":");

            var edit = VCUiKit.CreateButton(urlRow, Get("vc.settings.edit", "Edit"),
                Vector2.zero, new Vector2(90f, 44f), new Color(0.16f, 0.42f, 0.70f, 1f), () =>
                {
                    VCTextInputPopup.Show(Get("vc.settings.customUrl", "Custom Server URL"),
                        "https://...", VoiceConfig.CustomServerURL, 200, v =>
                        {
                            VoiceConfig.CustomServerURL = v;
                            RebuildContent();
                        });
                }, F(19f));
            var ert = (RectTransform)edit.transform;
            ert.anchorMin = ert.anchorMax = new Vector2(1f, 0.5f);
            ert.anchoredPosition = new Vector2(-40f, 0f);

            string urlText = string.IsNullOrEmpty(VoiceConfig.CustomServerURL)
                ? Get("vc.settings.empty", "(empty)") : VoiceConfig.CustomServerURL;
            VCUiKit.CreateText(urlRow, "Url", Truncate(urlText, 30), Vector2.zero,
                new Vector2(430f, RowH - 12f), F(18f), new Color(0.68f, 0.73f, 0.84f, 1f),
                FontStyles.Normal, TextAlignmentOptions.Left, true);
        }
    }

    private void OnServerSelected(int idx)
    {
        VoiceConfig.SelectedServerIndex = idx;
        VoiceRoom.RestartForCurrentGame();
        RebuildContent();
    }

    // -- Personal --------------------------------------------
    private void RenderPersonalSection()
    {
        AddSectionTitle(Get("vc.settings.personal", "Personal"));

        if (VoiceConfig.DeviceSelectionSupported)
        {
            RenderDeviceRow(Get("vc.settings.microphone", "Microphone"), VoiceConfig.MicrophoneDevice,
                VoiceConfig.MicrophoneDevices, v =>
                {
                    VoiceConfig.SetMicrophoneDevice(v);
                    VoiceRoom.Current?.SetMicrophone(v);
                    RebuildContent();
                });
            RenderDeviceRow(Get("vc.settings.speaker", "Speaker"), VoiceConfig.SpeakerDevice,
                VoiceConfig.SpeakerDevices, v =>
                {
                    VoiceConfig.SetSpeakerDevice(v);
                    VoiceRoom.Current?.SetSpeaker(v);
                    RebuildContent();
                });
        }
        else
        {
            var row = AddRow();
            VCUiKit.CreateText(row, "NoDev", Get("vc.settings.noDeviceSupport", "Device selection not supported on this platform."),
                new Vector2(-ContentW / 2f + 300f, 0f), new Vector2(ContentW - 120f, RowH - 12f),
                F(18f), Color.gray, FontStyles.Normal, TextAlignmentOptions.Left, true);
        }

        AddRowSlider(Get("vc.settings.micVolume", "Mic Volume") + ":", 0.1f, 2f, VoiceConfig.MicVolume,
            v => { VoiceConfig.SetMicVolume(v); VoiceRoom.Current?.SetMicVolume(v); },
            v => $"{v * 100f:F0}%");
        AddRowSlider(Get("vc.settings.masterVolume", "Master Volume") + ":", 0.1f, 2f, VoiceConfig.MasterVolume,
            v => { VoiceConfig.SetMasterVolume(v); VoiceRoom.Current?.SetMasterVolume(v); },
            v => $"{v * 100f:F0}%");
    }

    private void RenderDeviceRow(string label, string current, System.Collections.Generic.List<string> options, Action<string> onSelect)
    {
        var row = AddRow();
        AddLabel(row, label + ":");

        float devW = 240f;
        float right = ContentRight;
        string display = string.IsNullOrEmpty(current) ? Get("vc.settings.defaultDevice", "Default") : Truncate(current, 22);

        var devTmp = VCUiKit.CreateText(row, "Dev", display, Vector2.zero, new Vector2(devW, RowH - 12f),
            F(19f), Color.white, FontStyles.Normal, TextAlignmentOptions.Center);
        var drt = (RectTransform)devTmp.transform;
        drt.anchorMin = drt.anchorMax = new Vector2(1f, 0.5f);
        drt.anchoredPosition = new Vector2(-(40f + 30f + 24f + devW / 2f), 0f);

        VCUiKit.CreateButton(row, "<", new Vector2(right - 30f - 24f - devW - 24f - 15f, 0f), new Vector2(30f, 40f),
            new Color(0.30f, 0.36f, 0.48f, 1f), () =>
            {
                int idx = Mathf.Max(0, options.IndexOf(current ?? ""));
                int n = (idx - 1 + options.Count) % options.Count;
                onSelect(options[n]);
            }, F(20f));

        VCUiKit.CreateButton(row, ">", new Vector2(right - 15f, 0f), new Vector2(30f, 40f),
            new Color(0.30f, 0.36f, 0.48f, 1f), () =>
            {
                int idx = Mathf.Max(0, options.IndexOf(current ?? ""));
                int n = (idx + 1) % options.Count;
                onSelect(options[n]);
            }, F(20f));
    }

    private void AddRowSlider(string label, float min, float max, float value, Action<float> onChange, Func<float, string> formatter, bool enabled = true)
    {
        var row = AddRow();
        AddRowSlider(row, label, min, max, value, onChange, formatter, enabled);
    }

    // -- Room ------------------------------------------------
    private void RenderRoomSection(bool isHost)
    {
        AddSectionTitle(Get("vc.settings.room", "Room Settings"));

        void RoomChanged()
        {
            VoiceConfig.ApplyLocalHostSettingsToSynced();
            InterstellarHudState.MarkRoomSettingsDirty();
        }

        AddRowSlider(Get("vc.settings.maxChatDistance", "Max Chat Distance") + ":", 1.5f, 20f,
            isHost ? VoiceConfig.HostMaxChatDistance : VoiceConfig.SyncedRoomSettings.MaxChatDistance,
            v => { VoiceConfig.SetHostMaxChatDistance(v); RoomChanged(); },
            v => $"{v:F1}m", isHost);

        AddHostToggle(Get("vc.settings.wallsBlockSound", "Walls Block Sound"), () => VoiceConfig.SyncedRoomSettings.WallsBlockSound,
            v => { VoiceConfig.SetHostWallsBlockSound(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.onlyHearInSight", "Only Hear In Sight"), () => VoiceConfig.SyncedRoomSettings.OnlyHearInSight,
            v => { VoiceConfig.SetHostOnlyHearInSight(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.impostorHearGhosts", "Impostor Hear Ghosts"), () => VoiceConfig.SyncedRoomSettings.ImpostorHearGhosts,
            v => { VoiceConfig.SetHostImpostorHearGhosts(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.onlyGhostsCanTalk", "Only Ghosts Can Talk"), () => VoiceConfig.SyncedRoomSettings.OnlyGhostsCanTalk,
            v => { VoiceConfig.SetHostOnlyGhostsCanTalk(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.hearInVent", "Hear Outside In Vent"), () => VoiceConfig.SyncedRoomSettings.HearInVent,
            v => { VoiceConfig.SetHostHearInVent(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.hearVentPlayers", "Hear Players In Vent"), () => VoiceConfig.SyncedRoomSettings.HearVentPlayers,
            v => { VoiceConfig.SetHostHearVentPlayers(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.ventPrivateChat", "Vent Private Chat"), () => VoiceConfig.SyncedRoomSettings.VentPrivateChat,
            v => { VoiceConfig.SetHostVentPrivateChat(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.commsSabotageMutes", "Comms Sabotage Mutes"), () => VoiceConfig.SyncedRoomSettings.CommsSabDisables,
            v => { VoiceConfig.SetHostCommsSabDisables(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.cameraCanHear", "Hear Through Cameras"), () => VoiceConfig.SyncedRoomSettings.CameraCanHear,
            v => { VoiceConfig.SetHostCameraCanHear(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.impostorPrivateRadio", "Impostor Private Radio"), () => VoiceConfig.SyncedRoomSettings.ImpostorPrivateRadio,
            v => { VoiceConfig.SetHostImpostorPrivateRadio(v); RoomChanged(); }, isHost);
        AddHostToggle(Get("vc.settings.onlyMeetingOrLobby", "Only Meeting / Lobby"), () => VoiceConfig.SyncedRoomSettings.OnlyMeetingOrLobby,
            v => { VoiceConfig.SetHostOnlyMeetingOrLobby(v); RoomChanged(); }, isHost);
    }

    private void AddHostToggle(string label, Func<bool> getter, Action<bool> setter, bool isHost)
    {
        var row = AddRow();
        AddRowToggle(row, label, getter, setter, isHost);
    }

    // -- Public Lobby ----------------------------------------
    private void RenderPublicLobbySection(bool isHost)
    {
        AddSectionTitle(Get("vc.settings.publicLobby", "Public Lobby"));

        // Enable
        var row = AddRow();
        AddLabel(row, Get("vc.settings.publicLobbyEnable", "Enable Public Lobby"));
        var tog = VCUiKit.CreateToggle(row, "T", Vector2.zero, new Vector2(110f, 44f),
            () => VoiceConfig.PublicLobbyEnabled,
            v => { VoiceConfig.PublicLobbyEnabled = v; RebuildContent(); }, F(20f));
        tog.interactable = isHost;
        var trt = (RectTransform)tog.transform;
        trt.anchorMin = trt.anchorMax = new Vector2(1f, 0.5f);
        trt.anchoredPosition = new Vector2(-40f, 0f);

        if (VoiceConfig.PublicLobbyEnabled)
        {
            // Title
            var titleRow = AddRow();
            AddLabel(titleRow, Get("vc.settings.title", "Title") + ":");
            var edit = VCUiKit.CreateButton(titleRow, Get("vc.settings.edit", "Edit"),
                Vector2.zero, new Vector2(90f, 44f), new Color(0.16f, 0.42f, 0.70f, 1f), () =>
                {
                    VCTextInputPopup.Show(Get("vc.settings.publicLobbyTitle", "Public Lobby Title"),
                        "Among Us Lobby", VoiceConfig.PublicLobbyTitle, 40, v =>
                        {
                            VoiceConfig.PublicLobbyTitle = v;
                            RebuildContent();
                        });
                }, F(19f));
            var ert = (RectTransform)edit.transform;
            ert.anchorMin = ert.anchorMax = new Vector2(1f, 0.5f);
            ert.anchoredPosition = new Vector2(-40f, 0f);

            VCUiKit.CreateText(titleRow, "Txt", Truncate(VoiceConfig.PublicLobbyTitle, 26), Vector2.zero,
                new Vector2(430f, RowH - 12f), F(19f), new Color(0.72f, 0.77f, 0.88f, 1f),
                FontStyles.Normal, TextAlignmentOptions.Left, true);

            // Language
            var langRow = AddRow();
            AddLabel(langRow, Get("vc.settings.language", "Language") + ":");
            int langIdx = Mathf.Max(0, Array.IndexOf(Langs, VoiceConfig.PublicLobbyLanguage));
            if (langIdx < 0) langIdx = Langs.Length - 1;
            var langBtn = VCUiKit.CreateButton(langRow, Langs[langIdx] + "  v", Vector2.zero, new Vector2(200f, 44f),
                new Color(0.16f, 0.21f, 0.32f, 1f), () =>
                {
                    VCDropdown.Show(Get("vc.settings.language", "Language"), Langs, langIdx,
                        i => { VoiceConfig.PublicLobbyLanguage = Langs[i]; RebuildContent(); });
                }, F(19f));
            var lrt = (RectTransform)langBtn.transform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(1f, 0.5f);
            lrt.anchoredPosition = new Vector2(-40f - 100f, 0f);
        }
    }

    // -- Advanced --------------------------------------------
    private void RenderAdvancedSection()
    {
        AddSectionTitle(Get("vc.settings.advanced", "Advanced"));

        var row = AddRow();
        AddRowToggle(row, Get("vc.settings.noiseSuppression", "Noise Suppression"),
            () => VoiceConfig.NoiseSuppression, v => VoiceConfig.NoiseSuppression = v);

        row = AddRow();
        AddRowToggle(row, Get("vc.settings.echoCancellation", "Echo Cancellation"),
            () => VoiceConfig.EchoCancellation, v => VoiceConfig.EchoCancellation = v);

        row = AddRow();
        AddRowToggle(row, Get("vc.settings.vad", "VAD (Voice Activity Detection)"),
            () => VoiceConfig.VADEnabled, v => VoiceConfig.VADEnabled = v);
    }

    // -- Util ------------------------------------------------
    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}
