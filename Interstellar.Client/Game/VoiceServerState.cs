namespace Interstellar.Voice;

/// <summary>
/// Stores the latest server info received from the voice server.
/// Read by UI components (splash screen, capacity popup).
/// </summary>
public static class VoiceServerState
{
    public static int OptimalPlayers { get; private set; }
    public static int CurrentTotalPlayers { get; private set; }
    public static string VoiceServerUrl { get; private set; } = "";

    public static bool HasInfo { get; private set; }

    /// <summary>Whether the server is at or above optimal capacity.</summary>
    public static bool IsAtCapacity =>
        OptimalPlayers > 0 && CurrentTotalPlayers >= OptimalPlayers;

    public static void Update(int optimalPlayers, int totalClients, string serverUrl)
    {
        OptimalPlayers = optimalPlayers;
        CurrentTotalPlayers = totalClients;
        VoiceServerUrl = serverUrl;
        HasInfo = true;
    }

    public static void Reset()
    {
        OptimalPlayers = 0;
        CurrentTotalPlayers = 0;
        VoiceServerUrl = "";
        HasInfo = false;
    }
}
