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

            // ── Patch 1b: Willow trap discount (Tended Wilds companion) ──────────
            // OnStorageChanged fires when a hunter deposits a carcass — we use it
            // to check willow stock and log the discount opportunity.
            // Full discount implementation requires a crafting recipe patch (future).
            var storageChanged = hunterType.GetMethod("OnStorageChanged", AllInstance);
            if (storageChanged != null)
            {
                harmony.Patch(storageChanged, postfix: new HarmonyMethod(
                    typeof(HunterCabinPatches).GetMethod(
                        nameof(OnHuntCompletedPostfix), AllStatic)));
                MelonLogger.Msg("[WotW] Patched HunterBuilding.OnStorageChanged (willow discount check)");
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
        }

        // ── Patch implementations ─────────────────────────────────────────────

        /// <summary>
        /// Postfix on HunterBuilding.OnStorageChanged().
        /// Fires when a hunter deposits carcasses. Used for Tended Wilds willow
        /// trap discount check (full recipe discount requires a future crafting patch).
        /// </summary>
        public static void OnHuntCompletedPostfix(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                var enhancement = comp.GetComponent<HunterCabinEnhancement>();
                if (enhancement == null) return;

                // Willow trap discount check (TrapperLodge + Tended Wilds)
                if (enhancement.Path == HunterT2Path.TrapperLodge &&
                    WardenOfTheWildsMod.TendedWildsActive)
                {
                    ApplyWillowTrapDiscount(comp);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnHuntCompletedPostfix: {ex.Message}");
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

        // ── Willow trap discount (Tended Wilds companion) ─────────────────────
        // When Tended Wilds is active and this Trapper Lodge has cultivated Willow
        // nearby, iron trap cost is reduced (or willow traps are unlocked as free).
        private static void ApplyWillowTrapDiscount(Component hunterComp)
        {
            try
            {
                int willowStock = Systems.TendedWildsCompat.GetWillowStockNear(
                    hunterComp.transform.position, 60f);

                if (willowStock > 0)
                {
                    // TODO: Apply iron cost reduction to trap crafting recipe.
                    // Requires knowing how HunterCabin manages trap iron costs.
                    MelonLogger.Msg(
                        $"[WotW] Willow available ({willowStock}u) near Trapper Lodge at " +
                        $"{hunterComp.transform.position} — trap discount applicable.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyWillowTrapDiscount: {ex.Message}");
            }
        }
    }
}
