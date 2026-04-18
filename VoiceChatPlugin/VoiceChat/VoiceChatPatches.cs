using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public enum VoiceChannel
{
    All,
    Impostor,
}

[HarmonyPatch]
public static class VoiceChatPatches
{
    // ── HUD button objects ────────────────────────────────────────────────────
    private static PassiveButton?  _micButton;
    private static GameObject?     _micButtonObj;
    private static PassiveButton?  _spkButton;
    private static GameObject?     _spkButtonObj;
    private static AspectPosition? _micAspect;
    private static AspectPosition? _spkAspect;

    private static readonly AspectPosition.EdgeAlignments ButtonAnchor
        = AspectPosition.EdgeAlignments.RightTop;
    private static readonly Vector3 MicEdge = new(3.85f, 0.55f, 0f);
    private static readonly Vector3 SpkEdge = new(4.50f, 0.55f, 0f);

    // ── Tooltip ───────────────────────────────────────────────────────────────
    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;

    // ── State ─────────────────────────────────────────────────────────────────
    private static bool         _micMuted;
    private static bool         _speakerMuted;
    private static VoiceChannel _channel = VoiceChannel.All;

    public static bool IsSpeakerMuted      => _speakerMuted;
    public static bool IsImpostorRadioOnly => _channel == VoiceChannel.Impostor;

    private static VoiceChatRoomSettings? _lastSentSettings;
    public static void MarkRoomSettingsDirty() => _lastSentSettings = null;

    // ── HudManager.Start ──────────────────────────────────────────────────────
    [HarmonyPostfix, HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    static void HudStart_Post(HudManager __instance)
    {
        DestroyButtons();
        DestroyTooltips();
    }

    // ── HudManager.Update ─────────────────────────────────────────────────────
    // Nebula パターン: 毎フレーム接続状態を確認し、必要ならルームを自動起動/停止する。
    // OnGameJoined などのイベントには依存しない。
    // Nebula pattern: check connection state every frame, auto-start/stop the room.
    // Does NOT rely on OnGameJoined or any other one-shot lifecycle event.
    [HarmonyPostfix, HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    static void HudUpdate_Post(HudManager __instance)
    {
        EnsureHudButtons(__instance);
        EnsureTooltips(__instance);
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();

        // --- Nebula-style room lifecycle ---
        bool shouldUseVC = AmongUsClient.Instance != null
            && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Joined
            && AmongUsClient.Instance.networkAddress is not ("127.0.0.1" or "localhost");

        if (!shouldUseVC)
        {
            // Not in an online game – tear down the room if one exists.
            if (VoiceChatRoom.Current != null)
                VoiceChatRoom.CloseCurrentRoom();
            return;
        }

        // In an online game: start the room if it doesn't exist yet.
        // Crucially we do NOT wait for PlayerControl.LocalPlayer – Nebula never does.
        if (VoiceChatRoom.Current == null)
        {
            VoiceChatRoom.Start();
            ApplyMicState();
            if (_speakerMuted)
                VoiceChatRoom.Current?.SetMasterVolume(0f);
            if (AmongUsClient.Instance!.AmHost)
            {
                VoiceChatConfig.ApplyLocalHostSettingsToSynced();
                _lastSentSettings = null;
            }
        }

        if (VoiceChatRoom.Current == null) return;

        if (_speakerMuted)
            VoiceChatRoom.Current.SetMasterVolume(0f);

        TrySyncHostRoomSettings();

        try { VoiceChatRoom.Current.Update(); }
        catch (Exception ex)
        { VoiceChatPluginMain.Logger.LogError("[VC] Update error: " + ex); }
    }

    // ── MeetingHud: re-parent buttons ────────────────────────────────────────
    [HarmonyPostfix, HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    static void MeetingHud_Start_Post(MeetingHud __instance)
    {
        if (_micButtonObj != null)
        {
            _micButtonObj.transform.SetParent(__instance.transform, false);
            _micButtonObj.SetActive(true);
        }
        if (_spkButtonObj != null)
        {
            _spkButtonObj.transform.SetParent(__instance.transform, false);
            _spkButtonObj.SetActive(true);
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
    static void MeetingHud_Destroy_Post()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;
        if (_micButtonObj != null)
            _micButtonObj.transform.SetParent(hud.transform.parent, false);
        if (_spkButtonObj != null)
            _spkButtonObj.transform.SetParent(hud.transform.parent, false);
        ResetAspects();
    }

    private static void ResetAspects()
    {
        if (_micAspect != null)
        {
            _micAspect.enabled          = true;
            _micAspect.Alignment        = ButtonAnchor;
            _micAspect.DistanceFromEdge = MicEdge;
            _micAspect.AdjustPosition();
        }
        if (_spkAspect != null)
        {
            _spkAspect.enabled          = true;
            _spkAspect.Alignment        = ButtonAnchor;
            _spkAspect.DistanceFromEdge = SpkEdge;
            _spkAspect.AdjustPosition();
        }
    }

    // ── Game lifecycle ────────────────────────────────────────────────────────
    // Room start/stop is now handled entirely by HudUpdate_Post (Nebula pattern).
    // ExitGame still needs explicit cleanup to reset state cleanly.
    [HarmonyPostfix, HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
    static void ExitGame_Post()
    {
        _lastSentSettings = null;
        VoiceChatRoom.CloseCurrentRoom();
    }

    [HarmonyPostfix, HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    static void IntroCutscene_Begin_Post()
    {
        if (VoiceChatRoom.Current == null) return;
        foreach (var client in VoiceChatRoom.Current.AllClients)
            client.ResetMapping();
        VoiceChatPluginMain.Logger.LogInfo("[VC] IntroCutscene: all client mappings reset.");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
    static void EndGameManager_Post()
    {
        if (VoiceChatRoom.Current == null) return;
        VoiceChatRoom.Current.Rejoin();
        VoiceChatPluginMain.Logger.LogInfo("[VC] Game ended: VC room rejoined.");
    }

    // ── Keyboard shortcuts (Windows only) ────────────────────────────────────
#if WINDOWS
    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        if (Input.GetKeyDown(KeyCode.M)) CycleMic();
        if (Input.GetKeyDown(KeyCode.N)) ToggleSpeaker();
    }
#endif

    // ── Button creation ───────────────────────────────────────────────────────
    private static void DestroyButtons()
    {
        if (_micButtonObj != null) { Object.Destroy(_micButtonObj); _micButtonObj = null; }
        if (_spkButtonObj != null) { Object.Destroy(_spkButtonObj); _spkButtonObj = null; }
        _micButton  = null;
        _spkButton  = null;
        _micAspect  = null;
        _spkAspect  = null;
    }

    private static void DestroyTooltips()
    {
        if (_micTooltip != null) { Object.Destroy(_micTooltip); _micTooltip = null; }
        if (_spkTooltip != null) { Object.Destroy(_spkTooltip); _spkTooltip = null; }
        _micTooltipTmp = null;
        _spkTooltipTmp = null;
    }

    private static void EnsureTooltips(HudManager hud)
    {
        if (_micTooltip == null)
            _micTooltip = CreateTooltipObject(hud.transform.parent, out _micTooltipTmp);
        if (_spkTooltip == null)
            _spkTooltip = CreateTooltipObject(hud.transform.parent, out _spkTooltipTmp);
    }

    private static void EnsureHudButtons(HudManager hud)
    {
        if (hud.MapButton == null) return;

        if (_micButtonObj == null)
        {
            _micButtonObj      = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
            _micButtonObj.name = "VC_MicButton";
            ClearButtonBG(_micButtonObj);
            CreateIconChild(_micButtonObj, "VoiceChatPlugin.Resources.MicOn.png");

            _micButton = _micButtonObj.GetComponent<PassiveButton>();
            _micButton.OnClick = new ButtonClickedEvent();
            _micButton.OnClick.AddListener((Action)CycleMic);

            _micButton.OnMouseOver = new UnityEvent();
            _micButton.OnMouseOver.AddListener((Action)ShowMicTooltip);
            _micButton.OnMouseOut = new UnityEvent();
            _micButton.OnMouseOut.AddListener((Action)HideTooltips);

            _micAspect = _micButtonObj.GetComponent<AspectPosition>()
                ?? _micButtonObj.AddComponent<AspectPosition>();
            _micAspect.Alignment        = ButtonAnchor;
            _micAspect.DistanceFromEdge = MicEdge;
        }

        if (_spkButtonObj == null)
        {
            _spkButtonObj      = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
            _spkButtonObj.name = "VC_SpkButton";
            ClearButtonBG(_spkButtonObj);
            CreateIconChild(_spkButtonObj, "VoiceChatPlugin.Resources.SpeakerOn.png");

            _spkButton = _spkButtonObj.GetComponent<PassiveButton>();
            _spkButton.OnClick = new ButtonClickedEvent();
            _spkButton.OnClick.AddListener((Action)ToggleSpeaker);

            _spkButton.OnMouseOver = new UnityEvent();
            _spkButton.OnMouseOver.AddListener((Action)ShowSpeakerTooltip);
            _spkButton.OnMouseOut = new UnityEvent();
            _spkButton.OnMouseOut.AddListener((Action)HideTooltips);

            _spkAspect = _spkButtonObj.GetComponent<AspectPosition>()
                ?? _spkButtonObj.AddComponent<AspectPosition>();
            _spkAspect.Alignment        = ButtonAnchor;
            _spkAspect.DistanceFromEdge = SpkEdge;
        }
    }

    private static void UpdateHudButtonsVisibility()
    {
        if (_micButtonObj == null || _spkButtonObj == null) return;

        bool inMeeting = MeetingHud.Instance != null;
        if (!inMeeting)
        {
            bool mapOpen = MapBehaviour.Instance && MapBehaviour.Instance.IsOpen;
            _micButtonObj.SetActive(!mapOpen);
            _spkButtonObj.SetActive(!mapOpen);

            if (_micAspect != null)
            {
                _micAspect.Alignment        = ButtonAnchor;
                _micAspect.DistanceFromEdge = MicEdge;
                _micAspect.AdjustPosition();
            }
            if (_spkAspect != null)
            {
                _spkAspect.Alignment        = ButtonAnchor;
                _spkAspect.DistanceFromEdge = SpkEdge;
                _spkAspect.AdjustPosition();
            }
        }
    }

    // ── Sprite helpers ────────────────────────────────────────────────────────
    private static void ClearButtonBG(GameObject obj)
    {
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>())
            sr.color = new Color(0f, 0f, 0f, 0f);
    }

    private static void CreateIconChild(GameObject parent, string resource)
    {
        var go = new GameObject("VCIcon");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = parent.layer;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite       = LoadSpriteFromResources(resource, 900f);
        sr.sortingOrder = 5;
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────
    private static GameObject CreateTooltipObject(Transform hudRoot, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_Tooltip");
        go.transform.SetParent(hudRoot, false);
        go.transform.localPosition = new Vector3(0f, 0f, -5f);

        var bg   = new GameObject("BG");
        bg.transform.SetParent(go.transform, false);
        var bgSr = bg.AddComponent<SpriteRenderer>();
        bgSr.sprite       = CreateSolidSprite(new Color(0f, 0f, 0f, 0.82f));
        bgSr.sortingOrder = 20;
        bg.transform.localScale = new Vector3(2.6f, 1.6f, 1f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize           = 1.5f;
        tmp.color              = Color.white;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingOrder       = 21;
        tmp.rectTransform.sizeDelta = new Vector2(2.4f, 1.4f);

        go.SetActive(false);
        return go;
    }

    private static Sprite CreateSolidSprite(Color c)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, c);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }

    private static void PositionTooltipNearButton(GameObject tooltip, GameObject button)
    {
        if (tooltip == null || button == null) return;
        var btnWorld = button.transform.position;
        tooltip.transform.position = new Vector3(
            btnWorld.x - 0.2f,
            btnWorld.y - 0.8f,
            btnWorld.z - 1f);
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;

        string channelName = _channel == VoiceChannel.Impostor
            ? VoiceChatLocalization.Tr("channelImpostor")
            : VoiceChatLocalization.Tr("channelAll");

        string status = _micMuted
            ? VoiceChatLocalization.Tr("micStatusMuted")
            : (_channel == VoiceChannel.Impostor
                ? VoiceChatLocalization.Tr("micStatusImpostor")
                : VoiceChatLocalization.Tr("micStatusOn"));

        int volPct = (int)(VoiceChatConfig.MicVolume * 100f);

        _micTooltipTmp.text =
            "<b>" + VoiceChatLocalization.Tr("tooltipMicTitle") + "</b>\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicStatus"),  status)      + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicChannel"), channelName) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicVolume"),  volPct)      + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicHotkey"),  "M");

        PositionTooltipNearButton(_micTooltip, _micButtonObj);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;

        string status = _speakerMuted
            ? VoiceChatLocalization.Tr("speakerStatusOff")
            : VoiceChatLocalization.Tr("speakerStatusOn");

        int volPct = (int)(VoiceChatConfig.MasterVolume * 100f);

        _spkTooltipTmp.text =
            "<b>" + VoiceChatLocalization.Tr("tooltipSpeakerTitle") + "</b>\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipSpeakerStatus"), status) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipSpeakerVolume"), volPct) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipSpeakerHotkey"), "N");

        PositionTooltipNearButton(_spkTooltip, _spkButtonObj);
        _spkTooltip.SetActive(true);
    }

    private static void HideTooltips()
    {
        _micTooltip?.SetActive(false);
        _spkTooltip?.SetActive(false);
    }

    // ── Mic state machine ─────────────────────────────────────────────────────
    private static void CycleMic()
    {
        bool canImpMode = PlayerControl.LocalPlayer != null
            && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true
            && !PlayerControl.LocalPlayer.Data.IsDead;

        if (!_micMuted && _channel == VoiceChannel.All)
        {
            if (canImpMode) _channel  = VoiceChannel.Impostor;
            else            _micMuted = true;
        }
        else if (_channel == VoiceChannel.Impostor)
        {
            _channel  = VoiceChannel.All;
            _micMuted = true;
        }
        else
        {
            _micMuted = false;
            _channel  = VoiceChannel.All;
        }

        ApplyMicState();
        VoiceChatPluginMain.Logger.LogInfo(
            "[VC] Mic: muted=" + _micMuted + " channel=" + _channel);
        RefreshButtonVisuals();
    }

    private static void ApplyMicState()
    {
        if (VoiceChatRoom.Current == null) return;
        VoiceChatRoom.Current.SetMute(_micMuted);
    }

    private static void ToggleSpeaker()
    {
        _speakerMuted = !_speakerMuted;
        VoiceChatRoom.Current?.SetMasterVolume(
            _speakerMuted ? 0f : VoiceChatConfig.MasterVolume);
        VoiceChatPluginMain.Logger.LogInfo(
            "[VC] Speaker: " + (_speakerMuted ? "OFF" : "ON"));
        RefreshButtonVisuals();
    }

    // ── Button visuals ────────────────────────────────────────────────────────
    private static void RefreshButtonVisuals()
    {
        if (_micButtonObj != null)
        {
            var sr = _micButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                if (_micMuted)
                {
                    sr.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOff.png", 900f);
                    sr.color  = new Color(1f, 0.4f, 0.4f, 1f);
                }
                else if (_channel == VoiceChannel.Impostor)
                {
                    sr.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOn.png", 900f);
                    sr.color  = new Color(1f, 0.35f, 0.35f, 1f);
                }
                else
                {
                    sr.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOn.png", 900f);
                    sr.color  = Color.white;
                }
            }
        }

        if (_spkButtonObj != null)
        {
            var sr = _spkButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sprite = LoadSpriteFromResources(
                    _speakerMuted
                        ? "VoiceChatPlugin.Resources.SpeakerOff.png"
                        : "VoiceChatPlugin.Resources.SpeakerOn.png",
                    900f);
                sr.color = _speakerMuted ? new Color(1f, 0.4f, 0.4f, 1f) : Color.white;
            }
        }
    }

    // ── Host settings sync ────────────────────────────────────────────────────
    private static void TrySyncHostRoomSettings()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (AmongUsClient.Instance.GameState
            != InnerNet.InnerNetClient.GameStates.Joined) return;

        var cur = VoiceChatConfig.SyncedRoomSettings;
        if (_lastSentSettings != null && cur.ContentEquals(_lastSentSettings)) return;

        VoiceChatRoomSettings.SendToAll(cur);
        _lastSentSettings = new VoiceChatRoomSettings();
        _lastSentSettings.Apply(cur);
        VoiceChatPluginMain.Logger.LogInfo("[VC] Room settings synced.");
    }

    // ── Sprite / texture loading ──────────────────────────────────────────────
    public static readonly Dictionary<string, Sprite> CachedSprites = new();

    public static Sprite LoadSpriteFromResources(
        string path, float pixelsPerUnit, bool cache = true)
    {
        try
        {
            string key = path + pixelsPerUnit;
            if (cache && CachedSprites.TryGetValue(key, out var cached)) return cached;

            Texture2D tex = LoadTextureFromResources(path);
            var sprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);

            if (cache)
            {
                sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                CachedSprites[key] = sprite;
            }
            return sprite;
        }
        catch
        {
            VoiceChatPluginMain.Logger.LogError("[VC] Sprite load failed: " + path);
        }
        return null!;
    }

    public static Texture2D LoadTextureFromResources(string path)
    {
        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp };
            var stream = Assembly.GetCallingAssembly()
                .GetManifestResourceStream(path)!;
            tex.LoadImage(stream.ReadFully(), false);
            return tex;
        }
        catch
        {
            VoiceChatPluginMain.Logger.LogError("[VC] Texture load failed: " + path);
        }
        return null!;
    }

    public static byte[] ReadFully(this Stream input)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
