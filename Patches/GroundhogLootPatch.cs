using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Systems;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Groundhogs (marmots) drop nothing on death in vanilla DLC — they're
    /// crop pests meant to be chased off, not hunted for resources. WotW
    /// makes a killed groundhog yield a small auto-loot directly into the
    /// nearest hunter cabin's output storage.
    ///
    /// Why auto-loot instead of a world carcass (the fox approach):
    ///   • Groundhog extends PassiveAnimal, not AggressiveAnimal — different
    ///     death hook (PassiveAnimal.OnCombatDeath).
    ///   • A groundhog is tiny game — "1 pelt and that's it" is the right
    ///     reward scale. Spawning a full carcass-container + butcher cycle
    ///     for one hide is overkill.
    ///   • The SmallCarcass world-spawn pipeline is broken (see
    ///     FoxCarcassDropPatch history) — auto-deposit avoids it entirely.
    ///
    /// Deposits into cabin.storage (the OUTPUT pool), NOT manufacturingStorage,
    /// per the meat-stuck bug lessons: base.storage is what smokehouse pull /
    /// wagon push / storehouse haul logistics actually query.
    ///
    /// DLC-gated via PetsDlcActive; non-DLC saves never reach the body.
    /// </summary>
    internal static class GroundhogLootPatch
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const float MaxCabinAssignmentDistance = 300f;

        private static Type _groundhogType;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type passiveType = AccessTools.TypeByName("PassiveAnimal");
                if (passiveType == null)
                {
                    MelonLogger.Warning(
                        "[WotW] GroundhogLootPatch: PassiveAnimal type not found.");
                    return;
                }

                var onDeath = AccessTools.Method(passiveType, "OnCombatDeath");
                if (onDeath == null)
                {
                    MelonLogger.Warning(
                        "[WotW] GroundhogLootPatch: OnCombatDeath not found on PassiveAnimal.");
                    return;
                }

                harmony.Patch(onDeath,
                    postfix: new HarmonyMethod(typeof(GroundhogLootPatch),
                                               nameof(OnCombatDeathPostfix)));

                _groundhogType = AccessTools.TypeByName("Groundhog");
                MelonLogger.Msg(
                    $"[WotW] GroundhogLootPatch: patched PassiveAnimal.OnCombatDeath " +
                    $"(Groundhog type resolved: {_groundhogType != null}).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] GroundhogLootPatch.Register: {ex.Message}");
            }
        }

        public static void OnCombatDeathPostfix(object __instance, GameObject damageCauser)
        {
            try
            {
                if (__instance == null || _groundhogType == null) return;
                if (!_groundhogType.IsInstanceOfType(__instance)) return;
                if (!DlcDetection.PestGameplayActive) return;

                var ghComp = __instance as Component;
                if (ghComp == null) return;

                int bonusHide = WardenOfTheWildsMod.GroundhogKillBonusHide.Value;
                if (bonusHide <= 0) return;

                DepositPeltToNearestCabin(ghComp.transform.position, bonusHide);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] GroundhogLootPatch.Postfix: {ex.Message}");
            }
        }

        private static void DepositPeltToNearestCabin(Vector3 killPos, int hideAmount)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var rm = gm?.resourceManager;
                var wbm = gm?.workBucketManager;
                if (rm == null || wbm == null) return;

                // Nearest hunter cabin within range
                var listProp = rm.GetType().GetProperty("hunterBuildingsRO",
                    BindingFlags.Public | BindingFlags.Instance);
                var hunterBuildings = listProp?.GetValue(rm)
                    as System.Collections.IEnumerable;
                if (hunterBuildings == null) return;

                Component nearest = null;
                float bestSqr = MaxCabinAssignmentDistance * MaxCabinAssignmentDistance;
                foreach (var hb in hunterBuildings)
                {
                    var comp = hb as Component;
                    if (comp == null) continue;
                    float sqr = (comp.transform.position - killPos).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; nearest = comp; }
                }

                if (nearest == null)
                {
                    MelonLogger.Msg(
                        $"[WotW] Groundhog killed at {killPos:F0} but no hunter cabin " +
                        $"within {MaxCabinAssignmentDistance}u — pelt forfeit.");
                    return;
                }

                // cabin.storage (output pool). Reuse the bear-bonus
                // DepositClamped helper for cap enforcement + accurate delta.
                var storageProp = nearest.GetType().GetProperty("storage", AllInstance)
                               ?? nearest.GetType().BaseType?.GetProperty("storage", AllInstance);
                var storage = storageProp?.GetValue(nearest) as ReservableItemStorage;
                if (storage == null) return;

                int cap = WardenOfTheWildsMod.HunterCabinOutputStorageCap.Value;
                uint added = TrapMasterBearChancePatch.DepositClamped(
                    storage, wbm.itemHide, hideAmount, cap);

                MelonLogger.Msg(
                    $"[WotW] GROUNDHOG! Killed near '{nearest.gameObject.name}' " +
                    $"— +{added} hide auto-looted to cabin.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] GroundhogLootPatch.DepositPeltToNearestCabin: {ex.Message}");
            }
        }
    }
}
