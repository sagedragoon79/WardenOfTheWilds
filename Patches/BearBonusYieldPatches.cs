using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// BGH (Hunting Lodge) path: bonus yield on bear kills.
    ///
    /// Design:
    ///   Bears are the apex big-game target. Vanilla spawns a generic
    ///   CarcassResource with one ItemCarcass — butchered, that yields roughly
    ///   the same as a deer (~30 meat, 2 hide, 3 tallow). The Hunting Lodge
    ///   specialisation should make bears feel worth the risk: target ~250 meat,
    ///   5 pelts, 8 tallow per kill.
    ///
    /// Approach (why we inject into the cabin, not the carcass):
    ///   The carcass-collection reservation system only reserves the
    ///   ItemCarcass — extra items added to the CarcassResource.storage
    ///   would either sit unreserved (laborers then pick them up, which the
    ///   user explicitly doesn't want) or rot with the carcass. Injecting the
    ///   bonus directly into the killing hunter's cabin manufacturingStorage
    ///   bypasses the pickup pipeline entirely: the reward appears in the
    ///   cabin immediately, the ItemCarcass is still collected + butchered
    ///   by the normal flow, and the Smokehouse picks up meat from the cabin
    ///   as usual.
    ///
    /// Gating:
    ///   • A BGH hunter (Hunting Lodge path) must appear in the bear's
    ///     damage history — they don't have to land the killing blow.
    ///     Mirrors vanilla's ProcessAttackerToAssignResidence which also
    ///     walks damageHistory to find the hunter for carcass assignment.
    ///   • T1 Vanilla and T2 TrapperLodge paths are untouched.
    /// </summary>
    [HarmonyPatch(typeof(Bear), "OnCombatDeath")]
    internal static class BearBonusYieldPatch
    {
        // Dedup: Harmony can fire the postfix more than once per death
        // (Bear.OnCombatDeath → base.OnCombatDeath chain). Track the last
        // processed bear instance ID + frame to prevent double-injection.
        private static int _lastBearId;
        private static int _lastFrame;

        [HarmonyPostfix]
        public static void Postfix(Bear __instance, GameObject damageCauser)
        {
            try
            {
                if (__instance == null) return;

                int bearId = __instance.GetInstanceID();
                int frame  = Time.frameCount;
                if (bearId == _lastBearId && frame == _lastFrame) return;
                _lastBearId = bearId;
                _lastFrame  = frame;

                // Walk the bear's full damage history to find a BGH hunter —
                // mirrors vanilla's ProcessAttackerToAssignResidence pattern.
                // Check the killing-blow dealer first (most common case),
                // then walk history newest→oldest for mixed-combat scenarios
                // where a soldier or T1 hunter finishes the bear.
                Villager hunter;
                HunterBuilding cabin;
                if (!FindBGHHunterInvolved(__instance, damageCauser, out hunter, out cabin))
                    return;

                int bonusMeat   = WardenOfTheWildsMod.BGHBearBonusMeat.Value;
                int bonusPelt   = WardenOfTheWildsMod.BGHBearBonusPelt.Value;
                int bonusTallow = WardenOfTheWildsMod.BGHBearBonusTallow.Value;

                var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
                if (wbm == null)
                {
                    MelonLogger.Warning("[WotW] BearBonusYield: WorkBucketManager unavailable.");
                    return;
                }

                // Target storage: cabin's manufacturingStorage. This is where the
                // butcher pipeline operates; smokehouses / general collectors pick
                // finished meat/hide/tallow up from here.
                var storage = cabin.manufacturingStorage;
                if (storage == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] BearBonusYield: cabin '{cabin.gameObject.name}' " +
                        $"has no manufacturingStorage.");
                    return;
                }

                uint addedMeat   = 0u;
                uint addedPelt   = 0u;
                uint addedTallow = 0u;

                if (bonusMeat > 0 && wbm.itemMeat != null)
                    addedMeat = storage.AddItems(
                        new ItemBundle(wbm.itemMeat, (uint)bonusMeat, 100u));

                if (bonusPelt > 0 && wbm.itemHide != null)
                    addedPelt = storage.AddItems(
                        new ItemBundle(wbm.itemHide, (uint)bonusPelt, 100u));

                if (bonusTallow > 0 && wbm.itemTallow != null)
                    addedTallow = storage.AddItems(
                        new ItemBundle(wbm.itemTallow, (uint)bonusTallow, 100u));

                MelonLogger.Msg(
                    $"[WotW] BearBonusYield: '{hunter.gameObject.name}' (BGH) involved in bear kill " +
                    $"at ({__instance.transform.position.x:F0},{__instance.transform.position.z:F0}). " +
                    $"Cabin '{cabin.gameObject.name}' +{addedMeat} meat, " +
                    $"+{addedPelt} hide, +{addedTallow} tallow " +
                    $"(requested {bonusMeat}/{bonusPelt}/{bonusTallow}).");

                if (addedMeat < (uint)bonusMeat ||
                    addedPelt < (uint)bonusPelt ||
                    addedTallow < (uint)bonusTallow)
                {
                    MelonLogger.Warning(
                        $"[WotW] BearBonusYield: cabin '{cabin.gameObject.name}' " +
                        $"over capacity — some bonus items were dropped. " +
                        $"Consider building a Smokehouse or adding stockpiles.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] BearBonusYieldPatch: {ex.Message}");
            }
        }

        /// <summary>
        /// Searches for a BGH (Hunting Lodge) hunter who participated in this
        /// bear kill. Checks the killing-blow dealer first, then walks the
        /// bear's full damage history newest→oldest.
        ///
        /// Returns true and populates hunter/cabin if a BGH hunter was involved.
        /// </summary>
        private static bool FindBGHHunterInvolved(
            Bear bear, GameObject damageCauser,
            out Villager hunter, out HunterBuilding cabin)
        {
            hunter = null;
            cabin  = null;

            // 1. Fast path: check damageCauser (killing blow)
            if (damageCauser != null &&
                TryExtractBGHHunter(damageCauser, out hunter, out cabin))
                return true;

            // 2. Walk damage history newest→oldest (same traversal order as
            //    vanilla's ProcessAttackerToAssignResidence).
            var combatComp = bear.GetComponent<DamageableComponent>();
            if (combatComp == null || combatComp.damageHistory == null)
                return false;

            for (var node = combatComp.damageHistory.Last;
                 node != null;
                 node = node.Previous)
            {
                var entry = node.Value;
                if (entry.damageCauser == null) continue;
                // Skip the killing-blow dealer — already checked above
                if (entry.damageCauser == damageCauser) continue;

                if (TryExtractBGHHunter(entry.damageCauser, out hunter, out cabin))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Given a GameObject, checks if it's a BGH-path hunter and returns
        /// the Villager + HunterBuilding pair via out params.
        /// </summary>
        private static bool TryExtractBGHHunter(
            GameObject obj, out Villager hunter, out HunterBuilding cabin)
        {
            hunter = null;
            cabin  = null;

            var villager = obj.GetComponent<Villager>()
                        ?? obj.GetComponentInParent<Villager>();
            if (villager == null) return false;
            if (!(villager.occupation is VillagerOccupationHunter)) return false;

            var hb = villager.residence as HunterBuilding;
            if (hb == null) return false;

            var enhancement = hb.GetComponent<HunterCabinEnhancement>();
            if (enhancement == null || enhancement.Path != HunterT2Path.HuntingLodge)
                return false;

            hunter = villager;
            cabin  = hb;
            return true;
        }
    }
}
