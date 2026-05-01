using System;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

/// <summary>
/// Mirrors Nebula's NoSVCRoom.Update() static method.
/// Called every frame by VCManager.Update() when scene is "OnlineGame" or "EndGame".
///
/// Also handles state transitions that Nebula handles in OnGameStart()/OnGameEnd():
///   - IntroCutscene ending -> ResetMapping (Nebula: OnGameStart clients.Do(c => c.UpdateMappedState()))
///   - EndGame -> Rejoin     (Nebula: room.Rejoin())
/// </summary>
internal static class VoiceChatRoomDriver
{
    private static bool _wasInGame   = false; // ShipStatus was active last frame
    private static bool _wasInIntro  = false; // IntroCutscene was active last frame
    private static bool _wasInEndGame= false; // EndGameManager was active last frame

    private static bool IsLocalServer()
    {
        var addr = AmongUsClient.Instance?.networkAddress;
        return addr is "127.0.0.1" or "localhost";
    }

    internal static void Update()
    {
        // Nebula: shouldNotUseVC = !option || IsLocalServer()
        bool shouldNotUseVC = AmongUsClient.Instance == null
            || AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined
            || IsLocalServer();

        if (shouldNotUseVC)
        {
            if (VoiceChatRoom.Current != null)
                VoiceChatRoom.CloseCurrentRoom();
            _wasInGame = _wasInIntro = _wasInEndGame = false;
            return;
        }

        // Nebula: if (Instance == null) StartVoiceChat(region, roomId)
        if (VoiceChatRoom.Current == null)
        {
            VoiceChatRoom.Start();
            VoiceChatHudState.ApplyMicState();
            VoiceChatHudState.ApplySpeakerState();
            if (AmongUsClient.Instance!.AmHost)
            {
                VoiceChatConfig.ApplyLocalHostSettingsToSynced();
                VoiceChatHudState.MarkRoomSettingsDirty();
            }
        }

        if (VoiceChatRoom.Current == null) return;

        // ── State transition detection (replacing removed HarmonyPatches) ───────

        // IntroCutscene ended → reset client mappings
        // Nebula: OnGameStart() { clients.Values.Do(c => c.UpdateMappedState()); }
        bool inIntro = IntroCutscene.Instance != null;
        if (_wasInIntro && !inIntro)
        {
            foreach (var c in VoiceChatRoom.Current.AllClients)
                c.ResetMapping();
            VoiceChatPluginMain.Logger.LogInfo("[VC] IntroCutscene ended: mappings reset.");
        }
        _wasInIntro = inIntro;

		// EndGame started → Rejoin
		// Nebula: EndGame scene triggers Rejoin via room lifecycle
		bool inEndGame = UnityEngine.Object.FindObjectOfType<EndGameManager>() != null;
		if (inEndGame && !_wasInEndGame)
        {
            VoiceChatRoom.Current.Rejoin();
            // Force re-broadcast local profile so others can re-map
            VoiceChatRoom.Current.ForceUpdateLocalProfile();
            VoiceChatPluginMain.Logger.LogInfo("[VC] EndGame: room rejoined.");
        }
        _wasInEndGame = inEndGame;

        // ── Normal per-frame update ───────────────────────────────────────────
        VoiceChatHudState.TrySyncHostRoomSettings();

        try { VoiceChatRoom.Current.Update(); }
        catch (Exception ex)
        { VoiceChatPluginMain.Logger.LogError("[VC] Room update error: " + ex); }
    }
}
