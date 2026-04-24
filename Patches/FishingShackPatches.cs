using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Reflection;
using WardenOfTheWilds.Components;

// ---------------------------------------------------------------------------
//  FishingShackPatches
//  Harmony patches for the Fishing Shack Angler/Creeler mode system.
//
//  Patches:
//    1. GetNumFishCaught (Postfix)
//       Multiplies the per-catch fish count based on the shack's current mode.
//       Full Angler = x1.5, Mixed = x1.25, Full Creeler = x0.5.
//       This is the authoritative hook for rod-fishing output — it affects
//       both the actual fish items AND the tally.
//
//    2. FishFromShoreSubTask constructor (Postfix) [optional]
//       Modifies timer and capacity fields for Angler mode.
//       Faster timer (12-20s vs 20-30s), higher carry capacity (40 vs 25).
//       Falls back gracefully if the subtask class isn't found.
//
//    3. CreateFishingAreas (Postfix) [optional]
//       Triggers fishing area recreation when radius changes (Creeler mode).
// ---------------------------------------------------------------------------

namespace WardenOfTheWilds.Patches
{
    public static class FishingShackPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // Cached type reference for subtask timer modification
        private static FieldInfo _timerMinField = null;
        private static FieldInfo _timerMaxField = null;
        private static FieldInfo _capacityField = null;
        private static bool _subtaskFieldsSearched = false;

        // -- Manual patch application ---------------------------------------
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

            Type fishType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                fishType = asm.GetType("FishingShack") ?? asm.GetType("FishermanShack");
                if (fishType != null) break;
            }

            if (fishType == null)
            {
                MelonLogger.Warning(
                    "[WotW] FishingShackPatches: FishingShack type not found.");
                return;
            }

            // -- Patch 1: GetNumFishCaught on FishFromShoreSubTask (Postfix) --
            // GetNumFishCaught is declared on the FISHING SUBTASK (not the shack).
            // The subtask has a `fishingShack` field we use to find the mode.
            Type subtaskTypeForCatch = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                subtaskTypeForCatch = asm.GetType("FishFromShoreSubTask");
                if (subtaskTypeForCatch != null) break;
            }
            MethodInfo getNumMethod = null;
            if (subtaskTypeForCatch != null)
            {
                getNumMethod = subtaskTypeForCatch.GetMethod("GetNumFishCaught", AllInstance);
            }
            if (getNumMethod != null)
            {
                harmony.Patch(getNumMethod,
                    postfix: new HarmonyMethod(
                        typeof(FishingShackPatches).GetMethod(
                            nameof(GetNumFishCaughtPostfix), AllStatic)));
                MelonLogger.Msg(
                    "[WotW] Patched FishFromShoreSubTask.GetNumFishCaught (Angler output mult)");
            }
            else
            {
                MelonLogger.Warning(
                    "[WotW] FishingShackPatches: FishFromShoreSubTask.GetNumFishCaught not found. " +
                    "Angler output multiplier will not apply.");
            }

            // -- Patch 2: TallyFishCaught (Prefix) — kept for stats --------
            var tallyMethod = fishType.GetMethod("TallyFishCaught", AllInstance);
            if (tallyMethod != null)
            {
                harmony.Patch(tallyMethod,
                    prefix: new HarmonyMethod(
                        typeof(FishingShackPatches).GetMethod(
                            nameof(TallyFishCaughtPrefix), AllStatic)));
                MelonLogger.Msg(
                    "[WotW] Patched FishingShack.TallyFishCaught (stats tracking)");
            }

            // -- Patch 3: FishFromShoreSubTask timer/capacity ---------------
            // Optional — modifies timer and carry capacity for Angler workers.
            // If the subtask class isn't found, Angler still works via output mult.
            TryPatchSubtaskTimer(harmony);

            MelonLogger.Msg(
                "[WotW] FishingShackPatches applied " +
                $"(GetNumFishCaught={getNumMethod != null}, " +
                $"TallyFishCaught={tallyMethod != null}).");
        }

        // Cached field info for the subtask's fishingShack reference.
        private static FieldInfo _subtaskFishingShackField = null;
        private static bool _subtaskFishingShackFieldSearched = false;

        // -- Patch 1: GetNumFishCaught postfix ------------------------------
        /// <summary>
        /// Multiplies the per-catch fish count based on the owning shack's mode.
        /// __instance is a FishFromShoreSubTask; its `fishingShack` field holds
        /// the FishingShack we look up the mode on. Return value drives both
        /// the fish items added and TallyFishCaught — single authoritative hook.
        /// </summary>
        public static void GetNumFishCaughtPostfix(object __instance, ref int __result)
        {
            try
            {
                if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;
                if (__result <= 0 || __instance == null) return;

                // Resolve the subtask's fishingShack field once per session.
                if (!_subtaskFishingShackFieldSearched)
                {
                    _subtaskFishingShackFieldSearched = true;
                    _subtaskFishingShackField = __instance.GetType()
                        .GetField("fishingShack", AllInstance);
                }
                if (_subtaskFishingShackField == null) return;

                var shack = _subtaskFishingShackField.GetValue(__instance) as Component;
                if (shack == null) return;

                var enhancement = shack.GetComponent<FishingShackEnhancement>();
                if (enhancement == null) return;

                float mult = enhancement.GetAnglerOutputMult();
                if (Math.Abs(mult - 1f) < 0.01f) return; // No change

                int original = __result;
                __result = (int)Math.Max(1, Math.Round(__result * mult));

                // Occasional log (not every catch — would spam)
                if (UnityEngine.Random.value < 0.1f)
                {
                    MelonLogger.Msg(
                        $"[WotW] Fishing '{shack.gameObject.name}' mode={enhancement.Mode}: " +
                        $"catch {original} -> {__result} (x{mult:F2})");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] GetNumFishCaughtPostfix: {ex.Message}");
            }
        }

        // -- Patch 2: TallyFishCaught prefix --------------------------------
        /// <summary>
        /// Lightweight prefix on TallyFishCaught — logs catch events for
        /// diagnostics. The actual output multiplication is in GetNumFishCaught.
        /// </summary>
        public static void TallyFishCaughtPrefix(object __instance, ref uint numFishCaught)
        {
            try
            {
                if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

                // Log every catch for balance tuning (can be disabled later)
                var comp = __instance as Component;
                if (comp == null) return;

                var enhancement = comp.GetComponent<FishingShackEnhancement>();
                if (enhancement == null) return;

                // The numFishCaught is already modified by GetNumFishCaught postfix
                // (that runs before the fish reach this point in the pipeline).
                // We just log for diagnostic purposes.
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TallyFishCaughtPrefix: {ex.Message}");
            }
        }

        // -- Patch 3: FishFromShoreSubTask timer modification ---------------
        /// <summary>
        /// Attempts to patch the FishFromShoreSubTask constructor to modify
        /// timer and capacity fields for Angler mode. Falls back gracefully.
        /// </summary>
        private static void TryPatchSubtaskTimer(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type subtaskType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    subtaskType = asm.GetType("FishFromShoreSubTask");
                    if (subtaskType != null) break;
                }

                if (subtaskType == null)
                {
                    MelonLogger.Msg(
                        "[WotW] FishFromShoreSubTask not found — " +
                        "Angler timer boost unavailable (output mult still works).");
                    return;
                }

                // Cache field references for the postfix
                _timerMinField = subtaskType.GetField("timeBetweenFishMin", AllInstance)
                              ?? subtaskType.GetField("_timeBetweenFishMin", AllInstance);
                _timerMaxField = subtaskType.GetField("timeBetweenFishMax", AllInstance)
                              ?? subtaskType.GetField("_timeBetweenFishMax", AllInstance);
                _capacityField = subtaskType.GetField("fishCapacity", AllInstance)
                              ?? subtaskType.GetField("_fishCapacity", AllInstance);
                _subtaskFieldsSearched = true;

                if (_timerMinField == null && _timerMaxField == null && _capacityField == null)
                {
                    MelonLogger.Msg(
                        "[WotW] FishFromShoreSubTask timer/capacity fields not found. " +
                        "Dumping fields for investigation:");
                    foreach (var f in subtaskType.GetFields(AllInstance))
                        MelonLogger.Msg($"[WotW]   field: {f.Name} ({f.FieldType.Name})");
                    return;
                }

                // Find a constructor to patch
                var ctors = subtaskType.GetConstructors(AllInstance);
                if (ctors.Length == 0)
                {
                    MelonLogger.Warning(
                        "[WotW] FishFromShoreSubTask has no constructors to patch.");
                    return;
                }

                // Patch the first constructor with our postfix
                harmony.Patch(ctors[0],
                    postfix: new HarmonyMethod(
                        typeof(FishingShackPatches).GetMethod(
                            nameof(SubtaskConstructorPostfix), AllStatic)));

                MelonLogger.Msg(
                    $"[WotW] Patched FishFromShoreSubTask constructor " +
                    $"(timerMin={_timerMinField != null}, timerMax={_timerMaxField != null}, " +
                    $"capacity={_capacityField != null})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] TryPatchSubtaskTimer: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on FishFromShoreSubTask constructor.
        /// Modifies timer and capacity fields when the owning shack is in Angler mode.
        /// </summary>
        public static void SubtaskConstructorPostfix(object __instance, object[] __args)
        {
            try
            {
                if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;
                if (!_subtaskFieldsSearched) return;

                // The subtask constructor typically takes (FishTask, FishingShack).
                // Find the FishingShack argument.
                FishingShackEnhancement enhancement = null;
                foreach (object arg in __args)
                {
                    var comp = arg as Component;
                    if (comp != null)
                    {
                        enhancement = comp.GetComponent<FishingShackEnhancement>();
                        if (enhancement != null) break;
                    }
                }

                if (enhancement == null) return;

                // Only modify timer/capacity for Angler slots
                if (enhancement.AnglerSlots <= 0) return;

                // Angler timer: reduce by AnglerTimerReduction (default 0.65 = 35% faster)
                float timerMult = WardenOfTheWildsMod.AnglerTimerReduction.Value;
                if (_timerMinField != null)
                {
                    float current = (float)_timerMinField.GetValue(__instance);
                    _timerMinField.SetValue(__instance, current * timerMult);
                }
                if (_timerMaxField != null)
                {
                    float current = (float)_timerMaxField.GetValue(__instance);
                    _timerMaxField.SetValue(__instance, current * timerMult);
                }

                // Angler capacity: add bonus carry capacity
                int capBonus = WardenOfTheWildsMod.AnglerCapacityBonus.Value;
                if (_capacityField != null && capBonus > 0)
                {
                    if (_capacityField.FieldType == typeof(int))
                    {
                        int current = (int)_capacityField.GetValue(__instance);
                        _capacityField.SetValue(__instance, current + capBonus);
                    }
                    else if (_capacityField.FieldType == typeof(uint))
                    {
                        uint current = (uint)_capacityField.GetValue(__instance);
                        _capacityField.SetValue(__instance, current + (uint)capBonus);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] SubtaskConstructorPostfix: {ex.Message}");
            }
        }
    }
}
