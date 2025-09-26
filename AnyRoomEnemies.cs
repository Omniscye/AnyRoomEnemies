using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Empress.InitialSpawnDistributor
{
    [BepInPlugin("dev.empress.initialspawndistributor", "Empress Initial Spawn Distributor", "1.1.3")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> IncludeStartRoom = null!;
        internal static ConfigEntry<Distribution> DistributionMode = null!;
        internal static ConfigEntry<SeedMode> RandomSeedMode = null!;
        internal static ConfigEntry<bool> ForceSeparateGroupRooms = null!;

        internal static System.Random? DeterministicRng;
        internal static Plugin Instance = null!;
        internal Harmony? _harmony;

        public static ManualLogSource Log { get; private set; } = null!;

        private const string AsciiBanner = @"
/*  ██████╗ ███╗   ███╗███╗   ██╗██╗                           */
/* ██╔═══██╗████╗ ████║████╗  ██║██║                           */
/* ██║   ██║██╔████╔██║██╔██╗ ██║██║                           */
/* ██║   ██║██║╚██╔╝██║██║╚██╗██║██║                           */
/* ╚██████╔╝██║ ╚═╝ ██║██║ ╚████║██║                           */
/*  ╚═════╝ ╚═╝     ╚═╝╚═╝  ╚═══╝╚═╝                           */
/*                                                             */
/* ███████╗███╗   ███╗██████╗ ██████╗ ███████╗███████╗███████╗ */
/* ██╔════╝████╗ ████║██╔══██╗██╔══██╗██╔════╝██╔════╝██╔════╝ */
/* █████╗  ██╔████╔██║██████╔╝██████╔╝█████╗  ███████╗███████╗ */
/* ██╔══╝  ██║╚██╔╝██║██╔═══╝ ██╔══██╗██╔══╝  ╚════██║╚════██║ */
/* ███████╗██║ ╚═╝ ██║██║     ██║  ██║███████╗███████║███████║ */
/* ╚══════╝╚═╝     ╚═╝╚═╝     ╚═╝  ╚═╝╚══════╝╚══════╝╚══════╝ */
";

        internal static bool GroupContextActive = false;
        internal static HashSet<object> GroupUsedRooms = new HashSet<object>();

        public enum Distribution { BalancedRooms, UniformPoints }
        public enum SeedMode { UnityRandom, DeterministicLevelSeed }

        private void Awake()
        {
            Instance = this;
            Log = base.Logger;
            Log.LogInfo(AsciiBanner);

            IncludeStartRoom = Config.Bind("General", "IncludeStartRoom", false, "");
            DistributionMode = Config.Bind("General", "DistributionMode", Distribution.BalancedRooms, "");
            RandomSeedMode = Config.Bind("General", "RandomSeedMode", SeedMode.UnityRandom, "");
            ForceSeparateGroupRooms = Config.Bind("Groups", "ForceSeparateGroupRooms", true, "");

            TrySetDeterministicSeed();

            _harmony = new Harmony("dev.empress.initialspawndistributor");
            _harmony.PatchAll(typeof(FirstSpawnPatch));
            _harmony.PatchAll(typeof(EnemySpawnGroupScopePatch));
            Log.LogInfo("[Empress] Initial Spawn Distributor 1.1.3 loaded. Host-only install is enough.");
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        internal static void TrySetDeterministicSeed()
        {
            if (RandomSeedMode.Value != SeedMode.DeterministicLevelSeed) return;
            try
            {
                var lgType = AccessTools.TypeByName("LevelGenerator");
                if (lgType != null)
                {
                    var instProp = AccessTools.Property(lgType, "Instance");
                    var inst = instProp?.GetValue(null);
                    if (inst != null)
                    {
                        var seedField = AccessTools.Field(lgType, "Seed") ?? AccessTools.Field(lgType, "LevelSeed");
                        if (seedField != null)
                        {
                            int seed = (int)seedField.GetValue(inst);
                            DeterministicRng = new System.Random(seed ^ 0x6E6D_5A31);
                            return;
                        }
                    }
                }
            }
            catch { }
            DeterministicRng = new System.Random(1337);
        }

        internal static System.Random GetRng()
        {
            if (RandomSeedMode.Value == SeedMode.DeterministicLevelSeed && DeterministicRng != null)
                return DeterministicRng;
            int salt = Mathf.RoundToInt(UnityEngine.Random.value * int.MaxValue);
            return new System.Random(salt);
        }
    }

    [HarmonyPatch]
    internal static class EnemySpawnGroupScopePatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                AccessTools.TypeByName("LevelGenerator"),
                "EnemySpawn",
                new Type[] { AccessTools.TypeByName("EnemySetup"), typeof(Vector3) }
            );
        }

        static void Prefix()
        {
            if (!Plugin.ForceSeparateGroupRooms.Value) return;
            Plugin.GroupContextActive = true;
            Plugin.GroupUsedRooms.Clear();
        }

        static void Finalizer(Exception __exception)
        {
            Plugin.GroupContextActive = false;
            Plugin.GroupUsedRooms.Clear();
        }
    }

    [HarmonyPatch]
    internal static class FirstSpawnPatch
    {
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("EnemyDirector");
            return AccessTools.Method(type, "FirstSpawnPointAdd", new[] { AccessTools.TypeByName("EnemyParent") });
        }

        static bool Prefix(object __instance, object _enemyParent)
        {
            try
            {
                var semiFunc = AccessTools.TypeByName("SemiFunc");
                var isMaster = AccessTools.Method(semiFunc, "IsMasterClientOrSingleplayer")?.Invoke(null, null) as bool?;
                if (isMaster == false) return true;

                var levelPointType = AccessTools.TypeByName("LevelPoint");
                var roomVolumeType = AccessTools.TypeByName("RoomVolume");
                var enemyParentType = _enemyParent.GetType();

                var levelPoints = (IEnumerable<object>)AccessTools.Method(semiFunc, "LevelPointsGetAll").Invoke(null, null);

                var edType = __instance.GetType();
                var usedListField = AccessTools.Field(edType, "enemyFirstSpawnPoints");
                var usedList = (IList)(usedListField?.GetValue(__instance) ?? CreateGenericList(levelPointType));

                var truckField = AccessTools.Field(levelPointType, "Truck");
                var inStartRoomField = AccessTools.Field(levelPointType, "inStartRoom");
                var roomField = AccessTools.Field(levelPointType, "Room");

                IEnumerable<object> eligible = levelPoints.Where(lp =>
                {
                    bool isTruck = truckField != null && (bool)truckField.GetValue(lp);
                    if (isTruck) return false;
                    if (!Plugin.IncludeStartRoom.Value && inStartRoomField != null && (bool)inStartRoomField.GetValue(lp))
                        return false;
                    return true;
                });

                HashSet<object> usedHash = new HashSet<object>(usedList.Cast<object>());
                var available = eligible.Where(lp => !usedHash.Contains(lp)).ToList();

                if (available.Count == 0)
                {
                    usedList.Clear();
                    usedListField?.SetValue(__instance, usedList);
                    usedHash.Clear();
                    available = eligible.ToList();
                    if (available.Count == 0) return true;
                }

                object chosen;
                Dictionary<object, List<object>> byRoom = null;
                Dictionary<object, int> usedPerRoom = null;
                if (roomField != null && roomVolumeType != null)
                {
                    byRoom = new Dictionary<object, List<object>>();
                    foreach (var lp in available)
                    {
                        var r = roomField.GetValue(lp);
                        if (!byRoom.TryGetValue(r, out var list)) byRoom[r] = list = new List<object>();
                        list.Add(lp);
                    }

                    usedPerRoom = new Dictionary<object, int>();
                    foreach (var u in usedHash)
                    {
                        var r = roomField.GetValue(u);
                        if (!usedPerRoom.ContainsKey(r)) usedPerRoom[r] = 0;
                        usedPerRoom[r]++;
                    }
                }

                var rng = Plugin.GetRng();

                if (Plugin.DistributionMode.Value == Plugin.Distribution.BalancedRooms && byRoom != null && byRoom.Count > 0)
                {
                    List<object> candidateRooms;
                    if (Plugin.GroupContextActive && Plugin.ForceSeparateGroupRooms.Value)
                    {
                        var grpAllowed = byRoom.Keys.Where(r => !Plugin.GroupUsedRooms.Contains(r)).ToList();
                        candidateRooms = grpAllowed.Count > 0 ? grpAllowed : byRoom.Keys.ToList();
                    }
                    else
                    {
                        candidateRooms = byRoom.Keys.ToList();
                    }

                    int minUse = candidateRooms.Select(r => usedPerRoom != null && usedPerRoom.TryGetValue(r, out var c) ? c : 0).DefaultIfEmpty(0).Min();
                    var minUseRooms = candidateRooms.Where(r => (usedPerRoom != null && usedPerRoom.TryGetValue(r, out var c) ? c : 0) == minUse).ToList();

                    int maxAvail = minUseRooms.Select(r => byRoom[r].Count).Max();
                    var finalRooms = minUseRooms.Where(r => byRoom[r].Count == maxAvail).ToList();

                    var roomPick = finalRooms[rng.Next(finalRooms.Count)];
                    var lpList = byRoom[roomPick];
                    chosen = lpList[rng.Next(lpList.Count)];

                    if (Plugin.GroupContextActive && Plugin.ForceSeparateGroupRooms.Value)
                    {
                        Plugin.GroupUsedRooms.Add(roomPick);
                    }
                }
                else
                {
                    chosen = available[rng.Next(available.Count)];
                    if (Plugin.GroupContextActive && Plugin.ForceSeparateGroupRooms.Value && roomField != null)
                    {
                        var chosenRoom = roomField.GetValue(chosen);
                        if (Plugin.GroupUsedRooms.Contains(chosenRoom))
                        {
                            var alt = available.FirstOrDefault(lp => !Plugin.GroupUsedRooms.Contains(roomField.GetValue(lp)));
                            if (alt != null) chosen = alt;
                            Plugin.GroupUsedRooms.Add(roomField.GetValue(chosen));
                        }
                        else
                        {
                            Plugin.GroupUsedRooms.Add(chosenRoom);
                        }
                    }
                }

                var firstSpawnField = AccessTools.Field(enemyParentType, "firstSpawnPoint");
                if (firstSpawnField == null) return true;

                firstSpawnField.SetValue(_enemyParent, chosen);
                usedList.Add(chosen);
                usedListField?.SetValue(__instance, usedList);

                if (usedList.Count >= eligible.Count())
                {
                    usedList.Clear();
                    usedListField?.SetValue(__instance, usedList);
                }

                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Empress] FirstSpawnPointAdd patch failed, falling back to vanilla: {e}");
                return true;
            }
        }

        private static object CreateGenericList(Type t)
        {
            var listType = typeof(List<>).MakeGenericType(t);
            return Activator.CreateInstance(listType);
        }
    }
}