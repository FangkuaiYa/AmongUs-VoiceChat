using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Interstellar.Voice;

public static class TranslationHelper
{
    private const string BlankText = "[BLANK]";
    private const int DefaultLanguage = 0;

    private static Dictionary<string, Dictionary<int, string>>? _stringData;

    public static void Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Interstellar.Resources.stringData.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            InterstellarPlugin.Logger?.LogWarning("[VC] Translation: stringData.json not found.");
            return;
        }

        var bytes = new byte[stream.Length];
        stream.Read(bytes, 0, (int)stream.Length);
        var json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

        _stringData = new Dictionary<string, Dictionary<int, string>>();

        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var key = prop.Name;
            var translations = new Dictionary<int, string>();
            foreach (var langEntry in prop.Value.EnumerateObject())
            {
                if (int.TryParse(langEntry.Name, out var langId))
                {
                    var text = langEntry.Value.GetString();
                    if (text == BlankText)
                        translations[langId] = "";
                    else if (!string.IsNullOrEmpty(text))
                        translations[langId] = text;
                }
            }
            if (translations.Count > 0)
                _stringData[key] = translations;
        }

        InterstellarPlugin.Logger?.LogInfo($"[VC] Translation: loaded {_stringData.Count} keys.");
    }

    public static string Get(string key, string? defaultText = null)
    {
        defaultText ??= key;
        if (_stringData == null) return defaultText;

        var keyClean = Regex.Replace(key, "<.*?>", "");
        keyClean = Regex.Replace(keyClean, @"^-\s*", "");
        keyClean = keyClean.Trim();

        if (!_stringData.TryGetValue(keyClean, out var translations))
            return defaultText;

        var lang = (int)AmongUs.Data.DataManager.Settings.Language.CurrentLanguage;

        if (translations.TryGetValue(lang, out var translated))
            return translated;

        // Fallback to English
        if (translations.TryGetValue(DefaultLanguage, out var english))
            return english;

        return defaultText;
    }

    /// <summary>
    /// Gets the translated string, formatting it with the given arguments.
    /// </summary>
    public static string Get(string key, string defaultText, params object[] args)
        => string.Format(Get(key, defaultText), args);
}
