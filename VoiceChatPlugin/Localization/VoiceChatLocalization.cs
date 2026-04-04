using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AmongUs.Data;

namespace VoiceChatPlugin;

public static class VoiceChatLocalization
{
    private const uint SChinese = 13;
    private const uint TChinese = 14;

    private static Dictionary<string, string>? _currentDict;
    private static uint _currentLangId = uint.MaxValue;

    // FIX: The actual embedded resource path is VoiceChatPlugin.Resources.Locale.{fileName}
    // Previously used VoiceChatPlugin.Locale.{fileName} which did not match the project layout.
    private static string GetResourceName(string fileName)
        => $"VoiceChatPlugin.Resources.Locale.{fileName}";

    private static Dictionary<string, string>? LoadFromResource(string fileName)
    {
        var resourceName = GetResourceName(fileName);
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            VoiceChatPluginMain.Logger?.LogError($"[VC] Locale resource not found: {resourceName}");
            // Log all available resource names to help diagnose future issues
            foreach (var name in assembly.GetManifestResourceNames())
                VoiceChatPluginMain.Logger?.LogInfo($"[VC] Available resource: {name}");
            return null;
        }

        try
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger?.LogError($"[VC] Failed to parse locale {fileName}: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, string> GetTable()
    {
        uint lang = (uint)(DataManager.Settings?.Language?.CurrentLanguage ?? 0);

        if (_currentDict != null && _currentLangId == lang)
            return _currentDict;

        _currentLangId = lang;
        string fileName = lang switch
        {
            SChinese => "zh-Hans.json",
            TChinese => "zh-Hant.json",
            _        => "en.json"
        };

        var loaded = LoadFromResource(fileName);
        if (loaded != null)
        {
            _currentDict = loaded;
            return _currentDict;
        }

        // Fallback: empty dict so Tr() returns the key itself (visible to dev, harmless to users)
        _currentDict = new Dictionary<string, string>();
        return _currentDict;
    }

    /// <summary>Returns the localized string for key, or the key itself if not found.</summary>
    public static string Tr(string key)
    {
        var table = GetTable();
        if (table.TryGetValue(key, out var value))
            return value;
        return key;
    }

    /// <summary>Invalidate the cache so the next Tr() call reloads for the current language.</summary>
    public static void Invalidate() => _currentLangId = uint.MaxValue;
}
