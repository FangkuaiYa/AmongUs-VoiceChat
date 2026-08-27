using HarmonyLib;
using Interstellar.Voice;
using UnityEngine;
using Object = UnityEngine.Object;
using System.Collections.Generic;

namespace Interstellar;

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class MeetingSpeakingIndicatorPatch
{
    private const float SpeakingThreshold = 0.01f;

    private static readonly Dictionary<byte, Color> OriginalGlowColors = new();

    public static void Postfix(MeetingHud __instance)
    {
        if (__instance.playerStates == null) return;

        // FIX: Speaker mute — skip all speaking indicators when muted
        if (InterstellarHudState.IsSpeakerMuted)
        {
            foreach (var state in __instance.playerStates)
            {
                if (state == null || !state.HighlightedFX) continue;
                state.HighlightedFX.enabled = false;
            }
            return;
        }

        var room = VoiceRoom.Current;

        var speaking = new HashSet<byte>();
        if (room != null)
        {
            foreach (var c in room.AllClients)
                if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold && c.IsAudible)
                    speaking.Add(c.PlayerId);

            byte localId = PlayerControl.LocalPlayer
                ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
            // Don't show self-speaking indicator when locally muted
            if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold
                && localId != byte.MaxValue && !room.Mute)
                speaking.Add(localId);
        }

        foreach (var state in __instance.playerStates)
        {
            if (state == null || !state.HighlightedFX) continue;

            bool isSpeaking = speaking.Contains(state.PlayerId);

            if (isSpeaking)
            {
                Color glowColor = GetPlayerColor(state.PlayerId);

                if (!OriginalGlowColors.ContainsKey(state.PlayerId))
                    OriginalGlowColors[state.PlayerId] = state.HighlightedFX.color;

                state.HighlightedFX.color = glowColor;
                state.HighlightedFX.enabled = true;
            }
            else
            {
                if (OriginalGlowColors.TryGetValue(state.PlayerId, out var orig))
                    state.HighlightedFX.color = orig;
                state.HighlightedFX.enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
    private static class DestroyPatch
    {
        private static void Postfix() => OriginalGlowColors.Clear();
    }

    private static Color GetPlayerColor(byte playerId)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc.PlayerId != playerId) continue;

            int colorId = pc.Data.DefaultOutfit.ColorId;
            if (colorId >= 0 && colorId < Palette.PlayerColors.Length)
                return Palette.PlayerColors[colorId];
        }
        return new Color(0.18f, 0.80f, 0.44f, 1f);
    }
}
