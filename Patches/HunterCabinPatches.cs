using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using WardenOfTheWilds.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  HunterCabinPatches
//  Harmony patches for Hunter Cabin / Hunter Lodge.
//
//  STATUS: STUBS — awaiting Assembly-CSharp.dll decompile results.
//  All patch targets are marked with TODO comments.
//  Structure and logic are complete; only target method names need confirming.
//
//  Patches planned:
//    1. OnHuntCompleted (Postfix)
//       → Read HunterCabinEnhancement state
//       → For TrapperLodge: multiply pelt/tallow output
//       → For HuntingLodge: multiply meat output slightly
//       → TODO: confirm method name (candidates: OnHuntCompleted, FinishHunt,
//                CompleteWork, OnWorkComplete, HarvestAnimal)
//
//    2. GetTrapCount / SetTrapCount (Prefix or Postfix)
//       → When path = HuntingLodge, force trap count to 0
//       → Prevents the "traps steal hunter time" regression on Lodge upgrade
//       → TODO: confirm method/field name (candidates: numTraps, trapCount,
//                GetNumTraps, UpdateTraps)
//
//    3. Building.SetBuildingDataRecordName (Postfix)
//       → Rename building to "Trapper Lodge" or "Hunting Lodge" at T2
//       → Mirrors Tended Wilds DisplayNamePatches.SetBuildingDataRecordNamePostfix
//       → Uses Building.tier to decide which name
//       → Already confirmed: Building.SetBuildingDataRecordName exists (TW uses it)
//
//    4. HunterCabin tier-up / upgrade (Postfix)
//       → When building reaches T2, attach HunterCabinEnhancement component
//       → Show path-selection UI (TODO: design path selection UX — pop-up?
//                keyboard shortcut? right-click menu?)
//       → TODO: confirm upgrade method name
//
//    5. UISubwidgetHunterCabin.Init (Postfix) [if applicable]
//       → Inject T2 path choice buttons into the building's info panel
//       → Mirrors Tended Wilds WildPlantingPatches.CultivationInitPostfix
//       → TODO: confirm UI subwidget class name for Hunter Cabin
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    public static class HunterCabinPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ── Manual patch application (called from Plugin.OnInitializeMelon) ───
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!WardenOfTheWildsMod.HunterOverhaulEnabled.Value) return;

            Type? hunterType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // HunterBuilding confirmed from Assembly-CSharp.dll decompile; HunterCabin kept as fallback
                hunterType = asm.GetType("HunterBuilding") ?? asm.GetType("HunterCabin");
                if (hunterType != null) break;
            }

            if (hunterType == null)
            {
                MelonLogger.Warning("[WotW] HunterCabinPatches: could not find HunterCabin type. " +
                                    "Update type name after decompile.");
                return;
            }

            // ── Patch 1: TrapperLodge trap spawn interval ─────────────────────────
            // trappingCarcassSpawnInterval is a direct read/write property on HunterBuilding
            // (confirmed from method dump: get_trappingCarcassSpawnInterval / set_trappingCarcassSpawnInterval).
            // HunterCabinEnhancement.ApplyPath() sets it directly via the property setter
            // when TrapperLodge is chosen — no Harmony patch needed here.
            MelonLogger.Msg("[WotW] HunterCabinPatches: trap interval set by Enhancement on path selection (OK).");

            // ── Patch 1c: Trapper spawn-interval re-apply ────────────────────
            // Vanilla UpdateTrappingPoints recalculates trappingCarcassSpawnInterval
            // on any work-area change, clobbering our TrapperLodge divider.
            // Postfix detects + restores.
            var updateTrapsMethod = AccessTools.Method(hunterType, "UpdateTrappingPoints");
            if (updateTrapsMethod != null)
            {
                harmony.Patch(updateTrapsMethod, postfix: new HarmonyMethod(
                    typeof(HunterCabinPatches).GetMethod(
                        nameof(UpdateTrappingPointsPostfix), AllStatic)));
                MelonLogger.Msg("[WotW] Patched HunterBuilding.UpdateTrappingPoints (Trapper interval re-apply)");
            }
            else
            {
                MelonLogger.Warning("[WotW] HunterCabinPatches: UpdateTrappingPoints not found.");
            }

            // ── Patch 2: Building rename at T2 ───────────────────────────────
            // Building.SetBuildingDataRecordName is confirmed from Tended Wilds source
            Type? buildingType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                buildingType = asm.GetType("Building");
                if (buildingType != null) break;
            }
            if (buildingType != null)
            {
                var setNameMethod = buildingType.GetMethod("SetBuildingDataRecordName", AllInstance);
                if (setNameMethod != null)
                {
                    harmony.Patch(setNameMethod, postfix: new HarmonyMethod(
                        typeof(HunterCabinPatches).GetMethod(
                            nameof(SetBuildingDataRecordNamePostfix), AllStatic)));
                    MelonLogger.Msg("[WotW] Patched Building.SetBuildingDataRecordName (hunter rename)");
                }
            }

            // ── Patch 3: Trap count enforcement ──────────────────────────────
            // Trap enable/disable is handled directly in HunterCabinEnhancement.SetTrapsEnabled()
            // via set_userDefinedMaxDeployedTraps() — confirmed property from method dump.
            // No separate Harmony patch needed here; the Enhancement component drives it.
            MelonLogger.Msg("[WotW] HunterCabinPatches: trap control via Enhancement property setter (OK).");

            // ── Patch 4: T2 path persistence across save/reload ──────────────
            // Mirrors FishingShackLoadPatches' RegisterSaveLoadPatches: postfix
            // HunterBuilding.Save(ES2Writer) to append our path int and
            // HunterBuilding.Load(ES2Reader) to read it back into SavedPaths so
            // the cabin's RestoreSavedPath picks it up before InitializeDelayed
            // applies the path. Without this, T2 cabins always reverted to
            // the default (Hunting Lodge / BGH) on save/reload because the
            // in-memory SavedPaths dict was wiped by OnMapLoaded.
            try
            {
                var saveMethod = AccessTools.Method(hunterType, "Save", new[] { typeof(ES2Writer) });
                if (saveMethod != null)
                {
                    harmony.Patch(saveMethod, postfix: new HarmonyMethod(
                        typeof(HunterCabinPatches).GetMethod(nameof(SavePostfix), AllStatic)));
                    MelonLogger.Msg($"[WotW] Patched {saveMethod.DeclaringType.Name}.Save (T2 path persistence)");
                }
                else
                {
                    MelonLogger.Warning("[WotW] HunterCabinPatches: HunterBuilding.Save(ES2Writer) not found — path persistence disabled.");
                }

                var loadMethod = AccessTools.Method(hunterType, "Load", new[] { typeof(ES2Reader) });
                if (loadMethod != null)
                {
                    harmony.Patch(loadMethod, postfix: new HarmonyMethod(
                        typeof(HunterCabinPatches).GetMethod(nameof(LoadPostfix), AllStatic)));
                    MelonLogger.Msg($"[WotW] Patched {loadMethod.DeclaringType.Name}.Load (T2 path persistence)");
                }
                else
                {
                    MelonLogger.Warning("[WotW] HunterCabinPatches: HunterBuilding.Load(ES2Reader) not found — path persistence disabled.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterCabinPatches: Save/Load patch register failed: {ex.Message}");
            }
        }

        // ── Patch implementations ─────────────────────────────────────────────

        /// <summary>
        /// Postfix on HunterBuilding.UpdateTrappingPoints — restores our
        /// Trapper-path interval divider after vanilla recalculates.
        /// </summary>
        public static void UpdateTrappingPointsPostfix(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                var enh = comp?.GetComponent<HunterCabinEnhancement>();
                enh?.ReapplyTrapSpawnIntervalAfterVanillaUpdate();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] UpdateTrappingPointsPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on Building.SetBuildingDataRecordName.
        /// Renames Hunter Lodge T2 to "Trapper Lodge" or "Hunting Lodge"
        /// based on the HunterCabinEnhancement path.
        /// </summary>
        public static void SetBuildingDataRecordNamePostfix(object __instance)
        {
            try
            {
                var building = __instance as Building;
                if (building == null) return;

                // Only apply to Hunter Cabin type (check class name)
                string typeName = building.GetType().Name;
                if (!typeName.Contains("Hunter")) return;
                if (building.tier < 2) return;

                var enhancement = (building as Component)?.GetComponent<HunterCabinEnhancement>();
                if (enhancement == null) return;

                var resource = building as Resource;
                if (resource != null && enhancement.Path != HunterT2Path.Vanilla)
                    resource.displayName = enhancement.PathDisplayName;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] SetBuildingDataRecordNamePostfix (hunter): {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on HunterBuilding.Save(ES2Writer). Appends a single int
        /// (the cabin's HunterCabinEnhancement.Path) to the save stream so the
        /// matching Load postfix can restore it. Vanilla doesn't know about
        /// the path enum, so this extends FF's save format additively. If
        /// the cabin has no enhancement (T1) we still write Vanilla(0) to
        /// keep the stream layout consistent across all hunter buildings.
        /// </summary>
        public static void SavePostfix(object __instance, ES2Writer writer)
        {
            try
            {
                if (writer == null) return;
                var comp = __instance as Component;
                var enh = comp?.GetComponent<HunterCabinEnhancement>();
                int pathInt = enh != null ? (int)enh.Path : (int)HunterT2Path.Vanilla;
                writer.Write(pathInt);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterCabinSavePostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on HunterBuilding.Load(ES2Reader). Reads the path int we
        /// appended in SavePostfix and stashes it in HunterCabinEnhancement's
        /// SavedPaths dict keyed by world position, where the cabin's
        /// InitializeDelayed → RestoreSavedPath will pick it up.
        ///
        /// Backward-compat: pre-patch saves don't have the appended int.
        /// The reader will either throw EOF or return garbage. We catch and
        /// validate the int falls in the known enum range; otherwise default
        /// to Vanilla (the prior behavior). Saving the game once after
        /// installing this patch writes proper data for the next load.
        /// </summary>
        public static void LoadPostfix(object __instance, ES2Reader reader)
        {
            try
            {
                if (reader == null) return;
                var comp = __instance as Component;
                if (comp == null) return;

                int pathInt = reader.Read<int>();

                if (pathInt != (int)HunterT2Path.Vanilla &&
                    pathInt != (int)HunterT2Path.TrapperLodge &&
                    pathInt != (int)HunterT2Path.HuntingLodge)
                {
                    MelonLogger.Msg(
                        $"[WotW] HunterCabinLoadPostfix: '{comp.gameObject.name}' " +
                        $"invalid path={pathInt} (legacy save), defaulting to Vanilla. " +
                        "Saving this game will write proper data for next load.");
                    return;
                }

                var path = (HunterT2Path)pathInt;
                HunterCabinEnhancement.SetSavedPathForPosition(comp.transform.position, path);
                MelonLogger.Msg(
                    $"[WotW] HunterCabinLoadPostfix: '{comp.gameObject.name}' restored path={path}");
            }
            catch (Exception ex)
            {
                // Reader likely ran out of bytes (legacy save with no appended int).
                MelonLogger.Msg(
                    $"[WotW] HunterCabinLoadPostfix: no path int in save (legacy), " +
                    $"defaulting to Vanilla. ({ex.GetType().Name})");
            }
        }

    }
}
