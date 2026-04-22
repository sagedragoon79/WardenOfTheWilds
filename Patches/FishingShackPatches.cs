using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Reflection;
using WardenOfTheWilds.Components;
using WardenOfTheWilds.Systems;

// ─────────────────────────────────────────────────────────────────────────────
//  FishingShackPatches
//  Harmony patches for Fishing Shack / Fishing Dock.
//
//  STATUS: STUBS — awaiting Assembly-CSharp.dll decompile results.
//
//  Patches planned:
//    1. FishingShack.OnFishingComplete / OnFishHarvested (Postfix)
//       → At T2: multiply output by FishingDockOutputMult
//       → At T2: call FishingShackEnhancement.ConsumeAccumulatedFishOil()
//         and produce fish oil into output storage
//       → TODO: confirm method name (candidates: OnFishingComplete,
//                OnFishHarvested, CompleteWork, FinishFishing, CollectFish)
//
//    2. FishingShack.OnBuildingUpgraded / tier setter (Postfix)
//       → When building reaches T2, call ApplyDockUpgrade()
//       → Ensures worker slots and work circle are set even on fresh upgrades
//       → TODO: confirm tier-up event method
//
//    3. Building.SetBuildingDataRecordName (Postfix)
//       → At T2 rename to "Fishing Dock"
//       → Same confirmed target as used by Tended Wilds
//
//    4. FishingShack "fish stocks low" hook
//       → Fires notification when fish count in assigned pond drops below threshold
//       → TODO: find the fish stock/count field or event on FishingShack
//       → Candidate: fishReserve, stockCount, availableFish, or a Pond component
//
//    5. UISubwidgetFishingShack.Init (Postfix) [if applicable]
//       → Show Fish Oil production indicator at T2
//       → TODO: confirm UI class name
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    public static class FishingShackPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // Low-stock threshold — notify if fish pond drops below this count
        private const int LowStockThreshold = 20;

        // ── Manual patch application ──────────────────────────────────────────
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

            Type? fishType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                fishType = asm.GetType("FishingShack") ?? asm.GetType("FishermanShack");
                if (fishType != null) break;
            }

            if (fishType == null)
            {
                MelonLogger.Warning("[WotW] FishingShackPatches: could not find FishingShack type. " +
                                    "Update type name after decompile.");
                return;
            }

            // ── Patch 1: TallyFishCaught — confirmed hook from Assembly-CSharp.dll ──
            // Signature: void TallyFishCaught(uint numCaught)
            // Two patches applied:
            //   Prefix  → multiplies numCaught by FishingDockOutputMult at T2
            //             (cleanest approach — modifies the value before it is tallied)
            //   Postfix → drives fish oil accumulation and TW fertilizer synergy
            var tallyMethod = fishType.GetMethod("TallyFishCaught", AllInstance);
            if (tallyMethod != null)
            {
                harmony.Patch(tallyMethod,
                    prefix:  new HarmonyMethod(typeof(FishingShackPatches)
                                 .GetMethod(nameof(TallyFishCaughtPrefix),  AllStatic)),
                    postfix: new HarmonyMethod(typeof(FishingShackPatches)
                                 .GetMethod(nameof(TallyFishCaughtPostfix), AllStatic)));
                MelonLogger.Msg("[WotW] Patched FishingShack.TallyFishCaught (prefix: mult, postfix: oil)");
            }
            else
            {
                // Fallback candidates for unexpected version differences
                string[] fallbackCandidates = { "OnFishingComplete", "OnFishHarvested",
                                                 "CompleteWork", "FinishFishing",
                                                 "CollectFish", "OnWorkComplete" };
                bool patched = false;
                foreach (string candidate in fallbackCandidates)
                {
                    var m = fishType.GetMethod(candidate, AllInstance);
                    if (m == null) continue;
                    // Fallback only gets the postfix — no safe way to multiply without ref param
                    harmony.Patch(m, postfix: new HarmonyMethod(
                        typeof(FishingShackPatches).GetMethod(
                            nameof(TallyFishCaughtPostfix), AllStatic)));
                    MelonLogger.Msg($"[WotW] Patched FishingShack.{candidate} (fallback postfix)");
                    patched = true;
                    break;
                }
                if (!patched)
                    MelonLogger.Warning("[WotW] FishingShackPatches: could not find TallyFishCaught " +
                                        "or any fallback fishing completion method.");
            }

            // ── Patch 2: Building rename at T2 ───────────────────────────────
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
                    // NOTE: HunterCabinPatches also patches this method.
                    // Harmony allows multiple postfixes on the same method — no conflict.
                    harmony.Patch(setNameMethod, postfix: new HarmonyMethod(
                        typeof(FishingShackPatches).GetMethod(
                            nameof(SetBuildingDataRecordNamePostfix), AllStatic)));
                    MelonLogger.Msg("[WotW] Patched Building.SetBuildingDataRecordName (fishing rename)");
                }
            }

            // ── Patch 3: Low-stock detection ──────────────────────────────────
            // TODO: Find and patch the method/coroutine that updates fish stock counts.
            // Candidates: UpdateFishCount, RefreshFishStock, OnPondStateChanged
            MelonLogger.Msg("[WotW] FishingShackPatches applied (low-stock patch pending decompile).");
        }

        // ── Patch implementations ─────────────────────────────────────────────

        /// <summary>
        /// Prefix on FishingShack.TallyFishCaught(uint numCaught).
        /// At T2 (Fishing Dock) multiplies numCaught before the game tallies the catch.
        /// The ref parameter modifies the actual argument seen by the original method.
        /// </summary>
        // NOTE: Harmony matches ref parameters by name — must match exactly.
        // Confirmed parameter name from game DLL: numFishCaught (NOT numCaught)
        public static void TallyFishCaughtPrefix(object __instance, ref uint numFishCaught)
        {
            try
            {
                if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

                var comp = __instance as Component;
                if (comp == null) return;

                var building = comp.GetComponent<Building>();
                if (building == null || building.tier < 2) return;

                float mult = WardenOfTheWildsMod.FishingDockOutputMult.Value;
                if (mult <= 1f) return;

                // Multiply the catch before the game records it.
                // Floor to uint — minimum 1 if something was caught.
                uint boosted = (uint)Math.Max(1, Math.Floor(numFishCaught * mult));
                numFishCaught = boosted;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TallyFishCaughtPrefix: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on FishingShack.TallyFishCaught(uint numCaught).
        /// Handles fish oil accumulation and Tended Wilds fertilizer synergy.
        /// The output multiplication is handled by the Prefix above.
        /// </summary>
        public static void TallyFishCaughtPostfix(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                var building = comp.GetComponent<Building>();
                if (building == null || building.tier < 2) return;

                var enhancement = comp.GetComponent<FishingShackEnhancement>();
                if (enhancement == null) return;

                // ── Fish oil accumulation ─────────────────────────────────────
                int oilUnits = enhancement.ConsumeAccumulatedFishOil();
                if (oilUnits > 0)
                {
                    ProduceFishOil(comp, oilUnits);
                }

                // ── Tended Wilds: fish oil fertilizer synergy ─────────────────
                if (oilUnits > 0 && WardenOfTheWildsMod.TendedWildsActive)
                {
                    TendedWildsCompat.ApplyFishOilFertilizer(
                        comp.transform.position,
                        radius: 50f,
                        multiplier: 1.25f,
                        durationMonths: 2);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TallyFishCaughtPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on Building.SetBuildingDataRecordName.
        /// Renames FishingShack T2 to "Fishing Dock".
        /// </summary>
        public static void SetBuildingDataRecordNamePostfix(object __instance)
        {
            try
            {
                var building = __instance as Building;
                if (building == null) return;

                string typeName = building.GetType().Name;
                if (!typeName.Contains("Fishing") && !typeName.Contains("Fish")) return;
                if (building.tier < 2) return;

                var resource = building as Resource;
                if (resource != null)
                    resource.displayName = "Fishing Dock";
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] SetBuildingDataRecordNamePostfix (fishing): {ex.Message}");
            }
        }

        // ── Fish oil production helper ────────────────────────────────────────
        // TODO: Produce actual fish oil items into the building's output storage.
        //
        // Three paths (decide after decompile + ItemID confirmation):
        //   Path A: Fish Oil maps to an existing ItemID (lamp oil, tallow variant?)
        //   Path B: New ItemID via reflection/enum patching (complex, fragile)
        //   Path C: Produce as Tallow with a flag in the enhancement component
        //           that downstream buildings (Smokehouse) recognise as "fish oil"
        //
        // Path C is most pragmatic for v0.1 — easy to upgrade later.
        private static void ProduceFishOil(Component fishShack, int units)
        {
            try
            {
                // TODO: implement once storage access method is confirmed.
                // Placeholder: log production for now.
                MelonLogger.Msg($"[WotW] Fish Oil produced: {units} unit(s) at '{fishShack.gameObject.name}'");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ProduceFishOil: {ex.Message}");
            }
        }

        // ── Low-stock check helper ────────────────────────────────────────────
        // TODO: Call this from the patched fish-count update method.
        private static void CheckFishStock(object fishShackInstance)
        {
            try
            {
                // TODO: Read fish stock count from the confirmed field/property.
                // Candidate field names: fishCount, availableFish, pondFishReserve,
                //   or a reference to a Pond component with its own stock field.
                //
                // int stock = (int)(fishStockField.GetValue(fishShackInstance) ?? 0);
                // if (stock < LowStockThreshold)
                //     MelonLogger.Warning($"[WotW] LOW FISH STOCK at " +
                //         $"'{(fishShackInstance as Component)?.gameObject.name}': {stock} remaining!");
            }
            catch { }
        }
    }
}
