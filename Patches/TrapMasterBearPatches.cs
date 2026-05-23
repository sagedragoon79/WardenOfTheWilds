using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;
using WardenOfTheWilds.Systems;

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

                // Clamp deposits to the configured per-item cap (same pref that
                // governs vanilla butcher halt). storage.AddItems writes past
                // the per-item cap if total bldg capacity has room — without
                // this clamp, bear bonuses pile up indefinitely on lodges that
                // out-produce wagon haul rate, producing the well-known
                // "X/200 stack" symptom.
                int cap = WardenOfTheWildsMod.HunterCabinOutputStorageCap.Value;

                uint addedMeat   = DepositClamped(storage, wbm.itemMeat,   bonusMeat,   cap);
                uint addedPelt   = DepositClamped(storage, wbm.itemHide,   bonusPelt,   cap);
                uint addedTallow = DepositClamped(storage, wbm.itemTallow, bonusTallow, cap);

                MelonLogger.Msg(
                    $"[WotW] BEAR TRAP! Trap Master '{cabin.gameObject.name}' caught a bear! " +
                    $"+{addedMeat} meat, +{addedPelt} hide, +{addedTallow} tallow " +
                    $"(roll {roll:F3} < {chance:F3}).");

                int droppedMeat   = bonusMeat   - (int)addedMeat;
                int droppedPelt   = bonusPelt   - (int)addedPelt;
                int droppedTallow = bonusTallow - (int)addedTallow;
                if (droppedMeat > 0 || droppedPelt > 0 || droppedTallow > 0)
                {
                    MelonLogger.Warning(
                        $"[WotW] TrapMasterBear: cabin '{cabin.gameObject.name}' at cap " +
                        $"(HunterCabinOutputStorageCap={cap}) — forfeit " +
                        $"{droppedMeat} meat, {droppedPelt} hide, {droppedTallow} tallow.");
                }

                // ── DLC: Fox + Groundhog catches (Phase 1) ─────────────────
                // Trap Master's village-defender identity. Independent rolls
                // on top of the small-carcass-spawn event. Soft-gated by
                // DlcDetection.PetsDlcActive — non-DLC players never enter
                // the body.
                TryRollFoxOrGroundhog(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TrapMasterBearChancePatch: {ex.Message}");
            }
        }

        /// <summary>
        /// Deposits up to <paramref name="desired"/> of <paramref name="item"/> into
        /// <paramref name="storage"/>, clamped so the resulting per-item count does
        /// not exceed <paramref name="cap"/>. Returns the actual amount added
        /// (computed from before/after counts — vanilla AddItems' return value
        /// reports the new total, not the delta, which is why prior versions of
        /// this patch logged misleading "+N" numbers).
        /// </summary>
        internal static uint DepositClamped(ReservableItemStorage storage, Item item, int desired, int cap)
        {
            if (storage == null || item == null || desired <= 0) return 0u;

            uint current = storage.GetItemCount(item);
            if (current >= (uint)cap) return 0u;

            uint room = (uint)cap - current;
            uint deposit = (uint)Math.Min(desired, (int)room);
            if (deposit == 0u) return 0u;

            uint before = current;
            storage.AddItems(new ItemBundle(item, deposit, 100u));
            uint after = storage.GetItemCount(item);
            return after >= before ? after - before : 0u;
        }

        // ════════════════════════════════════════════════════════════════════
        //  DLC: Fox + Groundhog catches (Phase 1)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rolls independent fox and groundhog catches when a Trap Master
        /// small-carcass spawn event fires. Both gated by Pets DLC ownership;
        /// non-DLC players never enter the body (DlcDetection.PetsDlcActive
        /// returns false).
        ///
        /// Designed to fail safely on any reflection / item lookup miss —
        /// missing items just skip silently rather than throwing.
        /// </summary>
        private static void TryRollFoxOrGroundhog(AnimalTrapResource trap)
        {
            try
            {
                if (!DlcDetection.PestGameplayActive) return;
                if (trap == null) return;

                var cabin = trap.huntersResidence;
                if (cabin == null) return;

                var enh = cabin.GetComponent<HunterCabinEnhancement>();
                if (enh == null || enh.Path != HunterT2Path.TrapperLodge) return;

                var storage = cabin.storage;
                if (storage == null) return;

                var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
                if (wbm == null) return;

                int cap = WardenOfTheWildsMod.HunterCabinOutputStorageCap.Value;

                // ── Fox roll ─────────────────────────────────────────────────
                float foxChance = WardenOfTheWildsMod.TrapMasterFoxChance.Value;
                if (foxChance > 0f && UnityEngine.Random.value <= foxChance)
                {
                    int bonusHide = WardenOfTheWildsMod.TrapMasterFoxBonusHide.Value;
                    uint added = DepositClamped(storage, wbm.itemHide, bonusHide, cap);
                    MelonLogger.Msg(
                        $"[WotW] FOX TRAP! Trap Master '{cabin.gameObject.name}' caught a fox " +
                        $"raiding chickens. +{added} hide (chance {foxChance:F3}).");

                    int dropped = bonusHide - (int)added;
                    if (dropped > 0)
                        MelonLogger.Warning(
                            $"[WotW] FoxTrap: '{cabin.gameObject.name}' at cap — " +
                            $"forfeit {dropped} hide.");
                }

                // ── Groundhog roll ───────────────────────────────────────────
                float ghChance = WardenOfTheWildsMod.TrapMasterGroundhogChance.Value;
                if (ghChance > 0f && UnityEngine.Random.value <= ghChance)
                {
                    int bonusCarcass = WardenOfTheWildsMod.TrapMasterGroundhogBonusCarcass.Value;
                    int bonusHide    = WardenOfTheWildsMod.TrapMasterGroundhogBonusHide.Value;

                    uint addedCarcass = DepositClamped(
                        storage, wbm.itemSmallCarcass, bonusCarcass, cap);
                    uint addedHide    = DepositClamped(
                        storage, wbm.itemHide, bonusHide, cap);

                    MelonLogger.Msg(
                        $"[WotW] GROUNDHOG TRAP! Trap Master '{cabin.gameObject.name}' caught a " +
                        $"groundhog raiding crops. +{addedCarcass} small carcass, +{addedHide} hide " +
                        $"(chance {ghChance:F3}).");

                    int droppedCarcass = bonusCarcass - (int)addedCarcass;
                    int droppedHide    = bonusHide    - (int)addedHide;
                    if (droppedCarcass > 0 || droppedHide > 0)
                        MelonLogger.Warning(
                            $"[WotW] GroundhogTrap: '{cabin.gameObject.name}' at cap — " +
                            $"forfeit {droppedCarcass} carcass, {droppedHide} hide.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TryRollFoxOrGroundhog: {ex.Message}");
            }
        }
    }
}
