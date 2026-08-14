using HarmonyLib;

namespace Interstellar.Voice;

[HarmonyPatch]
public static class VoiceChatPatches
{
    // Windows keyboard shortcuts (Nebula uses VirtualInput, we use KeyboardJoystick)
    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.M))
            InterstellarHudState.CycleMicPublic();
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.N))
            InterstellarHudState.ToggleSpeakerPublic();
    }
}
