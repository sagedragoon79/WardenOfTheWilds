using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Trap Master bear trap chance — passive bear income without combat.
    ///
    /// Each time an AnimalTrapResource spawns a SmallCarcass (the daily tick
    /// fires and the interval is met), we roll a configurable chance (default
    /// 3%) that the trap "caught a bear" instead. On success, bonus meat/hide/
    /// tallow (same amounts as BGH bear kill config) are deposited directly
    /// into the owning cabin's `storage` (the OUTPUT pool — same place
    /// vanilla's ProduceItems writes butcher output).
    ///
    /// IMPORTANT (May 2026 fix): items go to cabin.storage, NOT
    /// manufacturingStorage. Vanilla's CheckWorkAvailabilityForItem only
    /// queries base.storage to register the HasItemMeat work bucket, so
    /// items in manufacturingStorage are invisible to smokehouse pull and
    /// wagon push logistics. The earlier (broken) version of this patch
    /// dumped bonus into manufacturingStorage, accumulating unhauled meat
    /// up to 1800+ units in long Trap Master saves.
    ///
    /// The small carcass still spawns normally — the bear is a bonus on top.
    /// This gives Trap Master a rare windfall that mirrors BGH's bear kill
    /// bonus but earned passively through trapping rather than combat.
    ///
    /// Gating:
    ///   • Only fires for TrapperLodge-path cabins.
    ///   • Prefix captures pre-call carcass count; postfix fires only on the
    ///     0→1 transition so the roll happens once per production event, not
    ///     every day the carcass sits in storage.
    ///   • Uses the same BGHBearBonusMeat/Pelt/Tallow config values as the
    ///     combat bear kill — keeps the reward consistent across paths.
    /// </summary>
    [HarmonyPatch(typeof(AnimalTrapResource), "OnDayPassedEvent")]
    internal static class TrapMasterBearChancePatch
    {
        /// <summary>
        /// Captures carcass count BEFORE vanilla runs so the postfix can detect
        /// a 0→1 transition (i.e., this is the tick where the trap actually spawned
        /// a new carcass). Without this, the postfix would re-fire the bear roll
        /// every day the carcass sat uncollected.
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(AnimalTrapResource __instance, out uint __state)
        {
            __state = uint.MaxValue;
            try
            {
                if (__instance == null) return;
                var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
                if (wbm == null) return;
                __state = __instance.storage.GetItemCount(wbm.itemSmallCarcass);
            }
            catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(AnimalTrapResource __instance, uint __state)
        {
            try
            {
                if (__instance == null) return;

                var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
                if (wbm == null) return;

                // Only fire on the 0→1 transition — i.e., vanilla spawned a fresh
                // carcass this tick. If it was already 1 (carcass sitting), or is
                // still 0 (interval not met), skip.
                uint postCount = __instance.storage.GetItemCount(wbm.itemSmallCarcass);
                if (__state != 0 || postCount == 0) return;

                // Get the owning hunter building
                var cabin = __instance.huntersResidence;
                if (cabin == null) return;

                // Gate: must be Trap Master (TrapperLodge) path
                var enhancement = cabin.GetComponent<HunterCabinEnhancement>();
                if (enhancement == null || enhancement.Path != HunterT2Path.TrapperLodge)
                    return;

                // Roll bear chance
                float chance = WardenOfTheWildsMod.TrapMasterBearChance.Value;
                if (chance <= 0f) return;

                float roll = UnityEngine.Random.value;
                if (roll > chance) return;

                // Bear trapped! Inject bonus into cabin's OUTPUT storage
                // (NOT manufacturingStorage — see class header for bug history).
                var storage = cabin.storage;
                if (storage == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] TrapMasterBear: cabin '{cabin.gameObject.name}' " +
                        $"has no storage.");
                    return;
                }

                int bonusMeat   = WardenOfTheWildsMod.BGHBearBonusMeat.Value;
                int bonusPelt   = WardenOfTheWildsMod.BGHBearBonusPelt.Value;
                int bonusTallow = WardenOfTheWildsMod.BGHBearBonusTallow.Value;

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
                    $"[WotW] BEAR TRAP! Trap Master '{cabin.gameObject.name}' caught a bear! " +
                    $"+{addedMeat} meat, +{addedPelt} hide, +{addedTallow} tallow " +
                    $"(roll {roll:F3} < {chance:F3}).");

                if (addedMeat < (uint)bonusMeat ||
                    addedPelt < (uint)bonusPelt ||
                    addedTallow < (uint)bonusTallow)
                {
                    MelonLogger.Warning(
                        $"[WotW] TrapMasterBear: cabin '{cabin.gameObject.name}' " +
                        $"over capacity — some bear bonus items were dropped.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TrapMasterBearChancePatch: {ex.Message}");
            }
        }
    }
}
