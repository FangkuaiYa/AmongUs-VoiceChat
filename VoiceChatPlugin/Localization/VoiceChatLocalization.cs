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

	private static string GetResourceName(string fileName)
	{
		return $"VoiceChatPlugin.Locale.{fileName}";
	}

	private static Dictionary<string, string>? LoadFromResource(string fileName)
	{
		var resourceName = GetResourceName(fileName);
		var assembly = Assembly.GetExecutingAssembly();
		using var stream = assembly.GetManifestResourceStream(resourceName);
		if (stream == null)
		{
			VoiceChatPluginMain.Logger?.LogError($"Locale resource not found: {resourceName}");
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
			VoiceChatPluginMain.Logger?.LogError($"Failed to parse locale {fileName}: {ex.Message}");
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
			_ => "en.json"
		};

		var loaded = LoadFromResource(fileName);
		if (loaded != null)
		{
			_currentDict = loaded;
			return _currentDict;
		}

		_currentDict = new Dictionary<string, string>();
		return _currentDict;
	}

	public static string Tr(string key)
	{
		var table = GetTable();
		if (table.TryGetValue(key, out var value))
			return value;
		return key;
	}
}
