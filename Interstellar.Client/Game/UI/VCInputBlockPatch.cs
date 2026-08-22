using HarmonyLib;

namespace Interstellar.Voice;

[HarmonyPatch(typeof(PassiveButtonManager), nameof(PassiveButtonManager.Update))]
public static class VCInputBlockPatch
{
    public static bool IsAnyVoiceWindowOpen =>
        (VoiceSettingsWindow.Instance != null && VoiceSettingsWindow.Instance.ShowWindow)
        || (PublicLobbyWindow.Instance != null && PublicLobbyWindow.Instance.ShowWindow)
        || (PlayerVolumeWindow.Instance != null && PlayerVolumeWindow.Instance.ShowWindow);

    // Returning false skips the original method entirely for this frame.
    static bool Prefix() => !IsAnyVoiceWindowOpen;
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CanMove), HarmonyLib.MethodType.Getter)]
public static class VCCanMoveBlockPatch
{
    static void Postfix(ref bool __result)
    {
        if (VCInputBlockPatch.IsAnyVoiceWindowOpen) __result = false;
    }
}
