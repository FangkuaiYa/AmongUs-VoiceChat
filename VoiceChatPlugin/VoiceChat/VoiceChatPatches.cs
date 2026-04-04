using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatPatches
{
    // ── HUD buttons (microphone / speaker) ────────────────────────────────────
    private static PassiveButton?  _micButton;
    private static GameObject?     _micButtonObj;
    private static PassiveButton?  _spkButton;
    private static GameObject?     _spkButtonObj;
    private static AspectPosition? _micAspect;
    private static AspectPosition? _spkAspect;

    private static readonly AspectPosition.EdgeAlignments ButtonAnchor = AspectPosition.EdgeAlignments.RightTop;
    private static readonly Vector3 MicEdge = new(3.85f, 0.55f, 0f);
    private static readonly Vector3 SpkEdge = new(4.50f, 0.55f, 0f);

    // ── Button state ──────────────────────────────────────────────────────────
    private static bool _micMuted;
    private static bool _impostorMode;
    private static bool _speakerMuted;

    public static bool IsSpeakerMuted      => _speakerMuted;
    public static bool IsImpostorRadioOnly => _impostorMode;

    // Dirty flag – triggers re-sync of host room settings
    private static VoiceChatRoomSettings? _lastSentSettings;
    public static void MarkRoomSettingsDirty() => _lastSentSettings = null;

    // ── HudManager.Update ─────────────────────────────────────────────────────
    [HarmonyPostfix, HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    static void HudUpdate_Post(HudManager __instance)
    {
        EnsureHudButtons(__instance);
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();

        if (VoiceChatRoom.Current == null) return;

        if (_speakerMuted) VoiceChatRoom.Current.SetMasterVolume(0f);

        TrySyncHostRoomSettings();

        try { VoiceChatRoom.Current.Update(); }
        catch (Exception ex) { VoiceChatPluginMain.Logger.LogError("[VC] Update error: " + ex); }
    }

    // ── MeetingHud: move VC buttons into meeting UI ───────────────────────────
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
        RepositionButtonsForMeeting(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
    static void MeetingHud_Destroy_Post()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;
        if (_micButtonObj != null) _micButtonObj.transform.SetParent(hud.transform.parent, false);
        if (_spkButtonObj != null) _spkButtonObj.transform.SetParent(hud.transform.parent, false);
        ResetAspects();
    }

    private static void RepositionButtonsForMeeting(MeetingHud hud)
    {
        if (_micButtonObj != null)
        {
            _micButtonObj.transform.localPosition = new Vector3(3.5f, 2.2f, -10f);
            if (_micAspect != null) _micAspect.enabled = false;
        }
        if (_spkButtonObj != null)
        {
            _spkButtonObj.transform.localPosition = new Vector3(4.1f, 2.2f, -10f);
            if (_spkAspect != null) _spkAspect.enabled = false;
        }
    }

    private static void ResetAspects()
    {
        if (_micAspect != null) { _micAspect.enabled = true; _micAspect.Alignment = ButtonAnchor; _micAspect.DistanceFromEdge = MicEdge; }
        if (_spkAspect != null) { _spkAspect.enabled = true; _spkAspect.Alignment = ButtonAnchor; _spkAspect.DistanceFromEdge = SpkEdge; }
        _micAspect?.AdjustPosition();
        _spkAspect?.AdjustPosition();
    }

    // ── Game joined / exited ──────────────────────────────────────────────────
    [HarmonyPostfix, HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
    static void OnGameJoined_Post(AmongUsClient __instance)
    {
        if (AmongUsClient.Instance == null) return;
        if (__instance.networkAddress is "127.0.0.1" or "localhost") return;

        VoiceChatRoom.Start();
        ApplyMicState();

        if (__instance.AmHost)
        {
            VoiceChatConfig.ApplyLocalHostSettingsToSynced();
            _lastSentSettings = null;
            TrySyncHostRoomSettings();
        }

        if (_speakerMuted) VoiceChatRoom.Current?.SetMasterVolume(0f);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
    static void ExitGame_Post()
    {
        _lastSentSettings = null;
        VoiceChatRoom.CloseCurrentRoom();
    }

    // Reset all mappings when a new round begins
    [HarmonyPostfix, HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    static void IntroCutscene_Begin_Post()
    {
        if (VoiceChatRoom.Current == null) return;
        foreach (var client in VoiceChatRoom.Current.AllClients)
            client.ResetMapping();
        VoiceChatPluginMain.Logger.LogInfo("[VC] IntroCutscene: all client mappings reset.");
    }

    // Rejoin after end-game screen so the room is clean for the next round
    [HarmonyPostfix, HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
    static void EndGameManager_Post()
    {
        if (VoiceChatRoom.Current == null) return;
        VoiceChatRoom.Current.Rejoin();
        VoiceChatPluginMain.Logger.LogInfo("[VC] Game ended: VC room rejoined.");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        if (Input.GetKeyDown(KeyCode.M)) ToggleMic();
    }

    // ── HUD button creation ───────────────────────────────────────────────────
    private static void EnsureHudButtons(HudManager hud)
    {
        if (hud.MapButton == null) return;

        if (_micButtonObj == null)
        {
            _micButtonObj = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
            _micButtonObj.name = "VC_MicButton";
            ClearButtonBG(_micButtonObj);
            CreateIconChild(_micButtonObj, "VoiceChatPlugin.Resources.MicOn.png");

            _micButton = _micButtonObj.GetComponent<PassiveButton>();
            _micButton.OnClick = new ButtonClickedEvent();
            _micButton.OnClick.AddListener((Action)ToggleMic);

            _micAspect = _micButtonObj.GetComponent<AspectPosition>()
                ?? _micButtonObj.AddComponent<AspectPosition>();
            _micAspect.Alignment        = ButtonAnchor;
            _micAspect.DistanceFromEdge = MicEdge;
        }

        if (_spkButtonObj == null)
        {
            _spkButtonObj = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
            _spkButtonObj.name = "VC_SpkButton";
            ClearButtonBG(_spkButtonObj);
            CreateIconChild(_spkButtonObj, "VoiceChatPlugin.Resources.SpeakerOn.png");

            _spkButton = _spkButtonObj.GetComponent<PassiveButton>();
            _spkButton.OnClick = new ButtonClickedEvent();
            _spkButton.OnClick.AddListener((Action)ToggleSpeaker);

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

            if (_micAspect != null) { _micAspect.Alignment = ButtonAnchor; _micAspect.DistanceFromEdge = MicEdge; }
            if (_spkAspect != null) { _spkAspect.Alignment = ButtonAnchor; _spkAspect.DistanceFromEdge = SpkEdge; }
            _micAspect?.AdjustPosition();
            _spkAspect?.AdjustPosition();
        }
    }

    private static void ClearButtonBG(GameObject obj)
    {
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>())
            sr.color = new Color(0f, 0f, 0f, 0f);
    }

    private static SpriteRenderer CreateIconChild(GameObject parent, string resource)
    {
        var go = new GameObject("VCIcon");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite       = LoadSpriteFromResources(resource, 900f);
        sr.sortingOrder = 5;
        return sr;
    }

    // ── Mic state ─────────────────────────────────────────────────────────────
    private static void ToggleMic()
    {
        bool canImpMode = PlayerControl.LocalPlayer != null
            && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true
            && !PlayerControl.LocalPlayer.Data.IsDead;

        if (!_micMuted && !_impostorMode)
        {
            if (canImpMode) _impostorMode = true;
            else            _micMuted     = true;
        }
        else if (_impostorMode)
        {
            _impostorMode = false;
            _micMuted     = true;
        }
        else
        {
            _micMuted = false;
        }

        ApplyMicState();
        string state = _micMuted ? "MUTED" : (_impostorMode ? "IMP" : "ON");
        VoiceChatPluginMain.Logger.LogInfo($"[VC] Mic: {state}");
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
        VoiceChatRoom.Current?.SetMasterVolume(_speakerMuted ? 0f : VoiceChatConfig.MasterVolume);
        VoiceChatPluginMain.Logger.LogInfo("[VC] Speaker: " + (_speakerMuted ? "OFF" : "ON"));
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
                    sr.color  = new Color(1f, 0.45f, 0.45f, 1f);
                }
                else if (_impostorMode)
                {
                    sr.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOn.png", 900f);
                    sr.color  = new Color(0.5f, 0.9f, 1f, 1f);
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
                    _speakerMuted ? "VoiceChatPlugin.Resources.SpeakerOff.png"
                                  : "VoiceChatPlugin.Resources.SpeakerOn.png", 900f);
                sr.color = _speakerMuted ? new Color(1f, 0.45f, 0.45f, 1f) : Color.white;
            }
        }
    }

    // ── Host settings sync ────────────────────────────────────────────────────
    private static void TrySyncHostRoomSettings()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined) return;

        var cur = VoiceChatConfig.SyncedRoomSettings;
        if (_lastSentSettings != null && cur.ContentEquals(_lastSentSettings)) return;

        VoiceChatRoomSettings.SendToAll(cur);
        _lastSentSettings = new VoiceChatRoomSettings();
        _lastSentSettings.Apply(cur);
        VoiceChatPluginMain.Logger.LogInfo("[VC] Room settings synced.");
    }

    // ── Sprite helpers (public – used by MeetingSpeakingIndicatorPatch) ────────
    public static readonly Dictionary<string, Sprite> CachedSprites = new();

	public static Sprite LoadSpriteFromResources(string path, float pixelsPerUnit, bool cache = true)
	{
		try
		{
			if (cache && CachedSprites.TryGetValue(path + pixelsPerUnit, out var sprite)) return sprite;
			Texture2D texture = loadTextureFromResources(path);
			sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
			if (cache) sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
			if (!cache) return sprite;
			return CachedSprites[path + pixelsPerUnit] = sprite;
		}
		catch
		{
			System.Console.WriteLine("Error loading sprite from path: " + path);
		}
		return null;
	}

	public static Texture2D loadTextureFromResources(string path)
	{
		try
		{
			var texture = new Texture2D(0, 0, TextureFormat.RGBA32, false)
			{
				wrapMode = TextureWrapMode.Clamp
			};
			var myStream = Assembly.GetCallingAssembly().GetManifestResourceStream(path);
			var data = myStream.ReadFully();
			texture.LoadImage(data, false);
			return texture;
		}
		catch
		{
			System.Console.WriteLine("Error loading texture from resources: " + path);
		}
		return null;
	}

	public static byte[] ReadFully(this Stream input)
	{
		using MemoryStream memoryStream = new MemoryStream();
		input.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}
}
