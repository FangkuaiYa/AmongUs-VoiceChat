using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using TMPro;
using Twitch;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Interstellar.Voice;

namespace Interstellar;

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
internal static class UpdateChecker
{
    private const string RepoOwner = "FangkuaiYa";
    private const string RepoName = "Interstellar";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string MirrorApiUrl = "https://api.amongusclub.cn/Interstellar/GitHubURL.json";

    private static readonly string CurrentVersionStr = InterstellarPlugin.PluginVersion;
    private static readonly Version CurrentVersion = new(CurrentVersionStr);
    private static readonly bool IsAndroid = Application.platform == RuntimePlatform.Android;
    private static readonly string PluginDllName = Assembly.GetExecutingAssembly().GetName().Name + ".dll";

    private static bool _checked;
    private static List<string> _mirrorUrls = new();

    public static void Postfix(MainMenuManager __instance)
    {
        if (_checked) return;
        _checked = true;
        _host = __instance;
        __instance.StartCoroutine(CoCheck());
    }

    private static MainMenuManager? _host;

    [HideFromIl2Cpp]
    private static IEnumerator CoCheck()
    {
        if (IsChinese())
        {
            InterstellarPlugin.Logger?.LogInfo("[Update] Chinese locale detected, fetching mirrors...");
            yield return CoFetchMirrors();
            InterstellarPlugin.Logger?.LogInfo($"[Update] Got {_mirrorUrls.Count} mirror URLs.");
        }
        else
        {
            InterstellarPlugin.Logger?.LogInfo("[Update] Non-Chinese locale, skipping mirrors.");
        }

        var www = UnityWebRequest.Get(GitHubApiUrl);
        www.SetRequestHeader("User-Agent", "Interstellar-UpdateChecker");
        yield return www.SendWebRequest();

        if (www.isNetworkError || www.isHttpError) { www.Dispose(); yield break; }

        GithubRelease? release = null;
        try
        {
            var raw = GetUnstrippedData(www.downloadHandler);
            var text = raw != null ? Encoding.UTF8.GetString(raw) : "";
            release = JsonSerializer.Deserialize<GithubRelease>(text);
        }
        catch { }
        www.Dispose();

        if (release == null || string.IsNullOrEmpty(release.Tag)) yield break;

        var latest = ParseVersion(release.Tag);
        if (latest == null || CurrentVersion >= latest) yield break;

        // Find the .dll asset
        var asset = release.Assets.Find(a => string.Equals(a.Name, PluginDllName, StringComparison.OrdinalIgnoreCase));
        if (asset == null) yield break;

        // Build download URLs: mirrors first, then original
        var urls = new List<string>();
        foreach (var m in _mirrorUrls) urls.Add(m + asset.DownloadUrl);
        urls.Add(asset.DownloadUrl);
        InterstellarPlugin.Logger?.LogInfo($"[Update] Download URLs: {string.Join(" | ", urls)}");

        // Popup text
        var title = TranslationHelper.Get("vc.update.title", "Interstellar Voice Chat Update");
        var desc = TranslationHelper.Get("vc.update.desc", "New version available!")
            .Replace("{current}", CurrentVersionStr).Replace("{latest}", latest.ToString());
        var msg = $"{title}\n\n{desc}";
        if (IsAndroid)
            msg += "\n\n" + TranslationHelper.Get("vc.update.android", "Android users: please update via your mod launcher.");

        var btnUpdate = TranslationHelper.Get("vc.update.button", "Update");
        var btnDismiss = TranslationHelper.Get("vc.update.dismiss", "Dismiss");

        // Show popup — Android opens URL, desktop downloads automatically
        var dp = DiscordManager.Instance?.discordPopup;
        if (dp == null) yield break;

        var popup = UnityEngine.Object.Instantiate(dp, dp.transform.parent);
        popup.transform.localScale = Vector3.one * 2f;

        var yesT = CreateButton(popup, "yesButton", btnUpdate, new Vector3(-0.85f, -0.75f));
        var noT = CreateButton(popup, "noButton", btnDismiss, new Vector3(0.85f, -0.75f));

        if (IsAndroid)
        {
            yesT.GetComponent<PassiveButton>().OnClick.AddListener((Action)(() =>
                Application.OpenURL(asset.DownloadUrl)));
        }
        else
        {
            yesT.GetComponent<PassiveButton>().OnClick.AddListener((Action)(() =>
            {
                popup.gameObject.SetActive(false);
                if (_host != null) _host.StartCoroutine(CoDownload(urls));
            }));
        }

        noT.GetComponent<PassiveButton>().OnClick.AddListener((Action)(() =>
            popup.gameObject.SetActive(false)));

        popup.Show(msg);
    }

    /// <summary>Download with progress bar. Try each mirror URL.</summary>
    [HideFromIl2Cpp]
    private static IEnumerator CoDownload(List<string> urls)
    {
        var twitch = TwitchManager.Instance?.TwitchPopup;
        if (twitch == null) yield break;

        var popup = UnityEngine.Object.Instantiate(twitch, twitch.transform.parent);
        popup.TextAreaTMP.fontSize *= 0.7f;
        popup.TextAreaTMP.enableAutoSizing = false;
        popup.Show();
        popup.transform.GetChild(2).gameObject.SetActive(false);
        popup.TextAreaTMP.text = TranslationHelper.Get("vc.update.downloading", "Downloading update...");

        byte[]? data = null;

        foreach (var url in urls)
        {
            InterstellarPlugin.Logger?.LogInfo($"[Update] Trying: {url}");

            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(url);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("User-Agent", "Interstellar-UpdateChecker");
            var op = www.SendWebRequest();

            while (!op.isDone)
            {
                int stars = Mathf.CeilToInt(www.downloadProgress * 10);
                popup.TextAreaTMP.text = TranslationHelper.Get("vc.update.downloading", "Downloading update...") + "\n"
                    + new string('\u25A0', stars) + new string('\u25A1', 10 - stars);
                yield return null;
            }

            if (www.isNetworkError || www.isHttpError)
            {
                InterstellarPlugin.Logger?.LogWarning($"[Update] Failed: {url} error={www.error}");
            }
            else
            {
                var raw = GetUnstrippedData(www.downloadHandler);
                InterstellarPlugin.Logger?.LogInfo($"[Update] Downloaded {raw?.Length ?? 0} bytes from {url}");
                if (raw != null && raw.Length > 1024)
                {
                    data = raw;
                    www.Dispose();
                    break;
                }
            }
            www.Dispose();
        }

        if (data == null)
        {
            InterstellarPlugin.Logger?.LogWarning("[Update] All download URLs failed.");
            popup.TextAreaTMP.text = TranslationHelper.Get("vc.update.failed", "Update failed.");
            popup.transform.GetChild(2).gameObject.SetActive(true);
            yield break;
        }

        try
        {
            var pluginPath = GetPluginPath();
            var tempPath = pluginPath + ".temp";
            var oldPath = pluginPath + ".old";

            File.WriteAllBytes(tempPath, data);
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (File.Exists(pluginPath)) File.Move(pluginPath, oldPath);
            File.Move(tempPath, pluginPath);

            popup.TextAreaTMP.text = TranslationHelper.Get("vc.update.success", "Update complete! Please restart the game.");
        }
        catch (Exception ex)
        {
            popup.TextAreaTMP.text = TranslationHelper.Get("vc.update.failed", "Update failed.") + "\n" + ex.Message;
        }
        popup.transform.GetChild(2).gameObject.SetActive(true);
    }

    private static string GetPluginPath()
    {
        // Find our plugin DLL in BepInEx chainloader
        foreach (var p in IL2CPPChainloader.Instance.Plugins.Values)
        {
            if (Path.GetFileName(p.Location).Contains(PluginDllName, StringComparison.OrdinalIgnoreCase))
                return p.Location;
        }
        return Path.Combine(Paths.PluginPath, PluginDllName);
    }

    private static IEnumerator CoFetchMirrors()
    {
        var www = UnityWebRequest.Get(MirrorApiUrl);
        yield return www.SendWebRequest();
        if (www.isNetworkError || www.isHttpError)
        {
            InterstellarPlugin.Logger?.LogWarning($"[Update] Mirror API failed: {www.error} (code={www.responseCode})");
            www.Dispose();
            yield break;
        }
        try
        {
            var raw = GetUnstrippedData(www.downloadHandler);
            var json = raw != null ? Encoding.UTF8.GetString(raw) : "";
            InterstellarPlugin.Logger?.LogInfo($"[Update] Mirror API response: {json}");
            var data = JsonSerializer.Deserialize<MirrorData>(json);
            if (data?.Mirrors is { Count: > 0 }) _mirrorUrls = data.Mirrors;
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger?.LogWarning($"[Update] Mirror parse error: {ex.Message}");
        }
        www.Dispose();
    }

    private static Version? ParseVersion(string tag)
    {
        var t = tag.TrimStart('v').Split('-')[0];
        return Version.TryParse(t, out var v) ? v : null;
    }

    private static bool IsChinese()
    {
        try { return CultureInfo.CurrentUICulture.Name.StartsWith("zh"); }
        catch { return false; }
    }

    // ── Button creation ───────────────────────────────────────────

    private static Transform CreateButton(GenericPopup popup, string name, string text, Vector3 offset)
    {
        var existing = popup.transform.FindChild(name);
        if (existing) return existing;

        var template = popup.transform.FindChild("ExitGame");
        UnityEngine.Object.Destroy(template.GetComponentInChildren<TextTranslatorTMP>());
        template.gameObject.SetActive(false);

        var button = UnityEngine.Object.Instantiate(template, popup.transform);
        button.gameObject.name = name;
        button.transform.position += offset;
        button.transform.localScale /= 2f;

        button.GetComponentInChildren<TextMeshPro>().text = text;
        button.gameObject.SetActive(true);
        return button.transform;
    }

    // ── JSON models ────────────────────────────────────────────────

    private class MirrorData
    {
        [JsonPropertyName("mirrors")]
        public List<string> Mirrors { get; set; } = new();
    }

    private class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = new();
    }

    private class GithubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }

    // ── IL2CPP-safe data access ────────────────────────────────────

    private static byte[]? GetUnstrippedData(DownloadHandler dh)
    {
        var native = dh.GetNativeData();
        if (native.IsCreated)
            return native.ToArray();
        return null;
    }
}
