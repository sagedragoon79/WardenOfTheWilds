using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Equipment gating for hunters: arrows + bow.
    ///
    /// THE BUG (community report, May 2026):
    ///   "Hunters gather carcasses or butcher when they have no arrows or bow."
    ///
    /// VANILLA DESIGN (confirmed via Assembly-CSharp decompile):
    ///   VillagerOccupationHunter sets up `itemGroupsToSeek` containing
    ///   SeekItemEntry(ItemID.Arrow, amountToSeek=30, amountToConsiderNotMet=2).
    ///   The vanilla logistics system (LogisticsRequest.RequestTag.SeekArrows)
    ///   automatically generates a delivery request when carried arrows drop
    ///   below `amountToConsiderNotMet`, fulfilled by laborers/wagons hauling
    ///   from the nearest source (Fletcher, Storehouse, etc.).
    ///
    /// WHY VANILLA'S DEFAULT FAILS:
    ///   The `amountToConsiderNotMet=2u` threshold means restock only triggers
    ///   when the hunter has 1 or 0 arrows. By that point they may already have
    ///   accepted a hunt/butcher task that runs to completion before the
    ///   restock delivery arrives — so they go to work nearly empty-handed.
    ///   Same pattern for bows (amountToConsiderNotMet=0u — only restocks when
    ///   bow is completely absent, not when worn-out replacement is queued).
    ///
    /// OUR FIX (two layers):
    ///
    ///   LAYER 1 — Threshold bump:
    ///     Postfix the VillagerOccupationHunter constructor. Walk
    ///     `itemGroupsToSeek` and bump the Arrow entry's `amountToConsiderNotMet`
    ///     from 2u to HunterMinArrows (default 10). Vanilla logistics then
    ///     dispatches a laborer-hauled delivery the moment carried arrows
    ///     drop below 10 — much earlier intervention.
    ///
    ///   LAYER 2 — Work gate:
    ///     Provide IsHunterUnderEquipped helper that other patches consume to
    ///     short-circuit non-restock tasks (hunt, carcass collection, butcher).
    ///     Hunter under-equipped → task cancelled → vanilla re-evaluates →
    ///     SeekArrows wins by default since other tasks self-disqualify.
    ///
    /// LAYER 3 (Crate-supplied):
    ///   The actual delivery — laborer or wagon hauling arrows to the hunter
    ///   — is vanilla's job. We don't need to invent task plumbing; the
    ///   LogisticsRequest system already does it. Bumping the threshold
    ///   (Layer 1) is what makes that system fire earlier.
    /// </summary>
    public static class HunterEquipmentGatePatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Static so we only mutate the shared SeekItemEntry list once per
        // session (the list is constructed lazily on first hunter creation
        // and reused for all subsequent hunters).
        private static bool _seekThresholdBumped = false;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type? hunterOccType = AccessTools.TypeByName("VillagerOccupationHunter");
                if (hunterOccType == null)
                {
                    MelonLogger.Warning(
                        "[WotW] HunterEquipmentGatePatches: VillagerOccupationHunter type not found.");
                    return;
                }

                // Postfix the constructor so we run after vanilla populates
                // itemGroupsToSeek. SetupItemGroupsToSeek() also exists as a
                // public method but the ctor fires for every hunter assignment
                // — bumping it once at first invocation is sufficient.
                var ctor = AccessTools.Constructor(hunterOccType, new[] { typeof(Villager) });
                if (ctor != null)
                {
                    var postfix = new HarmonyMethod(typeof(HunterEquipmentGatePatches),
                        nameof(VillagerOccupationHunter_Ctor_Postfix));
                    harmony.Patch(ctor, postfix: postfix);
                    MelonLogger.Msg(
                        "[WotW] HunterEquipmentGatePatches: patched VillagerOccupationHunter ctor " +
                        "(arrow seek-threshold bump).");
                }
                else
                {
                    MelonLogger.Warning(
                        "[WotW] HunterEquipmentGatePatches: VillagerOccupationHunter ctor not found.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[WotW] HunterEquipmentGatePatches.Apply: {ex}");
            }
        }

        // ── Constructor postfix: bump the arrow seek-threshold ────────────────
        public static void VillagerOccupationHunter_Ctor_Postfix(object __instance)
        {
            if (_seekThresholdBumped) return;

            try
            {
                int target = WardenOfTheWildsMod.HunterMinArrows.Value;
                if (target <= 0)
                {
                    _seekThresholdBumped = true;
                    return; // pref disabled — leave vanilla's 2u threshold alone
                }

                Type t = __instance.GetType();
                // itemGroupsToSeek is on VillagerOccupation (base class) — walk
                // up the chain to find it.
                FieldInfo? groupsField = null;
                Type? cur = t;
                while (cur != null && groupsField == null)
                {
                    groupsField = cur.GetField("itemGroupsToSeek", AllInstance);
                    cur = cur.BaseType;
                }
                if (groupsField == null)
                {
                    MelonLogger.Warning(
                        "[WotW] HunterEquipmentGate: itemGroupsToSeek field not found.");
                    _seekThresholdBumped = true;
                    return;
                }

                var groups = groupsField.GetValue(__instance) as System.Collections.IList;
                if (groups == null) { _seekThresholdBumped = true; return; }

                int bumped = 0;
                foreach (var grp in groups)
                {
                    if (grp == null) continue;
                    var entriesField = grp.GetType().GetField("entries", AllInstance);
                    var entries = entriesField?.GetValue(grp) as System.Collections.IList;
                    if (entries == null) continue;

                    foreach (var entry in entries)
                    {
                        if (entry == null) continue;
                        var entryType = entry.GetType();
                        var idField   = entryType.GetField("itemIDToSeek", AllInstance);
                        var notMetF   = entryType.GetField("amountToConsiderNotMet", AllInstance);
                        if (idField == null || notMetF == null) continue;

                        // ItemID.Arrow == 35 (confirmed from prior dump).
                        // Compare via Convert.ToInt32 to avoid hard enum dep.
                        int itemId = Convert.ToInt32(idField.GetValue(entry));
                        if (itemId != 35) continue; // not Arrow

                        uint oldThreshold = (uint)notMetF.GetValue(entry);
                        uint newThreshold = (uint)target;
                        if (oldThreshold == newThreshold) continue;

                        notMetF.SetValue(entry, newThreshold);
                        bumped++;
                        MelonLogger.Msg(
                            $"[WotW] Vanilla arrow seek-threshold bumped: " +
                            $"{oldThreshold} → {newThreshold} (HunterMinArrows). " +
                            "Logistics will dispatch laborer-hauled arrow restock when " +
                            "hunter carry count drops below this.");
                    }
                }

                _seekThresholdBumped = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] VillagerOccupationHunter_Ctor_Postfix: {ex.Message}");
                _seekThresholdBumped = true; // don't loop on error
            }
        }

        // ── Helpers consumed by other patches ────────────────────────────────

        // Cached references for fast per-tick access.
        private static PropertyInfo? _permanentInventoryProp = null;
        private static bool _invPropSearchDone = false;

        private static object? _arrowItem = null;
        private static System.Collections.IList? _rangedWeaponItems = null;
        private static bool _itemRefsResolved = false;

        /// <summary>
        /// Returns true if the hunter is currently below the equipment
        /// threshold required to engage in non-defensive work tasks
        /// (hunting, carcass collection, butchering).
        ///
        /// Returns false (= "is equipped") on any error — fail-open so a
        /// stuck check doesn't block the entire hunter task system.
        /// </summary>
        public static bool IsHunterUnderEquipped(Component hunter)
        {
            try
            {
                if (hunter == null) return false;

                // Resolve permanentInventory property once
                if (!_invPropSearchDone)
                {
                    _invPropSearchDone = true;
                    _permanentInventoryProp = hunter.GetType()
                        .GetProperty("permanentInventory", AllInstance);
                }
                if (_permanentInventoryProp == null) return false;

                var inv = _permanentInventoryProp.GetValue(hunter) as ItemStorage;
                if (inv == null) return false;

                // Resolve item refs once via WorkBucketManager / Villager statics
                if (!_itemRefsResolved)
                {
                    _itemRefsResolved = true;
                    var gm = UnitySingleton<GameManager>.Instance;
                    _arrowItem = gm?.workBucketManager?.itemArrow;

                    // rangedWeaponItems is a Villager static property
                    var villagerType = AccessTools.TypeByName("Villager");
                    var rangedProp   = villagerType?.GetProperty("rangedWeaponItems",
                        BindingFlags.Public | BindingFlags.Static);
                    _rangedWeaponItems = rangedProp?.GetValue(null)
                        as System.Collections.IList;
                }

                // Arrow check
                int minArrows = WardenOfTheWildsMod.HunterMinArrows.Value;
                if (minArrows > 0 && _arrowItem != null)
                {
                    var arrowCount = inv.GetItemCount((Item)_arrowItem);
                    if (arrowCount < (uint)minArrows) return true;
                }

                // Bow check (uses GetItemCount(List<Item>) overload)
                if (WardenOfTheWildsMod.HunterRequiresBowForWork.Value
                 && _rangedWeaponItems != null && _rangedWeaponItems.Count > 0)
                {
                    var listGetItemCount = typeof(ItemStorage).GetMethod(
                        "GetItemCount", new[] { _rangedWeaponItems.GetType() });
                    if (listGetItemCount != null)
                    {
                        var raw = listGetItemCount.Invoke(inv, new object[] { _rangedWeaponItems });
                        uint bowCount = (uint)raw;
                        if (bowCount == 0) return true;
                    }
                }

                return false;
            }
            catch
            {
                return false; // fail-open
            }
        }
    }
}
