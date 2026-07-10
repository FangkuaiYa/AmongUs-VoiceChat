using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using UnityEngine.Networking;

namespace VoiceChatPlugin;

/// <summary>
/// Loads custom Among Us servers and maps them to voice servers.
/// Supports: API fetch, custom JSON list, forced voice server override.
/// </summary>
internal static class CustomServerLoader
{
	private const string ApiUrl = "https://api.amongusclub.cn/Interstellar/ServerList.json";
	private const string FallbackVcUrl = "ws://47.122.116.50:22021";
	private const string DefaultRegionName = "Modded Asia (MAS)";

	private class ServerEntry
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("address")]
		public string Address { get; set; } = "";

		[JsonPropertyName("port")]
		public ushort Port { get; set; }

		[JsonPropertyName("vc")]
		public string? VcServer { get; set; }

		[JsonPropertyName("vcLocation")]
		public string? VcLocation { get; set; }
	}

	private class ServerListRoot
	{
		[JsonPropertyName("servers")]
		public List<ServerEntry> Servers { get; set; } = new();
	}

	private static List<ServerEntry> _customServers = new();
	private static List<ServerEntry> _apiServers = new();
	private static List<ServerEntry>? _mergedServers;
	private static IRegionInfo[]? _regions;

	public static string? MatchedVcLocation { get; private set; }
	public static string? PendingRegionName;

	internal static void Load()
	{
		ParseCustomServers();

		if (VoiceChat.VoiceChatConfig.UseApiServerList)
			FetchApiServers();

		MergeAndBuildRegions();
	}

	private static void ParseCustomServers()
	{
		var json = VoiceChat.VoiceChatConfig.CustomServerListJson;
		if (string.IsNullOrWhiteSpace(json)) return;

		try
		{
			var list = DeserializeServerList(json);
			if (list is { Count: > 0 })
			{
				_customServers = list;
				InterstellarPlugin.Logger?.LogInfo($"[VC] Loaded {list.Count} custom servers from config.");
			}
		}
		catch (Exception ex)
		{
			InterstellarPlugin.Logger?.LogWarning($"[VC] Failed to parse custom server list: {ex.Message}");
		}
	}

	private static void FetchApiServers()
	{
		try
		{
			var request = UnityWebRequest.Get(ApiUrl);
			request.timeout = 8;
			var op = request.SendWebRequest();

			while (!op.isDone) { }

			if (request.result != UnityWebRequest.Result.Success)
				throw new Exception($"HTTP request failed: {request.error}");

			var list = DeserializeServerList(request.downloadHandler.text);
			if (list is not { Count: > 0 })
				throw new Exception("Server list is empty or invalid.");

			_apiServers = list;
			InterstellarPlugin.Logger?.LogInfo($"[VC] Loaded {list.Count} servers from API.");
		}
		catch (Exception ex)
		{
			InterstellarPlugin.Logger?.LogWarning($"[VC] Failed to fetch API server list: {ex.Message}");
			_apiServers.Clear();
		}
	}

	private static List<ServerEntry>? DeserializeServerList(string json)
	{
		if (json.TrimStart().StartsWith("["))
			return JsonSerializer.Deserialize<List<ServerEntry>>(json);

		var root = JsonSerializer.Deserialize<ServerListRoot>(json);
		return root?.Servers;
	}

	private static void MergeAndBuildRegions()
	{
		var merged = new List<ServerEntry>();

		var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var s in _customServers)
		{
			if (!string.IsNullOrWhiteSpace(s.Name) && nameSet.Add(s.Name))
				merged.Add(s);
		}

		foreach (var s in _apiServers)
		{
			if (!string.IsNullOrWhiteSpace(s.Name) && nameSet.Add(s.Name))
				merged.Add(s);
		}

		if (merged.Count == 0)
		{
			InterstellarPlugin.Logger?.LogWarning("[VC] No servers available — server list is empty.");
			_mergedServers = null;
			_regions = null;
			return;
		}

		_mergedServers = merged;

		var regionList = new List<IRegionInfo>();
		foreach (var e in merged)
		{
			regionList.Add(new StaticHttpRegionInfo(
				e.Name,
				StringNames.NoTranslation,
				e.Address,
				new[] { new ServerInfo(e.Name + "-1", e.Address, e.Port, false) }).Cast<IRegionInfo>());
		}
		_regions = regionList.ToArray();

		InterstellarPlugin.Logger?.LogInfo($"[VC] Total regions: {_regions.Length} (custom: {_customServers.Count}, API: {_apiServers.Count}).");
	}

	internal static IRegionInfo[]? GetRegions() => _regions;

	internal static string GetVCServer()
	{
		if (VoiceChat.VoiceChatConfig.ForceVoiceServerEnabled)
		{
			var forced = VoiceChat.VoiceChatConfig.ForceVoiceServerUrl;
			if (!string.IsNullOrEmpty(forced))
			{
				InterstellarPlugin.Logger?.LogInfo($"[VC] Force VC: {forced}");
				return forced;
			}
			InterstellarPlugin.Logger?.LogInfo("[VC] Force VC enabled but URL empty, using fallback.");
			return FallbackVcUrl;
		}

		if (_mergedServers != null)
		{
			var currentRegion = ServerManager.Instance?.CurrentRegion;
			if (currentRegion != null)
			{
				var match = _mergedServers.Find(e =>
					string.Equals(e.Name, currentRegion.Name, StringComparison.OrdinalIgnoreCase));
				if (match != null && !string.IsNullOrEmpty(match.VcServer))
				{
					MatchedVcLocation = match.VcLocation;
					InterstellarPlugin.Logger?.LogInfo($"[VC] Matched '{currentRegion.Name}' → VC: {match.VcServer}" +
						(match.VcLocation != null ? $" ({match.VcLocation})" : ""));
					return match.VcServer;
				}
			}
		}

		MatchedVcLocation = null;
		InterstellarPlugin.Logger?.LogInfo("[VC] No match, using fallback VC URL.");
		return FallbackVcUrl;
	}

	[HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoConnect))]
	public static class SkipAuthCoConnectPatch
	{
		public static bool Prefix()
		{
			if (_regions != null && ServerManager.Instance != null)
			{
				var cur = ServerManager.Instance.CurrentRegion;
				if (cur != null && _regions.Any(r => string.Equals(r.Name, cur.Name, StringComparison.OrdinalIgnoreCase)))
					return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoWaitForNonce))]
	public static class SkipNoncePatch
	{
		public static bool Prefix()
		{
			if (_regions != null && ServerManager.Instance != null)
			{
				var cur = ServerManager.Instance.CurrentRegion;
				if (cur != null && _regions.Any(r => string.Equals(r.Name, cur.Name, StringComparison.OrdinalIgnoreCase)))
					return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(ServerManager), nameof(ServerManager.Awake))]
	public static class ServerManagerAwakePatch
	{
		public static void Postfix(ServerManager __instance)
		{
			if (_regions == null || _regions.Length == 0) return;

			__instance.AvailableRegions = _regions;

			var cur = __instance.CurrentRegion;
			if (cur == null || !_regions.Any(r => string.Equals(r.Name, cur.Name, StringComparison.OrdinalIgnoreCase)))
			{
				var def = _regions.FirstOrDefault(r => string.Equals(r.Name, DefaultRegionName, StringComparison.OrdinalIgnoreCase)) ?? _regions[0];
				__instance.SetRegion(def);
				InterstellarPlugin.Logger?.LogInfo($"[VC] Set default region: {def.Name}");
			}
		}
	}

	[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
	public static class MainMenuManagerStartPatch
	{
		private static bool _initialized;

		public static void Postfix()
		{
			if (_initialized) return;
			if (_regions == null || _regions.Length == 0) return;

			var sm = ServerManager.Instance;
			if (sm == null) return;

			var cur = sm.CurrentRegion;
			if (cur == null || !_regions.Any(r => string.Equals(r.Name, cur.Name, StringComparison.OrdinalIgnoreCase)))
			{
				var def = _regions.FirstOrDefault(r => string.Equals(r.Name, DefaultRegionName, StringComparison.OrdinalIgnoreCase)) ?? _regions[0];
				sm.SetRegion(def);
				InterstellarPlugin.Logger?.LogInfo($"[VC] MainMenu: switched to {def.Name}");
			}

			_initialized = true;
		}
	}

	[HarmonyPatch(typeof(EnterCodeManager), nameof(EnterCodeManager.FindGameResult))]
	public static class FindGameResultPatch
	{
		public static void Postfix(HttpMatchmakerManager.FindGameByCodeResponse response)
		{
			PendingRegionName = (response != null && !string.IsNullOrEmpty(response.UntranslatedRegion))
				? response.UntranslatedRegion
				: null;
		}
	}

	[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoJoinOnlineGameFromListing))]
	public static class JoinGameRegionSwitchPatch
	{
		public static void Prefix()
		{
			if (string.IsNullOrEmpty(PendingRegionName)) return;
			if (ServerManager.Instance == null) return;

			var regions = ServerManager.Instance.AvailableRegions;
			if (regions == null) return;

			var target = regions.FirstOrDefault(r =>
				string.Equals(r.Name, PendingRegionName, StringComparison.OrdinalIgnoreCase));

			if (target != null && ServerManager.Instance.CurrentRegion != target)
			{
				ServerManager.Instance.SetRegion(target);
				InterstellarPlugin.Logger?.LogInfo($"[RegionSwitch] Switched to '{target.Name}' for code-join");
			}

			PendingRegionName = null;
		}
	}
}