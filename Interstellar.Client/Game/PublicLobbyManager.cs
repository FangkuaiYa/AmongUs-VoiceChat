using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Interstellar.Voice;

public static class PublicLobbyManager
{
    public class LobbyInfo
    {
        public int id { get; set; }
        public string title { get; set; } = "";
        public string host { get; set; } = "";
        public int current_players { get; set; }
        public int max_players { get; set; }
        public string language { get; set; } = "";
        public string mods { get; set; } = "";
        public bool isPublic { get; set; }
        public string server { get; set; } = "";
        public int gameState { get; set; }
    }

    public static Action<int, string, string>? OnLobbyJoinResult;

    /// <summary>Cached lobby list from socket.io real-time events.</summary>
    public static Dictionary<int, LobbyInfo> LobbyMap { get; } = new();
    public static List<LobbyInfo> CachedLobbies => new(LobbyMap.Values);
    public static bool IsLoading { get; set; }
    public static string? LastError { get; set; }
    public static bool IsWatching { get; set; }

    // Called by ServerConnection when socket.io events arrive
    internal static void OnNewLobbies(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<LobbyInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list != null)
                foreach (var l in list) LobbyMap[l.id] = l;
        }
        catch { }
        IsLoading = false;
    }

    internal static void OnUpdateLobby(string json)
    {
        try
        {
            var lobby = JsonSerializer.Deserialize<LobbyInfo>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (lobby != null) LobbyMap[lobby.id] = lobby;
        }
        catch { }
    }

    internal static void OnRemoveLobby(int id)
    {
        LobbyMap.Remove(id);
    }

    public static void StartWatching()
    {
        IsWatching = true;
        IsLoading = true;
        LobbyMap.Clear();
    }

    public static void StopWatching()
    {
        IsWatching = false;
        LobbyMap.Clear();
    }

    public static string GetGameStateName(int state)
    {
        return state switch
        {
            0 => "Menu", 1 => "Lobby", 2 => "Tasks", 3 => "Discussion", _ => "?"
        };
    }
}
