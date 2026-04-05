using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using VoiceChatPlugin.Reactor;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin
{
	[HarmonyPatch]
	internal class Options
	{
		private static StringNames voiceChatCategoryName;
		private static StringNames wallsBlockSoundName;
		private static StringNames onlyHearInSightName;
		private static StringNames impostorHearGhostsName;
		private static StringNames hearInVentName;
		private static StringNames ventPrivateChatName;
		private static StringNames commsSabDisablesName;
		private static StringNames cameraCanHearName;
		private static StringNames impostorPrivateRadioName;
		private static StringNames onlyGhostsCanTalkName;
		private static StringNames onlyMeetingOrLobbyName;
		private static StringNames maxDistanceName;

		private static BoolOptionNames wallsBlockSoundBool;
		private static BoolOptionNames onlyHearInSightBool;
		private static BoolOptionNames impostorHearGhostsBool;
		private static BoolOptionNames hearInVentBool;
		private static BoolOptionNames ventPrivateChatBool;
		private static BoolOptionNames commsSabDisablesBool;
		private static BoolOptionNames cameraCanHearBool;
		private static BoolOptionNames impostorPrivateRadioBool;
		private static BoolOptionNames onlyGhostsCanTalkBool;
		private static BoolOptionNames onlyMeetingOrLobbyBool;
		private static FloatOptionNames maxDistanceFloat;

		private static RulesCategory voiceChatCategory;
		private static bool settingsInjected = false;

		static Options()
		{
			InitializeLocalization();
		}

		public static void InitializeLocalization()
		{
			voiceChatCategoryName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("header"));
			wallsBlockSoundName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("wallsBlockSound"));
			onlyHearInSightName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("onlyHearInSight"));
			impostorHearGhostsName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("impostorHearGhosts"));
			hearInVentName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("hearInVent"));
			ventPrivateChatName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("ventPrivateChat"));
			commsSabDisablesName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("commsSabDisables"));
			cameraCanHearName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("cameraCanHear"));
			impostorPrivateRadioName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("impostorPrivateRadio"));
			onlyGhostsCanTalkName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("onlyGhostsCanTalk"));
			onlyMeetingOrLobbyName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("onlyMeetingOrLobby"));
			maxDistanceName = CustomStringName.CreateAndRegister(VoiceChatLocalization.Tr("maxDistance"));
		}

		public static void SetupCustomSettings()
		{
			int boolCount = Enum.GetValues<BoolOptionNames>().Length;
			wallsBlockSoundBool = (BoolOptionNames)boolCount++;
			onlyHearInSightBool = (BoolOptionNames)boolCount++;
			impostorHearGhostsBool = (BoolOptionNames)boolCount++;
			hearInVentBool = (BoolOptionNames)boolCount++;
			ventPrivateChatBool = (BoolOptionNames)boolCount++;
			commsSabDisablesBool = (BoolOptionNames)boolCount++;
			cameraCanHearBool = (BoolOptionNames)boolCount++;
			impostorPrivateRadioBool = (BoolOptionNames)boolCount++;
			onlyGhostsCanTalkBool = (BoolOptionNames)boolCount++;
			onlyMeetingOrLobbyBool = (BoolOptionNames)boolCount++;

			var boolDict = new Dictionary<string, object>
			{
				{ "WallsBlockSound", wallsBlockSoundBool },
				{ "OnlyHearInSight", onlyHearInSightBool },
				{ "ImpostorHearGhosts", impostorHearGhostsBool },
				{ "HearInVent", hearInVentBool },
				{ "VentPrivateChat", ventPrivateChatBool },
				{ "CommsSabDisables", commsSabDisablesBool },
				{ "CameraCanHear", cameraCanHearBool },
				{ "ImpostorPrivateRadio", impostorPrivateRadioBool },
				{ "OnlyGhostsCanTalk", onlyGhostsCanTalkBool },
				{ "OnlyMeetingOrLobby", onlyMeetingOrLobbyBool }
			};
			EnumInjector.InjectEnumValues<BoolOptionNames>(boolDict);

			int floatCount = Enum.GetValues<FloatOptionNames>().Length;
			maxDistanceFloat = (FloatOptionNames)floatCount++;
			var floatDict = new Dictionary<string, object> { { "MaxChatDistance", maxDistanceFloat } };
			EnumInjector.InjectEnumValues<FloatOptionNames>(floatDict);
		}

		[HarmonyPatch(typeof(IGameOptionsExtensions), nameof(IGameOptionsExtensions.GetValue))]
		[HarmonyPostfix]
		static void GetValuePatch(IGameOptions gameOptions, BaseGameSetting data, ref float __result)
		{
			if (data.Type == OptionTypes.Checkbox && data.TryCast<CheckboxGameSetting>() != null)
			{
				var optName = data.Cast<CheckboxGameSetting>().OptionName;
				if (optName == wallsBlockSoundBool)
					__result = VoiceChatConfig.HostWallsBlockSound ? 1f : 0f;
				else if (optName == onlyHearInSightBool)
					__result = VoiceChatConfig.HostOnlyHearInSight ? 1f : 0f;
				else if (optName == impostorHearGhostsBool)
					__result = VoiceChatConfig.HostImpostorHearGhosts ? 1f : 0f;
				else if (optName == hearInVentBool)
					__result = VoiceChatConfig.HostHearInVent ? 1f : 0f;
				else if (optName == ventPrivateChatBool)
					__result = VoiceChatConfig.HostVentPrivateChat ? 1f : 0f;
				else if (optName == commsSabDisablesBool)
					__result = VoiceChatConfig.HostCommsSabDisables ? 1f : 0f;
				else if (optName == cameraCanHearBool)
					__result = VoiceChatConfig.HostCameraCanHear ? 1f : 0f;
				else if (optName == impostorPrivateRadioBool)
					__result = VoiceChatConfig.HostImpostorPrivateRadio ? 1f : 0f;
				else if (optName == onlyGhostsCanTalkBool)
					__result = VoiceChatConfig.HostOnlyGhostsCanTalk ? 1f : 0f;
				else if (optName == onlyMeetingOrLobbyBool)
					__result = VoiceChatConfig.HostOnlyMeetingOrLobby ? 1f : 0f;
			}
			else if (data.Type == OptionTypes.Float && data.TryCast<FloatGameSetting>() != null)
			{
				if (data.Cast<FloatGameSetting>().OptionName == maxDistanceFloat)
					__result = VoiceChatConfig.HostMaxChatDistance;
			}
		}

		[HarmonyPatch(typeof(NormalGameOptionsV10), nameof(NormalGameOptionsV10.SetBool))]
		[HarmonyPrefix]
		static bool SetBoolPatch(NormalGameOptionsV10 __instance, BoolOptionNames optionName, bool value)
		{
			if (!AmongUsClient.Instance.AmHost) return true;

			if (optionName == wallsBlockSoundBool)
				VoiceChatConfig.SetHostWallsBlockSound(value);
			else if (optionName == onlyHearInSightBool)
				VoiceChatConfig.SetHostOnlyHearInSight(value);
			else if (optionName == impostorHearGhostsBool)
				VoiceChatConfig.SetHostImpostorHearGhosts(value);
			else if (optionName == hearInVentBool)
				VoiceChatConfig.SetHostHearInVent(value);
			else if (optionName == ventPrivateChatBool)
				VoiceChatConfig.SetHostVentPrivateChat(value);
			else if (optionName == commsSabDisablesBool)
				VoiceChatConfig.SetHostCommsSabDisables(value);
			else if (optionName == cameraCanHearBool)
				VoiceChatConfig.SetHostCameraCanHear(value);
			else if (optionName == impostorPrivateRadioBool)
				VoiceChatConfig.SetHostImpostorPrivateRadio(value);
			else if (optionName == onlyGhostsCanTalkBool)
				VoiceChatConfig.SetHostOnlyGhostsCanTalk(value);
			else if (optionName == onlyMeetingOrLobbyBool)
				VoiceChatConfig.SetHostOnlyMeetingOrLobby(value);
			else
				return true;

			VoiceChatConfig.ApplyLocalHostSettingsToSynced();
			VoiceChatRoomSettings.SendToAll(VoiceChatConfig.SyncedRoomSettings);
			VoiceChatPatches.MarkRoomSettingsDirty();
			return false;
		}

		[HarmonyPatch(typeof(NormalGameOptionsV10), nameof(NormalGameOptionsV10.SetFloat))]
		[HarmonyPrefix]
		static bool SetFloatPatch(NormalGameOptionsV10 __instance, FloatOptionNames optionName, float value)
		{
			if (!AmongUsClient.Instance.AmHost) return true;

			if (optionName == maxDistanceFloat)
			{
				value = Mathf.Clamp(value, 1.5f, 20f);
				VoiceChatConfig.SetHostMaxChatDistance(value);
				VoiceChatConfig.ApplyLocalHostSettingsToSynced();
				VoiceChatRoomSettings.SendToAll(VoiceChatConfig.SyncedRoomSettings);
				VoiceChatPatches.MarkRoomSettingsDirty();
				return false;
			}
			return true;
		}

		[HarmonyPatch(typeof(GameManagerCreator), nameof(GameManagerCreator.Awake))]
		[HarmonyPostfix]
		static void GameManagerCreatorPatch(GameManagerCreator __instance)
		{
			if (settingsInjected) return;
			settingsInjected = true;

			var allSettings = new Il2CppSystem.Collections.Generic.List<BaseGameSetting>();

			allSettings.Add(CreateCheckbox(wallsBlockSoundBool, wallsBlockSoundName));
			allSettings.Add(CreateCheckbox(onlyHearInSightBool, onlyHearInSightName));
			allSettings.Add(CreateCheckbox(impostorHearGhostsBool, impostorHearGhostsName));
			allSettings.Add(CreateCheckbox(hearInVentBool, hearInVentName));
			allSettings.Add(CreateCheckbox(ventPrivateChatBool, ventPrivateChatName));
			allSettings.Add(CreateCheckbox(commsSabDisablesBool, commsSabDisablesName));
			allSettings.Add(CreateCheckbox(cameraCanHearBool, cameraCanHearName));
			allSettings.Add(CreateCheckbox(impostorPrivateRadioBool, impostorPrivateRadioName));
			allSettings.Add(CreateCheckbox(onlyGhostsCanTalkBool, onlyGhostsCanTalkName));
			allSettings.Add(CreateCheckbox(onlyMeetingOrLobbyBool, onlyMeetingOrLobbyName));

			var distanceFloat = ScriptableObject.CreateInstance<FloatGameSetting>();
			distanceFloat.Title = maxDistanceName;
			distanceFloat.OptionName = maxDistanceFloat;
			distanceFloat.Type = OptionTypes.Float;
			distanceFloat.name = "Max Chat Distance";
			distanceFloat.Increment = 0.5f;
			distanceFloat.FormatString = "0.0";
			distanceFloat.SuffixType = NumberSuffixes.None;
			distanceFloat.ZeroIsInfinity = false;
			distanceFloat.ValidRange = new FloatRange(1.5f, 20f);
			distanceFloat.Value = VoiceChatConfig.HostMaxChatDistance;
			allSettings.Add(distanceFloat);

			voiceChatCategory = new RulesCategory
			{
				AllGameSettings = allSettings,
				CategoryName = voiceChatCategoryName
			};

			dynamic allCategories = __instance.NormalGameManagerPrefab.gameSettingsList.AllCategories;
			allCategories.System_Collections_IList_Add(voiceChatCategory);
		}

		[HarmonyPatch(typeof(LobbyViewSettingsPane), nameof(LobbyViewSettingsPane.ChangeTab))]
		[HarmonyPostfix]
		static void LobbyViewSettingsPaneChangeTabPatch(LobbyViewSettingsPane __instance)
		{
			if (voiceChatCategory == null) return;

			int numOptions = voiceChatCategory.AllGameSettings.Count;
			int rows = (numOptions + 1) / 2;
			const float headerHeight = 1.05f;
			const float rowHeight = 0.85f;
			const float trailingGap = 0.85f;
			float extraHeight = headerHeight + rows * rowHeight + trailingGap;

			__instance.scrollBar.SetYBoundsMax(__instance.scrollBar.ContentYBounds.max + extraHeight);
		}

		private static CheckboxGameSetting CreateCheckbox(BoolOptionNames optionName, StringNames title)
		{
			var cb = ScriptableObject.CreateInstance<CheckboxGameSetting>();
			cb.Title = title;
			cb.OptionName = optionName;
			cb.Type = OptionTypes.Checkbox;
			cb.name = title.ToString();
			return cb;
		}
	}
}