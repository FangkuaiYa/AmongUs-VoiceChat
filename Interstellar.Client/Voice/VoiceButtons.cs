using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar;

public enum VoiceChannel
{
    All,
    Impostor,
}

[HarmonyLib.HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class VoiceButtons
{
    static PassiveButton? toggleMicButton;
    static GameObject? toggleMicButtonObject;
    static SpriteRenderer? micInactive, micActive;

    static PassiveButton? toggleSpkButton;
    static GameObject? toggleSpkButtonObject;
    static SpriteRenderer? spkInactive, spkActive;

    static PassiveButton? toggleSetButton;
    static GameObject? toggleSetButtonObject;
    static SpriteRenderer? setInactive, setActive;

    // Shared background that hosts the three voice buttons (TOR copy/paste style).
    static GameObject? voiceBgObject;
    static SpriteRenderer? voiceBgRenderer;
    static GameObject? VoiceModButtons;
    static PassiveButton? toggleSettingsButton;
    static GameObject? toggleSettingsButtonObject;
    private static bool _micMuted, _speakerMuted;
    private static VoiceChannel _channel = VoiceChannel.All;
    public static bool IsSpeakerMuted => _speakerMuted;
    public static bool IsImpostorRadioOnly => _channel == VoiceChannel.Impostor;

    private static bool _tabVisible = false;

    static void Postfix(HudManager __instance)
    {
        if (__instance.MapButton == null) return;

        if (!toggleSettingsButton || !toggleSettingsButtonObject) {
            // add a special button for settings viewing:
            toggleSettingsButtonObject = GameObject.Instantiate(__instance.MapButton.gameObject, __instance.MapButton.transform.parent);
            toggleSettingsButtonObject.transform.localPosition = __instance.MapButton.transform.localPosition + new Vector3(0, -2.1f, -500f);
            toggleSettingsButtonObject.name = "TOGGLESETTINGSBUTTONINTERSTELLAR";
            SpriteRenderer renderer = toggleSettingsButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            SpriteRenderer rendererActive = toggleSettingsButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            toggleSettingsButtonObject.transform.Find("Background").localPosition = Vector3.zero;
            renderer.sprite = LoadSprite("Interstellar.Resources.Settings_Button.png", 100f);
            rendererActive.sprite = LoadSprite("Interstellar.Resources.Settings_ButtonActive.png", 100);
            toggleSettingsButton = toggleSettingsButtonObject.GetComponent<PassiveButton>();
            toggleSettingsButton.OnClick.RemoveAllListeners();
            toggleSettingsButton.OnClick.AddListener((Action)(() => _tabVisible = !_tabVisible));
        }
        toggleSettingsButtonObject!.SetActive(true);
        toggleSettingsButtonObject!.transform.localPosition = AmongUsClient.Instance.GameState == AmongUsClient.GameStates.Joined ?(__instance.SettingsButton?.transform?.localPosition ?? new Vector3()) + new Vector3(-1.45f, -0.82f, -200f) : __instance.MapButton.transform.localPosition + new Vector3(0, -1.65f, -500f);


        if (Input.GetKeyDown(KeyCode.H))
        {
            _tabVisible = !_tabVisible;
        }

        if (!VoiceModButtons)
        {
            VoiceModButtons = new GameObject("VoiceModButtons");
            VoiceModButtons.transform.SetParent(__instance.transform, false);
        }
        bool settingsActive = __instance.SettingsButton!.gameObject.active;
        VoiceModButtons!.SetActive(_tabVisible && settingsActive);

        if (!voiceBgObject)
        {
            voiceBgObject = new GameObject("VC_BtnBG");
            voiceBgRenderer = voiceBgObject.AddComponent<SpriteRenderer>();
            voiceBgRenderer.sprite = LoadSprite("Interstellar.Resources.VoiceButtonsBG.png", 175f);
            voiceBgObject.transform.SetParent(VoiceModButtons.transform, false);
            voiceBgObject.transform.localPosition = new Vector3(-0.6f, 0.03f, -500f);
            voiceBgObject.layer = __instance.SettingsButton.gameObject.layer;
            voiceBgObject.transform.SetSiblingIndex(1);
        }
        voiceBgObject!.SetActive(settingsActive);
        voiceBgObject!.transform.localPosition = __instance.SettingsButton.transform.localPosition + new Vector3(-0.6f, 0.007f, -500f);

        // ── Mic button (Copy icon) ──
        if (!toggleMicButton || !toggleMicButtonObject)
        {
            toggleMicButtonObject = Object.Instantiate(__instance.MapButton.gameObject, voiceBgObject.transform.parent);
            toggleMicButtonObject.transform.localPosition = __instance.MapButton.transform.localPosition + new Vector3(0, -1.25f, -500f);
            toggleMicButtonObject.name = "VC_MicBtn";
            toggleMicButtonObject.transform.Find("Background").gameObject.SetActive(false);

            micInactive = toggleMicButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            micActive = toggleMicButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            micInactive.sprite = LoadSprite("Interstellar.Resources.MicOn.png", 100f);
            micActive.sprite = LoadSprite("Interstellar.Resources.MicOnOver.png", 100f);

            toggleMicButton = toggleMicButtonObject.GetComponent<PassiveButton>();
            toggleMicButton.OnClick.RemoveAllListeners();
            toggleMicButton.OnClick.AddListener((Action)CycleMic);
            toggleMicButtonObject.transform.SetSiblingIndex(2);
        }
        toggleMicButtonObject!.SetActive(settingsActive);
        toggleMicButtonObject!.transform.localPosition = __instance.SettingsButton.transform.localPosition + new Vector3(-1.2f, 0.03f, -500f);

        // ── Speaker button (Paste icon) ──
        if (!toggleSpkButton || !toggleSpkButtonObject)
        {
            toggleSpkButtonObject = Object.Instantiate(__instance.MapButton.gameObject, voiceBgObject.transform.parent);
            toggleSpkButtonObject.transform.localPosition = __instance.MapButton.transform.localPosition + new Vector3(0, -1.25f, -500f);
            toggleSpkButtonObject.name = "VC_SpkBtn";
            toggleSpkButtonObject.transform.Find("Background").gameObject.SetActive(false);

            spkInactive = toggleSpkButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            spkActive = toggleSpkButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            spkInactive.sprite = LoadSprite("Interstellar.Resources.SpeakerOn.png", 100f);
            spkActive.sprite = LoadSprite("Interstellar.Resources.SpeakerOnOver.png", 100f);

            toggleSpkButton = toggleSpkButtonObject.GetComponent<PassiveButton>();
            toggleSpkButton.OnClick.RemoveAllListeners();
            toggleSpkButton.OnClick.AddListener((Action)ToggleSpeaker);
            toggleSpkButtonObject.transform.SetSiblingIndex(2);
        }
        toggleSpkButtonObject!.SetActive(settingsActive);
        toggleSpkButtonObject!.transform.localPosition = __instance.SettingsButton.transform.localPosition + new Vector3(-0.6f, 0.03f, -500f);

        // ── Settings button ──
        if (!toggleSetButton || !toggleSetButtonObject)
        {
            toggleSetButtonObject = Object.Instantiate(__instance.MapButton.gameObject, voiceBgObject.transform.parent);
            toggleSetButtonObject.transform.localPosition = __instance.MapButton.transform.localPosition + new Vector3(0, -1.25f, -500f);
            toggleSetButtonObject.name = "VC_SetBtn";
            toggleSetButtonObject.transform.Find("Background").gameObject.SetActive(false);

            setInactive = toggleSetButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            setActive = toggleSetButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            setInactive.sprite = LoadSprite("Interstellar.Resources.Settings_Button.png", 100f);
            setActive.sprite = LoadSprite("Interstellar.Resources.Settings_ButtonActive.png", 100f);

            toggleSetButton = toggleSetButtonObject.GetComponent<PassiveButton>();
            toggleSetButton.OnClick.RemoveAllListeners();
            toggleSetButton.OnClick.AddListener((Action)(() =>
            {
                var w = VoiceSettingsWindow.Instance;
                if (w != null) { if (!w.ShowWindow) w.Open(); else w.Close(); }
            }));
            toggleSetButtonObject.transform.SetSiblingIndex(2);
        }
        toggleSetButtonObject!.SetActive(settingsActive);
        toggleSetButtonObject!.transform.localPosition = __instance.SettingsButton.transform.localPosition + new Vector3(0f, 0.03f, -500f);

        RefreshVisuals();
    }

    internal static void CycleMic()
    {
        bool impRadioOn = VoiceConfig.SyncedRoomSettings.ImpostorPrivateRadio;
        bool canImpMode = PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true && !PlayerControl.LocalPlayer.Data.IsDead && impRadioOn;
        if (!_micMuted && _channel == VoiceChannel.All) { if (canImpMode) _channel = VoiceChannel.Impostor; else _micMuted = true; }
        else if (_channel == VoiceChannel.Impostor) { _channel = VoiceChannel.All; _micMuted = true; }
        else { _micMuted = false; _channel = VoiceChannel.All; }
        ApplyMicState();
        RefreshVisuals();
    }

    internal static void ToggleSpeaker()
    {
        _speakerMuted = !_speakerMuted;
        var room = VoiceRoom.Current;
        if (room != null)
        {
            if (_speakerMuted) { room.SetMasterVolume(0f); room.SetSpeaker(null!); }
            else { room.SetSpeaker(VoiceConfig.SpeakerDevice); room.SetMasterVolume(VoiceConfig.MasterVolume); }
        }
    }

    internal static void ApplyMicState() => VoiceRoom.Current?.SetMute(_micMuted, _channel == VoiceChannel.Impostor);
    internal static void ApplySpeakerState()
    {
        var room = VoiceRoom.Current;
        if (room == null) return;
        if (_speakerMuted) { room.SetMasterVolume(0f); room.SetSpeaker(null!); }
        else if (!room.HasSpeaker) { room.SetSpeaker(VoiceConfig.SpeakerDevice); room.SetMasterVolume(VoiceConfig.MasterVolume); }
    }

    static void RefreshVisuals()
    {
        bool micOff = _micMuted;
        bool impChan = _channel == VoiceChannel.Impostor;
        // Red is reserved for impostor-radio mode only; the speaker never turns red.
        bool useRedState = impChan && !micOff;
        Color micColor = useRedState ? new Color(1f, 0.2f, 0.2f) : Color.white;
        Color spkColor = Color.white;

        if (micInactive != null && micActive != null)
        {
            micInactive.sprite = LoadMicSprite(useRedState, false);
            micActive.sprite = LoadMicSprite(useRedState, true);
            micInactive.color = micColor;
            micActive.color = micColor;
        }
        if (spkInactive != null && spkActive != null)
        {
            spkInactive.sprite = LoadSprite(_speakerMuted ? "Interstellar.Resources.SpeakerOff.png" : "Interstellar.Resources.SpeakerOn.png", 100f);
            spkActive.sprite = LoadSprite(_speakerMuted ? "Interstellar.Resources.SpeakerOffOver.png" : "Interstellar.Resources.SpeakerOnOver.png", 100f);
            spkInactive.color = spkColor;
            spkActive.color = spkColor;
        }
    }

    static Sprite? LoadMicSprite(bool useRedState, bool active)
    {
        var normalPath = active ? "Interstellar.Resources.MicOnOver.png" : "Interstellar.Resources.MicOn.png";
        var mutedPath = active ? "Interstellar.Resources.MicOffOver.png" : "Interstellar.Resources.MicOff.png";

        if (_micMuted)
            return LoadSprite(mutedPath, 100f);

        var redPath = active ? "Interstellar.Resources.MicOnRedOver.png" : "Interstellar.Resources.MicOnRed.png";
        if (useRedState)
        {
            var redSprite = LoadSprite(redPath, 100f);
            if (redSprite != null) return redSprite;
        }

        return LoadSprite(normalPath, 100f);
    }

    static readonly Dictionary<string, Sprite> _spriteCache = new();
    static Sprite? LoadSprite(string path, float ppu)
    {
        if (_spriteCache.TryGetValue(path, out var c)) return c;
        try
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            if (s == null) return null;
            var t = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            using var m = new System.IO.MemoryStream(); s.CopyTo(m);
            t.LoadImage(m.ToArray(), false);
            var sp = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), ppu);
            sp.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = sp; return sp;
        }
        catch { return null; }
    }
}