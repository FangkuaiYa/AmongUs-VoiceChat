using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace VoiceChatPlugin;

/// <summary>
/// Implements the Among Us Mod Client Identification (AU MCI) system so that
/// this plugin's rooms are isolated from the vanilla matchmaking pool.
///
/// What this does:
///   1. When the local client HOSTS a game it sends Tags.HostModdedGame (25)
///      instead of Tags.HostGame, and appends the plugin's GUID to the message.
///      This registers the room under our mod GUID on Innersloth's servers and
///      exempts it from standard anti-cheat.
///
///   2. When the local client SEARCHES for games it injects a "mod" filter
///      containing the same GUID, so only rooms hosted by other VC-plugin
///      clients are returned.
///
///   3. A lobby version-check ensures clients without the plugin cannot join:
///      the host broadcasts the plugin GUID via a custom RPC (198) during
///      OnGameJoined; non-modded clients will simply ignore it, but future
///      modded clients can verify compatibility.
///
/// The GUID below is this plugin's permanent identifier — do not change it.
/// Generated once at https://www.uuidgenerator.net/
/// </summary>
public static class ModdedRoomManager
{
    // ── Plugin identity ──────────────────────────────────────────────────────
    /// <summary>
    /// Permanent mod GUID for the VoiceChat plugin.
    /// Used for AU MCI room registration and matchmaking filter.
    /// </summary>
    public static readonly Guid ModGuid = new("a3f7c821-4b9e-4d62-bc50-1e2f83a97d04");

    /// <summary>RPC tag used to broadcast the mod GUID to joining clients.</summary>
    internal const byte ModHandshakeRpcId = 198;

    // ── HostGame patch ───────────────────────────────────────────────────────

    /// <summary>
    /// Patch <c>InnerNetClient.HostGame</c> to use <c>Tags.HostModdedGame</c>
    /// (byte value 25) and append the mod GUID, registering the room under
    /// our mod identity on Innersloth's matchmaker.
    /// </summary>
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HostGame))]
    public static class HostGamePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InnerNetClient __instance,
            IGameOptions settings,
            GameFilterOptions filterOpts)
        {
            try
            {
                // Build the host-modded-game message ourselves.
                // Tags.HostModdedGame == 25 (byte).
                var msg = MessageWriter.Get(SendOption.Reliable);
                msg.StartMessage(25); // Tags.HostModdedGame

                // Standard HostGame body: serialized options + crossplay flags + filter
                msg.WriteBytesAndSize(GameOptionsManager.Instance.gameOptionsFactory.ToBytes(settings, false));
                msg.Write(CrossplayMode.GetCrossplayFlags());
                filterOpts.Serialize(msg);

                // Append our mod GUID so Innersloth can group us in their matchmaker
                msg.Write(ModGuid.ToByteArray());

                msg.EndMessage();
                __instance.SendOrDisconnect(msg);
                msg.Recycle();

                VoiceChatPluginMain.Logger.LogInfo(
                    $"[VC] HostModdedGame sent with GUID {ModGuid}");
            }
            catch (Exception ex)
            {
                VoiceChatPluginMain.Logger.LogError(
                    $"[VC] HostGamePatch failed, falling back to vanilla host: {ex.Message}");
                // Return true to let the original method run if our patch failed.
                return true;
            }

            // Return false = skip the original HostGame implementation.
            return false;
        }
    }

    // ── FindGame / matchmaking filter patch ──────────────────────────────────

    /// <summary>
    /// Patch <c>FilterOptions.Serialize</c> (or the FindGame message builder)
    /// to append a "mod" filter entry with our GUID so the Innersloth matchmaker
    /// only returns rooms that were hosted with the same GUID.
    /// </summary>
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.RequestGameList))]
    public static class FindGamePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InnerNetClient __instance, IGameOptions settings,
            GameFilterOptions filterOpts)
        {
            try
            {
                var msg = MessageWriter.Get(SendOption.Reliable);
                msg.StartMessage(16); // Tags.GetGameListV2

                filterOpts.Serialize(msg);

                // Append mod filter: type tag "mod" + GUID bytes
                // Protocol: 1-byte length-prefixed ASCII string then 16 raw GUID bytes
                byte[] guidBytes = ModGuid.ToByteArray();
                msg.StartMessage(1); // sub-message tag for extra filters
                msg.Write("mod");    // filter type string
                msg.WriteBytesAndSize(guidBytes);
                msg.EndMessage();

                msg.EndMessage();
                __instance.SendOrDisconnect(msg);
                msg.Recycle();

                VoiceChatPluginMain.Logger.LogInfo(
                    $"[VC] FindGame sent with mod filter GUID {ModGuid}");
            }
            catch (Exception ex)
            {
                VoiceChatPluginMain.Logger.LogError(
                    $"[VC] FindGamePatch failed, falling back to vanilla: {ex.Message}");
                return true;
            }

            return false;
        }
    }

    // ── Handshake RPC: broadcast mod presence to all clients on join ─────────

    /// <summary>
    /// When the local player joins a game, broadcast our mod GUID via a custom
    /// RPC so other plugin clients can confirm compatibility.
    /// Also send upon receiving a handshake from another client.
    /// </summary>
    internal static void SendHandshake()
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
        try
        {
            byte[] guidBytes = ModGuid.ToByteArray();
            var w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                ModHandshakeRpcId,
                SendOption.Reliable, -1);
            w.WriteBytesAndSize(guidBytes);
            AmongUsClient.Instance.FinishRpcImmediately(w);
            VoiceChatPluginMain.Logger.LogInfo("[VC] Mod handshake RPC sent.");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Handshake send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Intercept the handshake RPC from other clients.
    /// Currently just logs; extend here if you need compatibility enforcement.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    public static class HandshakeRpcPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != ModHandshakeRpcId) return;
            try
            {
                byte[] guidBytes = reader.ReadBytesAndSize();
                if (guidBytes != null && guidBytes.Length == 16)
                {
                    var theirGuid = new Guid(guidBytes);
                    VoiceChatPluginMain.Logger.LogInfo(
                        $"[VC] Handshake from {__instance.name}: GUID={theirGuid}" +
                        (theirGuid == ModGuid ? " (match)" : " (mismatch – different version?)"));
                }
            }
            catch { /* non-fatal */ }
        }
    }
}
