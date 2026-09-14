using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Bassment
{
    [BepInPlugin("xyz.eggstudios.bassment", "Bassment", "0.1.0")]
    public class BassmentPlugin : BaseUnityPlugin
    {
        private const string OldMotdHost = "http://www.againstgrav.com";
        private const string OldPlayersHost = "https://recroom.azurewebsites.net";
        private const string OldTournamentHost = "http://recroom.azurewebsites.net";

        internal static ConfigEntry<string> RevivalBaseUrl;
        internal static ConfigEntry<bool> LogRedirects;
        internal static ConfigEntry<string> PhotonHost;
        internal static ConfigEntry<int> PhotonPort;
        internal static ConfigEntry<string> PhotonApp;

        internal static Harmony HarmonyInstance;
        internal static bool PhotonPatchApplied;

        private void Awake()
        {
            RevivalBaseUrl = Config.Bind("General", "RevivalBaseUrl", "http://X.X.X.X", "");
            LogRedirects = Config.Bind("General", "LogRedirects", true, "");
            PhotonHost = Config.Bind("Photon", "PhotonHost", "X.X.X.X", "");
            PhotonPort = Config.Bind("Photon", "PhotonPort", 5055, "");
            PhotonApp = Config.Bind("Photon", "PhotonApp", "Master", "");

            Logger.LogInfo("[Bassment] redirecting 2016 API to " + RevivalBaseUrl.Value.TrimEnd('/'));

            Harmony harmony = new Harmony("xyz.eggstudios.bassment");
            HarmonyInstance = harmony;
            harmony.PatchAll(typeof(WWWPatches));
            harmony.PatchAll(typeof(ObjectiveTrackerPatch));
            harmony.PatchAll(typeof(DailyObjectivesPatch));
            harmony.PatchAll(typeof(PhotonEnsurePatch));
            Logger.LogInfo("[Bassment] patches applied.");
        }

        internal static string RewriteUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return url;
            }

            string target = RevivalBaseUrl.Value.TrimEnd('/');
            string[] olds = new string[] { OldMotdHost, OldPlayersHost, OldTournamentHost };

            foreach (string old in olds)
            {
                if (url.StartsWith(old, StringComparison.OrdinalIgnoreCase))
                {
                    string rewritten = target + url.Substring(old.Length);
                    if (LogRedirects.Value)
                    {
                        Debug.Log("[Bassment] " + url + " -> " + rewritten);
                    }
                    return rewritten;
                }
            }

            return url;
        }
    }

    [HarmonyPatch]
    internal static class WWWPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (ConstructorInfo ctor in typeof(WWW).GetConstructors())
            {
                ParameterInfo[] ps = ctor.GetParameters();
                if (ps.Length > 0 && ps[0].ParameterType == typeof(string)) 
                {
                    yield return ctor;
                }
            }
        }

        private static void Prefix(ref string url)
        {
            url = BassmentPlugin.RewriteUrl(url);
        }
    }

    [HarmonyPatch(typeof(PlayerObjectiveTracker), "GetAllObjectivesCompleted")]
    internal static class ObjectiveTrackerPatch
    {
        private static MethodInfo getTodaysObjectives;

        private static bool Prefix(PlayerObjectiveTracker __instance, ref bool __result)
        {
            if (getTodaysObjectives == null)
            {
                getTodaysObjectives = AccessTools.Method(typeof(PlayerObjectiveTracker), "GetTodaysObjectives");
            }
            object todaysObjectives = getTodaysObjectives.Invoke(__instance, null);
            if (todaysObjectives == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerObjectiveTracker), "GetTodaysObjectives")]
    internal static class DailyObjectivesPatch
    {
        private static FieldInfo dailyField;
        private static FieldInfo descriptionField;
        private static FieldInfo dailyYearField;
        private static FieldInfo dailyMonthField;
        private static FieldInfo dailyDateField;
        private static FieldInfo dailyObjectivesField;
        private static FieldInfo objectiveTypeField;
        private static FieldInfo objectiveScoreField;
        private static FieldInfo objectiveXpField;
        private static FieldInfo descriptionTypeField;
        private static Type dailyType;
        private static Type objectiveType;
        private static Type objectiveEnumType;
        private static bool poolBuilt;
        private static int[] poolTypes;
        private static int[] poolScores;
        private static int[] poolXp;

        private static bool Prefix(PlayerObjectiveTracker __instance)
        {
            EnsureReflection();
            if (dailyField == null)
            {
                return true;
            }
            Array entries = (Array)dailyField.GetValue(__instance);
            if (entries == null || entries.Length == 0)
            {
                return true;
            }
            if (!poolBuilt)
            {
                BuildPool(__instance, entries);
            }
            if (poolTypes == null || poolTypes.Length < 3)
            {
                return true;
            }
            DateTime today = DateTime.Today;
            if (HasEntryFor(entries, today))
            {
                return true;
            }
            entries.SetValue(BuildTodays(today), 0);
            Debug.Log("[Bassment] daily objectives generated for " + today.ToString("yyyy-MM-dd"));
            return true;
        }

        private static void EnsureReflection()
        {
            if (dailyField != null)
            {
                return;
            }
            dailyField = AccessTools.Field(typeof(PlayerObjectiveTracker), "dailyObjectives");
            descriptionField = AccessTools.Field(typeof(PlayerObjectiveTracker), "objectiveTypeDescriptions");
            dailyType = dailyField.FieldType.GetElementType();
            dailyYearField = dailyType.GetField("Year");
            dailyMonthField = dailyType.GetField("Month");
            dailyDateField = dailyType.GetField("Date");
            dailyObjectivesField = dailyType.GetField("Objectives");
            objectiveType = dailyObjectivesField.FieldType.GetElementType();
            objectiveTypeField = objectiveType.GetField("ObjectiveType");
            objectiveEnumType = objectiveTypeField.FieldType;
            objectiveScoreField = objectiveType.GetField("RequiredScore");
            objectiveXpField = objectiveType.GetField("Xp");
            descriptionTypeField = descriptionField.FieldType.GetElementType().GetField("ObjectiveType");
        }

        private static void BuildPool(PlayerObjectiveTracker instance, Array entries)
        {
            List<int> described = new List<int>();
            Array descriptions = (Array)descriptionField.GetValue(instance);
            if (descriptions != null)
            {
                foreach (object description in descriptions)
                {
                    if (description != null)
                    {
                        described.Add(Convert.ToInt32(descriptionTypeField.GetValue(description)));
                    }
                }
            }
            List<int> types = new List<int>();
            List<int> scores = new List<int>();
            List<int> xps = new List<int>();
            foreach (object entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }
                Array objectives = (Array)dailyObjectivesField.GetValue(entry);
                if (objectives == null)
                {
                    continue;
                }
                foreach (object objective in objectives)
                {
                    if (objective == null)
                    {
                        continue;
                    }
                    int type = Convert.ToInt32(objectiveTypeField.GetValue(objective));
                    if (types.Contains(type))
                    {
                        continue;
                    }
                    if (described.Count > 0 && !described.Contains(type))
                    {
                        continue;
                    }
                    types.Add(type);
                    scores.Add(Convert.ToInt32(objectiveScoreField.GetValue(objective)));
                    xps.Add(Convert.ToInt32(objectiveXpField.GetValue(objective)));
                }
            }
            foreach (int type in described)
            {
                if (types.Count >= 3)
                {
                    break;
                }
                if (!types.Contains(type))
                {
                    types.Add(type);
                    scores.Add(3);
                    xps.Add(100);
                }
            }
            poolTypes = types.ToArray();
            poolScores = scores.ToArray();
            poolXp = xps.ToArray();
            poolBuilt = true;
        }

        private static bool HasEntryFor(Array entries, DateTime today)
        {
            foreach (object entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }
                int year = (int)dailyYearField.GetValue(entry);
                int month = (int)dailyMonthField.GetValue(entry);
                int day = (int)dailyDateField.GetValue(entry);
                if (year == today.Year && month == today.Month && day == today.Day)
                {
                    return true;
                }
            }
            return false;
        }

        private static object BuildTodays(DateTime today)
        {
            int seed = today.Year * 10000 + today.Month * 100 + today.Day;
            System.Random random = new System.Random(seed);
            int count = poolTypes.Length;
            int[] order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }
            for (int i = count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int swap = order[i];
                order[i] = order[j];
                order[j] = swap;
            }
            object daily = Activator.CreateInstance(dailyType, true);
            dailyYearField.SetValue(daily, today.Year);
            dailyMonthField.SetValue(daily, today.Month);
            dailyDateField.SetValue(daily, today.Day);
            Array objectives = Array.CreateInstance(objectiveType, 3);
            for (int i = 0; i < 3; i++)
            {
                int pick = order[i];
                object objective = Activator.CreateInstance(objectiveType, true);
                objectiveTypeField.SetValue(objective, Enum.ToObject(objectiveEnumType, poolTypes[pick]));
                objectiveScoreField.SetValue(objective, poolScores[pick]);
                objectiveXpField.SetValue(objective, poolXp[pick]);
                objectives.SetValue(objective, i);
            }
            dailyObjectivesField.SetValue(daily, objectives);
            return daily;
        }
    }

    [HarmonyPatch(typeof(PhotonNetwork), "ConnectUsingSettings", new Type[] { typeof(string) })]
    internal static class PhotonConnectPatch
    {
        private static string cachedUserId;

        private static void Prefix()
        {
            ServerSettings settings = PhotonNetwork.PhotonServerSettings;
            if (settings == null) 
            {
                return;
            }
            settings.UseMyServer(BassmentPlugin.PhotonHost.Value, BassmentPlugin.PhotonPort.Value, BassmentPlugin.PhotonApp.Value);
            PhotonNetwork.AuthValues = new AuthenticationValues(BuildUserId());
            Debug.Log("[Bassment] photon self-hosted: " + BassmentPlugin.PhotonHost.Value + ":" + BassmentPlugin.PhotonPort.Value);
        }

        private static string BuildUserId()
        {
            if (!string.IsNullOrEmpty(cachedUserId))
            {
                return cachedUserId;
            }
            cachedUserId = ResolveUserId();
            return cachedUserId;
        }

        private static string ResolveUserId()
        {
            try
            {
                CSteamID steamId = SteamUser.GetSteamID();
                if (steamId.IsValid())
                {
                    return steamId.m_SteamID.ToString();
                }
            }
            catch
            {
            }
            try
            {
                if (Player.LocalPlayer != null && Player.LocalPlayer.PlatformId != 0)
                {
                    return Player.LocalPlayer.PlatformId.ToString();
                }
            }
            catch
            {
            }
            return SystemInfo.deviceUniqueIdentifier;
        }
    }

    [HarmonyPatch(typeof(PUNNetworkManager), "InitializeNewConnection")]
    internal static class PhotonEnsurePatch
    {
        private static void Prefix()
        {
            if (BassmentPlugin.PhotonPatchApplied)
            {
                return;
            }
            BassmentPlugin.PhotonPatchApplied = true;
            try
            {
                BassmentPlugin.HarmonyInstance.PatchAll(typeof(PhotonConnectPatch));
                Debug.Log("[Bassment] photon patch applied.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Bassment] photon patch failed: " + ex);
            }
        }
    }
}
