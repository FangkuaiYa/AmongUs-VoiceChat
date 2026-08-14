using System;
using System.Collections.Generic;
using Interstellar.Voice;

namespace Interstellar;

internal static class CustomServerLoader
{
    public static string? MatchedVcLocation { get; private set; }

    public static string GetVCServer()
    {
        var url = VoiceConfig.GetActiveServerURL();
        MatchedVcLocation = url.Contains("bcl.server.amongusclub.cn")
            ? "Beijing (AmongUsClub)" : "BetterCrewLink Official";
        return url;
    }

    internal static void Load() { }

    internal static List<string> GetServerUrls()
    {
        return new List<string>
        {
            "https://bettercrewl.ink",
            "https://bcl.server.amongusclub.cn"
        };
    }
}
