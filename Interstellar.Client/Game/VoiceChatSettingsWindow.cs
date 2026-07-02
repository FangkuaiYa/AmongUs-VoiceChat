using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using static VoiceChatPlugin.VoiceChat.TranslationHelper;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatSettingsWindow : MonoBehaviour
{
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

    public void Close()
    {
        ShowWindow = false;
    }

    private Vector2 _scrollPosition;
    private bool _needsDeviceRefresh = true;

    // F1 toggles the settings window
    private const KeyCode ToggleKey = KeyCode.F1;

    private GUIStyle? _sectionLabelStyle;
    private GUIStyle? _boxStyle;
    private GUIStyle? _titleStyle;
    private bool _stylesBuilt;

    void Awake()
    {
        SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= (Action<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey))
            Toggle();

        // Android hardware/gesture back button is reported by Unity as Escape.
        // Without this, mobile players who get stuck (e.g. window drawn partly
        // off-screen) have no way to dismiss the window at all.
        if (ShowWindow && Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    void OnGUI()
    {
        if (!ShowWindow) return;

        if (_needsDeviceRefresh)
        {
            VoiceChatConfig.RefreshDeviceCaches(true);
            _needsDeviceRefresh = false;
        }

        BuildStyles();

        bool isAndroid = Application.platform == RuntimePlatform.Android;

        // Desired size, then clamped to the real screen size (minus a small margin)
        // so the window — and critically its close button — can never end up
        // drawn partly (or fully) off-screen on smaller/oddly-scaled mobile screens.
        const float margin = 24f;
        float desiredW = isAndroid ? 640f : 440f;
        float desiredH = isAndroid ? 800f : 540f;
        float winW = Mathf.Min(desiredW, Screen.width - margin);
        float winH = Mathf.Min(desiredH, Screen.height - margin);
        float x = (Screen.width - winW) / 2f;
        float y = (Screen.height - winH) / 2f;

        var windowRect = new Rect(x, y, winW, winH);

        // Safety net: tapping/clicking anywhere outside the window closes it.
        // This guarantees the window is always dismissable even if the "X"
        // button itself were ever unreachable (e.g. covered by other UI).
        // Only acts on genuine clicks outside the window rect so it never
        // steals input from the sliders/buttons/scrollview inside it.
        if (Event.current.type == EventType.MouseDown && !windowRect.Contains(Event.current.mousePosition))
            Close();

        var oldColor = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.95f);
        GUI.Box(new Rect(x - 4, y - 4, winW + 8, winH + 8), "");
        GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.98f);
        GUI.Box(new Rect(x, y, winW, winH), "");
        GUI.color = oldColor;

        GUILayout.BeginArea(new Rect(x + 10, y + 8, winW - 20, winH - 16));
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{Get("vc.settings.title", "Voice Chat")}</b>", _titleStyle);
            GUILayout.FlexibleSpace();
            if (!isAndroid)
            {
                GUILayout.Label("F1", new GUIStyle(GUI.skin.label) { fontSize = 10, normal = new GUIStyleState { textColor = Color.gray } });
                GUILayout.Space(4f);
            }
            float closeBtnSize = isAndroid ? 44f : 20f;
            if (GUILayout.Button("X", GUILayout.Width(closeBtnSize + 8f), GUILayout.Height(closeBtnSize)))
                Close();
            GUILayout.EndHorizontal();

            GUILayout.Space(5f);

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(winH - 50f));
            {
                // Host can only edit room settings before the game starts (in lobby)
                bool isHost = (AmongUsClient.Instance?.AmHost ?? false)
                    && AmongUsClient.Instance?.GameState == InnerNet.InnerNetClient.GameStates.Joined;

                RenderPersonalSection();
                GUILayout.Space(20f);
                RenderRoomSection(isHost);
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

        _sectionLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
        };
        _sectionLabelStyle.normal.textColor = new Color(0.51f, 0.65f, 0.86f);

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset { top = 5, bottom = 10, left = 10, right = 10 },
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 16,
        };
        _titleStyle.normal.textColor = new Color(0.55f, 0.70f, 0.90f);
    }

    void RenderPersonalSection()
    {
        GUILayout.Label(Get("vc.settings.personal", "Personal"), _sectionLabelStyle);

        bool showDevices = VoiceChatConfig.DeviceSelectionSupported;

        if (showDevices)
        {
            RenderDeviceCycle(
                Get("vc.settings.microphone", "Microphone"),
                VoiceChatConfig.MicrophoneDevice,
                VoiceChatConfig.MicrophoneDevices,
                v =>
                {
                    VoiceChatConfig.SetMicrophoneDevice(v);
                    VoiceChatRoom.Current?.SetMicrophone(v);
                });

            RenderDeviceCycle(
                Get("vc.settings.speaker", "Speaker"),
                VoiceChatConfig.SpeakerDevice,
                VoiceChatConfig.SpeakerDevices,
                v =>
                {
                    VoiceChatConfig.SetSpeakerDevice(v);
                    VoiceChatRoom.Current?.SetSpeaker(v);
                });
        }
        else
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label(Get("vc.settings.noDeviceSupport", "Device selection not supported on this platform."),
                new GUIStyle(GUI.skin.label) { normal = new GUIStyleState { textColor = Color.gray }, wordWrap = true });
            GUILayout.EndVertical();
        }

        RenderSlider(Get("vc.settings.micVolume", "Mic Volume"), 0.1f, 2f, VoiceChatConfig.MicVolume, v =>
        {
            VoiceChatConfig.SetMicVolume(v);
            VoiceChatRoom.Current?.SetMicVolume(v);
        });

        RenderSlider(Get("vc.settings.masterVolume", "Master Volume"), 0.1f, 2f, VoiceChatConfig.MasterVolume, v =>
        {
            VoiceChatConfig.SetMasterVolume(v);
            VoiceChatRoom.Current?.SetMasterVolume(v);
        });
    }

    private static readonly (string label, Func<bool> getter, Action<bool> setter)[] RoomBoolSettings = new (string, Func<bool>, Action<bool>)[]
    {
        (Get("vc.settings.wallsBlockSound", "Walls Block Sound"),          () => VoiceChatConfig.SyncedRoomSettings.WallsBlockSound,       v => VoiceChatConfig.SetHostWallsBlockSound(v)),
        (Get("vc.settings.impostorHearGhosts", "Impostor Hear Ghosts"),   () => VoiceChatConfig.SyncedRoomSettings.ImpostorHearGhosts,    v => VoiceChatConfig.SetHostImpostorHearGhosts(v)),
        (Get("vc.settings.onlyGhostsCanTalk", "Only Ghosts Can Talk"),    () => VoiceChatConfig.SyncedRoomSettings.OnlyGhostsCanTalk,     v => VoiceChatConfig.SetHostOnlyGhostsCanTalk(v)),
        (Get("vc.settings.hearInVent", "Hear Outside While In Vent"),      () => VoiceChatConfig.SyncedRoomSettings.HearInVent,            v => VoiceChatConfig.SetHostHearInVent(v)),
        (Get("vc.settings.hearVentPlayers", "Hear Players In Vent"),     () => VoiceChatConfig.SyncedRoomSettings.HearVentPlayers,       v => VoiceChatConfig.SetHostHearVentPlayers(v)),
        (Get("vc.settings.ventPrivateChat", "Vent Private Chat"),         () => VoiceChatConfig.SyncedRoomSettings.VentPrivateChat,       v => VoiceChatConfig.SetHostVentPrivateChat(v)),
        (Get("vc.settings.commsSabotageMutes", "Comms Sabotage Mutes"),   () => VoiceChatConfig.SyncedRoomSettings.CommsSabDisables,      v => VoiceChatConfig.SetHostCommsSabDisables(v)),
        (Get("vc.settings.cameraCanHear", "Camera Can Hear"),             () => VoiceChatConfig.SyncedRoomSettings.CameraCanHear,         v => VoiceChatConfig.SetHostCameraCanHear(v)),
        (Get("vc.settings.impostorPrivateRadio", "Impostor Private Radio"),() => VoiceChatConfig.SyncedRoomSettings.ImpostorPrivateRadio,  v => VoiceChatConfig.SetHostImpostorPrivateRadio(v)),
        (Get("vc.settings.onlyMeetingOrLobby", "Only Meeting / Lobby"),   () => VoiceChatConfig.SyncedRoomSettings.OnlyMeetingOrLobby,    v => VoiceChatConfig.SetHostOnlyMeetingOrLobby(v)),
    };

    void RenderRoomSection(bool isHost)
    {
        GUILayout.Label(Get("vc.settings.room", "Room"), _sectionLabelStyle);

        void RoomChanged()
        {
            VoiceChatConfig.ApplyLocalHostSettingsToSynced();
            InterstellarHudState.MarkRoomSettingsDirty();
        }

        {
            RenderSlider(Get("vc.settings.maxChatDistance", "Max Chat Distance"), 1.5f, 20f,
                isHost ? VoiceChatConfig.HostMaxChatDistance
                       : VoiceChatConfig.SyncedRoomSettings.MaxChatDistance,
                v =>
                {
                    VoiceChatConfig.SetHostMaxChatDistance(v);
                    RoomChanged();
                });
            GUI.enabled = true;
        }

        foreach (var bs in RoomBoolSettings)
        {
            GUI.enabled = isHost;
            RenderToggle(bs.label, bs.getter(), v =>
            {
                if (isHost)
                {
                    bs.setter(v);
                    RoomChanged();
                }
            });
            GUI.enabled = true;
        }
    }

    void RenderDeviceCycle(string label, string currentValue, List<string> options, Action<string> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", GUILayout.Width(130f));

            int idx = Math.Max(0, options.IndexOf(currentValue ?? ""));
            string display = string.IsNullOrEmpty(currentValue)
                ? Get("vc.settings.defaultDevice", "Default")
                : Truncate(currentValue, 20);

            if (GUILayout.Button("◄", GUILayout.Width(24f)))
            {
                if (Event.current.type == EventType.Used)
                {
                    idx = (idx - 1 + options.Count) % options.Count;
                    onChange(options[idx]);
                }
            }
            GUILayout.Label(display, GUILayout.Width(160f));
            if (GUILayout.Button("►", GUILayout.Width(24f)))
            {
                if (Event.current.type == EventType.Used)
                {
                    idx = (idx + 1) % options.Count;
                    onChange(options[idx]);
                }
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
    }

    void RenderSlider(string label, float min, float max, float value, Action<float> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", GUILayout.Width(135f));

            float newVal = GUILayout.HorizontalSlider(value, min, max,
                GUILayout.Width(185f), GUILayout.Height(18f));

            string display = value.ToString("F1");
            GUILayout.Label(display, GUILayout.Width(32f));

            GUILayout.EndHorizontal();

            if (Math.Abs(newVal - value) > 0.001f)
            {
                float clamped = Mathf.Clamp((float)Math.Round(newVal, 2), min, max);
                onChange(clamped);
            }
        }
        GUILayout.EndVertical();
    }

    void RenderToggle(string label, bool value, Action<bool> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            bool newVal = GUILayout.Toggle(value, " " + label);
            if (newVal != value)
                onChange(newVal);
        }
        GUILayout.EndVertical();
    }

    static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    void OnSceneLoaded(Scene _, LoadSceneMode __)
    {
        ShowWindow = false;
    }
}
