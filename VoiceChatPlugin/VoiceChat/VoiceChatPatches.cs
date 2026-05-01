using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Remaining HarmonyPatches — kept ONLY where Nebula also uses patches for the same purpose.
///
/// Nebula uses NO patches for:
///   - Room lifecycle (handled by VCManager MonoBehaviour + SceneManager events)
///   - HUD button creation/update (handled by VoiceChatHudState)
///   - Mic/speaker toggle (handled by VoiceChatHudState buttons)
///   - ExitGame, OnGameJoined, IntroCutscene, EndGameManager (all handled by VCManager)
///
/// Patches retained here:
///   - PlayerControl.HandleRpc (audio RPC 203, room settings RPC 201) — same as Nebula's NebulaRPC
///   - MeetingHud speaking indicator — UI-only, no lifecycle impact
///   - VoiceChatOptionsPatches — settings UI, harmless
///   - VoiceVolumeMenu — per-player volume, harmless
///   - Windows keyboard shortcuts — no Android equivalent, guarded by #if WINDOWS
/// </summary>
[HarmonyPatch]
public static class VoiceChatPatches
{
    // Windows keyboard shortcuts only (Nebula uses VirtualInput, not KeyboardJoystick patch,
    // but since we have no VirtualInput system we keep this Windows-only).
#if WINDOWS
    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.M))
        {
            // Cycle mic via HudState
            VoiceChatHudState.CycleMicPublic();
        }
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.N))
        {
            VoiceChatHudState.ToggleSpeakerPublic();
        }
    }
#endif
}
