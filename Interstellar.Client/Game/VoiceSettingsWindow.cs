using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using static Interstellar.Voice.TranslationHelper;

namespace Interstellar.Voice;

public class VoiceSettingsWindow : MonoBehaviour
{
    public VoiceSettingsWindow(System.IntPtr ptr) : base(ptr) { }

    public static VoiceSettingsWindow? Instance { get; private set; }

    public bool ShowWindow { get; private set; }

    public void Toggle()
    {
        ShowWindow = !ShowWindow;
        if (ShowWindow)
        {
            _needsDeviceRefresh = true;
            var opt = UnityEngine.Object.FindObjectOfType<OptionsMenuBehaviour>();
            if (opt) opt.Close();
        }
    }

    public void Open()
    {
        if (!ShowWindow) Toggle();
    }

    public void Close()
    {
        ShowWindow = false;
    }

    private Vector2 _scrollPosition;
    private Vector2 _serverScrollPos;
    private bool _needsDeviceRefresh = true;
    private bool _showServerDropdown;
    private bool _showLangDropdown;

    // Draggable window
    private Rect _winRect;
    private bool _winInitialized;
    private bool _isDragging;
    private Vector2 _dragOffset;

    // F1 toggles
    private const KeyCode ToggleKey = KeyCode.F1;

    private GUIStyle? _sectionLabelStyle;
    private GUIStyle? _boxStyle;
    private GUIStyle? _titleStyle;
    private GUIStyle? _serverBtnStyle;
    private bool _stylesBuilt;

    void Awake()
    {
        Instance = this;
        SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= (Action<Scene, LoadSceneMode>)OnSceneLoaded;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey))
            Toggle();

        if (ShowWindow && Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    void OnGUI()
    {
        if (!ShowWindow) return;

        if (_needsDeviceRefresh)
        {
            VoiceConfig.RefreshDeviceCaches(true);
            _needsDeviceRefresh = false;
        }

        BuildStyles();

        bool isAndroid = Application.platform == RuntimePlatform.Android;
        float winW = isAndroid ? 640f : 480f;
        float winH = isAndroid ? 800f : 680f;

        if (!_winInitialized)
        {
            _winRect = new Rect((Screen.width - winW) / 2f, (Screen.height - winH) / 2f, winW, winH);
            _winInitialized = true;
        }
        _winRect.width = winW;
        _winRect.height = winH;

        // ── Drag handling ──
        const float titleH = 28f;
        var titleRect = new Rect(_winRect.x, _winRect.y, _winRect.width, titleH);
        var ev = Event.current;
        if (ev.type == EventType.MouseDown && titleRect.Contains(ev.mousePosition))
        {
            _isDragging = true;
            _dragOffset = ev.mousePosition - new Vector2(_winRect.x, _winRect.y);
            ev.Use();
        }
        if (ev.type == EventType.MouseUp) _isDragging = false;
        if (_isDragging && ev.type == EventType.MouseDrag)
        {
            _winRect.x = ev.mousePosition.x - _dragOffset.x;
            _winRect.y = ev.mousePosition.y - _dragOffset.y;
            ev.Use();
        }
        _winRect.x = Mathf.Clamp(_winRect.x, -_winRect.width + 60f, Screen.width - 60f);
        _winRect.y = Mathf.Clamp(_winRect.y, -titleH + 8f, Screen.height - 30f);

        // ── Background (opaque for mobile readability) ──
        var oldColor = GUI.color;
        GUI.color = new Color(0.08f, 0.10f, 0.16f, 1f);
        GUI.Box(new Rect(_winRect.x - 4, _winRect.y - 4, _winRect.width + 8, _winRect.height + 8), "");
        GUI.color = new Color(0.10f, 0.12f, 0.18f, 1f);
        GUI.Box(_winRect, "");
        GUI.color = oldColor;

        // ── Title bar (drag handle) ──
        GUI.Label(titleRect, "  ≡  <b>Voice Chat Settings</b>", _titleStyle);

        float btnH = isAndroid ? 36f : 24f;
        float closeBtnSize = isAndroid ? 44f : 22f;

        GUILayout.BeginArea(new Rect(_winRect.x + 10, _winRect.y + titleH + 2, _winRect.width - 20, _winRect.height - titleH - 12));
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("🌐 Public Lobbies (F2)", GUILayout.Width(160f), GUILayout.Height(btnH)))
            {
                PublicLobbyWindow.Instance?.Toggle();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(closeBtnSize + 8f), GUILayout.Height(closeBtnSize)))
                Close();
            GUILayout.EndHorizontal();

            GUILayout.Space(5f);

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(_winRect.height - titleH - 48f));
            {
                bool isHost = (AmongUsClient.Instance?.AmHost ?? false)
                    && AmongUsClient.Instance?.GameState == InnerNet.InnerNetClient.GameStates.Joined;

                RenderServerSection();
                GUILayout.Space(16f);
                RenderPersonalSection();
                GUILayout.Space(16f);
                RenderRoomSection(isHost);
                GUILayout.Space(16f);
                RenderPublicLobbySection(isHost);
                GUILayout.Space(16f);
                RenderAdvancedSection();
                GUILayout.Space(10f);
            }
            GUILayout.EndScrollView();
        }
        GUILayout.EndArea();
    }

    void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        bool isAndroid2 = Application.platform == RuntimePlatform.Android;
        _sectionLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            fontSize = isAndroid2 ? 18 : 14,
        };
        _sectionLabelStyle.normal.textColor = new Color(0.51f, 0.65f, 0.86f);

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset { top = 5, bottom = 10, left = 10, right = 10 },
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = isAndroid2 ? 20 : 16,
        };
        _titleStyle.normal.textColor = new Color(0.55f, 0.70f, 0.90f);

        _serverBtnStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = isAndroid2 ? 17 : 13,
        };
    }

    // ── Server Section ──────────────────────────────────────────

    void RenderServerSection()
    {
        GUILayout.Label(Get("vc.settings.server", "Server"), _sectionLabelStyle);

        var serverNames = ServerList.GetServerNames();
        int currentIdx = VoiceConfig.SelectedServerIndex;
        bool isBeijing = VoiceConfig.IsBeijingServer;

        GUILayout.BeginVertical(_boxStyle);
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Get("vc.settings.server", "Server") + ":", GUILayout.Width(80f));

            // Dropdown button
            string currentServerName = currentIdx >= 0 && currentIdx < serverNames.Length
                ? serverNames[currentIdx]
                : "Custom...";

            if (GUILayout.Button(currentServerName + " ▼", _serverBtnStyle, GUILayout.MinWidth(200f)))
            {
                _showServerDropdown = !_showServerDropdown;
            }

            // Refresh button
            if (GUILayout.Button("⟳ " + Get("vc.settings.refresh", "Refresh"), GUILayout.Width(90f)))
            {
                VoiceRoom.RestartForCurrentGame();
            }
            GUILayout.EndHorizontal();

            // Server dropdown
            if (_showServerDropdown)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(84f);
                GUILayout.BeginVertical();
                _serverScrollPos = GUILayout.BeginScrollView(_serverScrollPos, GUILayout.Height(120f));
                for (int i = 0; i < serverNames.Length; i++)
                {
                    if (GUILayout.Button((i == currentIdx ? "✓ " : "   ") + serverNames[i],
                        GUILayout.Height(24f)))
                    {
                        VoiceConfig.SelectedServerIndex = i;
                        _showServerDropdown = false;
                        // Auto reconnect on server change
                        VoiceRoom.RestartForCurrentGame();
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            // Custom URL (when custom selected)
            if (currentIdx >= serverNames.Length - 1)
            {
                GUILayout.BeginHorizontal();
                var lblStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = new GUIStyleState { textColor = new Color(0.7f, 0.7f, 0.7f) } };
                GUILayout.Label(Get("vc.settings.url", "URL") + ": " + (string.IsNullOrEmpty(VoiceConfig.CustomServerURL) ? Get("vc.settings.empty", "(empty)") : VoiceConfig.CustomServerURL), lblStyle);
                if (GUILayout.Button(Get("vc.settings.edit", "Edit"), GUILayout.Width(50f)))
                    ShowTextInput(Get("vc.settings.customUrl", "Custom Server URL"), VoiceConfig.CustomServerURL, v => { VoiceConfig.CustomServerURL = v; });
                if (GUILayout.Button(Get("vc.settings.reconnect", "Reconnect"), GUILayout.Width(100f)))
                    VoiceRoom.RestartForCurrentGame();
                GUILayout.EndHorizontal();
            }

            // NAT Fix toggle (disabled for Beijing)
            bool natFixVal = VoiceConfig.NatFixEnabled;
            GUI.enabled = !isBeijing;
            bool newNatFix = GUILayout.Toggle(natFixVal,
                isBeijing ? Get("vc.settings.natFixOff", "NAT Fix (unavailable on Beijing server)") : Get("vc.settings.natFixOn", "NAT Fix (force relay)"));
            if (newNatFix != natFixVal)
                VoiceConfig.NatFixEnabled = newNatFix;
            GUI.enabled = true;

            if (isBeijing)
            {
                var warnStyle = new GUIStyle(GUI.skin.label)
                {
                    normal = new GUIStyleState { textColor = new Color(1f, 0.65f, 0.2f) },
                    fontSize = 11,
                    wordWrap = true
                };
                GUILayout.Label(Get("vc.settings.natFixWarn", "Beijing server does not support NAT Fix."), warnStyle);
            }
        }
        GUILayout.EndVertical();
    }

    // ── Personal Section ────────────────────────────────────────

    void RenderPersonalSection()
    {
        GUILayout.Label(Get("vc.settings.personal", "Personal"), _sectionLabelStyle);

        bool showDevices = VoiceConfig.DeviceSelectionSupported;

        if (showDevices)
        {
            RenderDeviceCycle(Get("vc.settings.microphone", "Microphone"), VoiceConfig.MicrophoneDevice,
                VoiceConfig.MicrophoneDevices, v =>
                {
                    VoiceConfig.SetMicrophoneDevice(v);
                    VoiceRoom.Current?.SetMicrophone(v);
                });

            RenderDeviceCycle(Get("vc.settings.speaker", "Speaker"), VoiceConfig.SpeakerDevice,
                VoiceConfig.SpeakerDevices, v =>
                {
                    VoiceConfig.SetSpeakerDevice(v);
                    VoiceRoom.Current?.SetSpeaker(v);
                });
        }
        else
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label(Get("vc.settings.noDeviceSupport", "Device selection not supported on this platform."),
                new GUIStyle(GUI.skin.label) { normal = new GUIStyleState { textColor = Color.gray }, wordWrap = true });
            GUILayout.EndVertical();
        }

        RenderSlider(Get("vc.settings.micVolume", "Mic Volume"), 0.1f, 2f, VoiceConfig.MicVolume, v =>
        {
            VoiceConfig.SetMicVolume(v);
            VoiceRoom.Current?.SetMicVolume(v);
        });

        RenderSlider(Get("vc.settings.masterVolume", "Master Volume"), 0.1f, 2f, VoiceConfig.MasterVolume, v =>
        {
            VoiceConfig.SetMasterVolume(v);
            VoiceRoom.Current?.SetMasterVolume(v);
        });
    }

    // ── Room Section ────────────────────────────────────────────

    void RenderRoomSection(bool isHost)
    {
        GUILayout.Label(Get("vc.settings.room", "Room Settings"), _sectionLabelStyle);

        void RoomChanged()
        {
            VoiceConfig.ApplyLocalHostSettingsToSynced();
            InterstellarHudState.MarkRoomSettingsDirty();
        }

        GUI.enabled = isHost;
        RenderSlider(Get("vc.settings.maxChatDistance", "Max Chat Distance"), 1.5f, 20f,
            isHost ? VoiceConfig.HostMaxChatDistance : VoiceConfig.SyncedRoomSettings.MaxChatDistance,
            v =>
            {
                VoiceConfig.SetHostMaxChatDistance(v);
                RoomChanged();
            });
        GUI.enabled = true;

        RenderHostToggle(Get("vc.settings.wallsBlockSound", "Walls Block Sound"),
            () => VoiceConfig.SyncedRoomSettings.WallsBlockSound,
            v => { VoiceConfig.SetHostWallsBlockSound(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.impostorHearGhosts", "Impostor Hear Ghosts"),
            () => VoiceConfig.SyncedRoomSettings.ImpostorHearGhosts,
            v => { VoiceConfig.SetHostImpostorHearGhosts(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.onlyGhostsCanTalk", "Only Ghosts Can Talk"),
            () => VoiceConfig.SyncedRoomSettings.OnlyGhostsCanTalk,
            v => { VoiceConfig.SetHostOnlyGhostsCanTalk(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.hearInVent", "Hear Outside In Vent"),
            () => VoiceConfig.SyncedRoomSettings.HearInVent,
            v => { VoiceConfig.SetHostHearInVent(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.hearVentPlayers", "Hear Players In Vent"),
            () => VoiceConfig.SyncedRoomSettings.HearVentPlayers,
            v => { VoiceConfig.SetHostHearVentPlayers(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.ventPrivateChat", "Vent Private Chat"),
            () => VoiceConfig.SyncedRoomSettings.VentPrivateChat,
            v => { VoiceConfig.SetHostVentPrivateChat(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.commsSabotageMutes", "Comms Sabotage Mutes"),
            () => VoiceConfig.SyncedRoomSettings.CommsSabDisables,
            v => { VoiceConfig.SetHostCommsSabDisables(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.cameraCanHear", "Hear Through Cameras"),
            () => VoiceConfig.SyncedRoomSettings.CameraCanHear,
            v => { VoiceConfig.SetHostCameraCanHear(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.impostorPrivateRadio", "Impostor Private Radio"),
            () => VoiceConfig.SyncedRoomSettings.ImpostorPrivateRadio,
            v => { VoiceConfig.SetHostImpostorPrivateRadio(v); RoomChanged(); }, isHost);

        RenderHostToggle(Get("vc.settings.onlyMeetingOrLobby", "Only Meeting / Lobby"),
            () => VoiceConfig.SyncedRoomSettings.OnlyMeetingOrLobby,
            v => { VoiceConfig.SetHostOnlyMeetingOrLobby(v); RoomChanged(); }, isHost);
    }

    void RenderHostToggle(string label, Func<bool> getter, Action<bool> setter, bool enabled)
    {
        GUILayout.BeginVertical(_boxStyle);
        GUI.enabled = enabled;
        bool val = GUILayout.Toggle(getter(), label);
        if (val != getter() && enabled) setter(val);
        GUI.enabled = true;
        GUILayout.EndVertical();
    }

    // ── Public Lobby Section ────────────────────────────────────

    void RenderPublicLobbySection(bool isHost)
    {
        GUILayout.Label(Get("vc.settings.publicLobby", "Public Lobby"), _sectionLabelStyle);

        GUILayout.BeginVertical(_boxStyle);
        {
            GUI.enabled = isHost;
            bool enabled = GUILayout.Toggle(VoiceConfig.PublicLobbyEnabled, Get("vc.settings.publicLobbyEnable", "Enable Public Lobby"));
            if (enabled != VoiceConfig.PublicLobbyEnabled && isHost)
                VoiceConfig.PublicLobbyEnabled = enabled;
            GUI.enabled = true;

            if (VoiceConfig.PublicLobbyEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Get("vc.settings.title", "Title") + ":", GUILayout.Width(60f));
                var lblStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = new GUIStyleState { textColor = new Color(0.7f, 0.7f, 0.7f) } };
                GUILayout.Label(string.IsNullOrEmpty(VoiceConfig.PublicLobbyTitle) ? Get("vc.settings.empty", "(empty)") : VoiceConfig.PublicLobbyTitle, lblStyle);
                GUI.enabled = isHost;
                if (GUILayout.Button(Get("vc.settings.edit", "Edit"), GUILayout.Width(50f)))
                    ShowTextInput(Get("vc.settings.publicLobbyTitle", "Public Lobby Title"), VoiceConfig.PublicLobbyTitle, v => { VoiceConfig.PublicLobbyTitle = v; });
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Get("vc.settings.language", "Language") + ":", GUILayout.Width(60f));
                GUI.enabled = isHost;
                var langs = new[] { "en", "zh_CN", "ja", "ko", "ru", "es", "pt_BR", "Other" };
                int langIdx = Array.IndexOf(langs, VoiceConfig.PublicLobbyLanguage);
                if (langIdx < 0) langIdx = langs.Length - 1;
                if (GUILayout.Button(langs[langIdx] + " ▼", GUILayout.Width(120f)))
                    _showLangDropdown = !_showLangDropdown;

                if (_showLangDropdown)
                {
                    for (int i = 0; i < langs.Length; i++)
                    {
                        if (GUILayout.Button((i == langIdx ? "✓ " : "   ") + langs[i], GUILayout.Width(120f)))
                        {
                            VoiceConfig.PublicLobbyLanguage = langs[i];
                            _showLangDropdown = false;
                        }
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }
        GUILayout.EndVertical();
    }

    // ── Advanced Section ────────────────────────────────────────

    void RenderAdvancedSection()
    {
        GUILayout.Label(Get("vc.settings.advanced", "Advanced"), _sectionLabelStyle);

        GUILayout.BeginVertical(_boxStyle);
        {
            bool ns = GUILayout.Toggle(VoiceConfig.NoiseSuppression, Get("vc.settings.noiseSuppression", "Noise Suppression"));
            if (ns != VoiceConfig.NoiseSuppression) VoiceConfig.NoiseSuppression = ns;

            bool ec = GUILayout.Toggle(VoiceConfig.EchoCancellation, Get("vc.settings.echoCancellation", "Echo Cancellation"));
            if (ec != VoiceConfig.EchoCancellation) VoiceConfig.EchoCancellation = ec;

            bool vad = GUILayout.Toggle(VoiceConfig.VADEnabled, Get("vc.settings.vad", "VAD (Voice Activity Detection)"));
            if (vad != VoiceConfig.VADEnabled) VoiceConfig.VADEnabled = vad;
        }
        GUILayout.EndVertical();
    }

    // ── Widget Helpers ──────────────────────────────────────────

    void RenderDeviceCycle(string label, string currentValue, List<string> options, Action<string> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", GUILayout.Width(130f));

            int idx = Math.Max(0, options.IndexOf(currentValue ?? ""));
            string display = string.IsNullOrEmpty(currentValue) ? Get("vc.settings.defaultDevice", "Default") : Truncate(currentValue, 20);

            if (GUILayout.Button("◄", GUILayout.Width(24f)))
            {
                idx = (idx - 1 + options.Count) % options.Count;
                onChange(options[idx]);
            }
            GUILayout.Label(display, GUILayout.Width(160f));
            if (GUILayout.Button("►", GUILayout.Width(24f)))
            {
                idx = (idx + 1) % options.Count;
                onChange(options[idx]);
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
    }

    void RenderSlider(string label, float min, float max, float value, Action<float> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        GUILayout.Label($"{label}: {value:F1}");
        float newVal = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(300f));
        if (Math.Abs(newVal - value) > 0.01f) onChange(newVal);
        GUILayout.EndVertical();
    }

    static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";

    // ── TextBoxTMP input popup ──────────────────────────────────

    static void ShowTextInput(string title, string currentValue, Action<string> onSave)
    {
        var template = AccountManager.Instance?.transform.Find("PremissionRequestWindow");
        if (template == null) return;

        var old = AccountManager.Instance.transform.Find("VC_TextInput");
        if (old != null) Object.Destroy(old.gameObject);

        var popup = Object.Instantiate(template.gameObject, AccountManager.Instance.transform);
        popup.name = "VC_TextInput";
        popup.SetActive(true);

        popup.transform.Find("TitleText_TMP").GetComponent<TextMeshPro>().text = title;
        Object.Destroy(popup.transform.Find("TitleText_TMP").GetComponent<TextTranslatorTMP>());

        popup.transform.Find("InfoText_TMP").gameObject.SetActive(false);
        popup.transform.Find("GuardianEmailTitle_TMP").gameObject.SetActive(false);
        popup.transform.Find("GuardianEmailConfirmTitle_TMP").gameObject.SetActive(false);
        popup.transform.Find("GuardianEmailConfirm").gameObject.SetActive(false);

        var emailInput = popup.transform.Find("GuardianEmail");
        emailInput.localPosition = new Vector3(0f, 0.3f, 0f);
        emailInput.GetChild(0).GetComponent<SpriteRenderer>().size = new Vector2(6.8f, 0.8f);
        emailInput.GetComponent<BoxCollider2D>().size = new Vector2(6.8f, 0.8f);
        Object.Destroy(emailInput.GetComponent<EmailTextBehaviour>());
        var inputText = emailInput.GetChild(1).GetComponent<TextMeshPro>();
        inputText.text = currentValue ?? "";
        inputText.transform.localPosition = new Vector3(-3.2f, 0f, 0f);
        inputText.rectTransform.sizeDelta = new Vector2(6.4f, 0);
        var textBox = emailInput.GetComponent<TextBoxTMP>();
        textBox.characterLimit = -1;

        if (popup.transform.childCount > 9)
            popup.transform.GetChild(9).gameObject.SetActive(false);

        var submitBtn = popup.transform.Find("SubmitButton").GetComponent<PassiveButton>();
        submitBtn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        var capturedInput = inputText;
        var capturedPopup = popup;
        var capturedOnSave = onSave;
        submitBtn.OnClick.AddListener((Action)(() =>
        {
            capturedOnSave(capturedInput.text);
            Object.Destroy(capturedPopup);
        }));
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Auto-close on scene changes
    }
}
