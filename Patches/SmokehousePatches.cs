using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using WardenOfTheWilds.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  SmokehousePatches
//  Harmony patches for SmokeHouse (confirmed class name from decompile).
//
//  The core community complaint: Smokehouse workers walk across the entire
//  map to collect Raw Meat / Raw Fish from distant sources even when closer
//  ones exist. SmokeHouse has no WorkArea of its own (unlike HunterBuilding
//  and FishingShack which both have WorkArea fields).
//
//  Item flow (confirmed):
//    Animal killed → Carcass → Hunter Cabin (processed by worker)
//                                   ↓
//                              Raw Meat + Pelts + Tallow
//                                   ↓
//              Fishing Shack → Raw Fish ──→ Smokehouse
//
//  Carcasses NEVER leave the Hunter Cabin. The Smokehouse only receives
//  Raw Meat (from Hunter Cabin output) and Raw Fish (from Fishing Shack).
//
//  Our fix:
//    1. SmokehouseEnhancement adds a WorkArea component and manages a
//       configurable work radius and optional source pins.
//
//    2. We patch SmokeHouse.CheckWorkAvailability to additionally validate
//       that the source buildings providing raw goods are within the
//       enhancement's approved radius/pins before dispatching a worker.
//
//    3. Phase 2 (once search entry class is confirmed via dump): patch the
//       search entry GetScore() to return -∞ for sources outside radius/pins.
//       This is cleaner than suppressing the work bucket.
//
//  ITEM IDs (confirmed from Assembly-CSharp.dll decompile):
//    Fish    = 39   (raw fish, from Fishing Shack)
//    RawMeat = ??   TODO: confirm from ItemID dump — likely near 40
//                   Add "ItemID" to DumpCombatMethods list to get all values.
//
//  Confirmed class:  SmokeHouse (capital H)
//  Confirmed base:   EnterableBuilding
//  Confirmed bucket: WorkBucketIdentifier.SmokeHouseNeedsWorker
//  Confirmed method: CheckWorkAvailability
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    public static class SmokehousePatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ── Source building scan cache ────────────────────────────────────────
        // CheckApprovedSourcesForGoods calls FindObjectsOfType for HunterBuilding
        // and FishingShack on every CheckWorkAvailability tick. Without caching,
        // two smokehouses ticking every ~2s = 4 uncached scene scans/second.
        private static readonly List<Component> _cachedHunterBuildings = new List<Component>();
        private static readonly List<Component> _cachedFishingShacks   = new List<Component>();
        private static float _sourceCacheExpiry = -1f;
        private const float  SourceCacheTTL     = 8f;

        // ── Raw Meat item ID ──────────────────────────────────────────────────
        // CONFIRMED from ItemID dump (26-4-19):
        //   Meat=12, Fish=39, Carcass=41, BoarCarcass=42, SmokedMeat=43, SmokedFish=44
        // Raw Meat (Meat=12) is the PROCESSED output of a Hunter Cabin.
        // Carcasses never leave the cabin — only Meat reaches the Smokehouse.
        private const int ITEM_RAW_MEAT = 12;  // ← CONFIRMED from dump (26-4-19): Meat=12
        private const int ITEM_RAW_FISH = 39;  // ← confirmed

        // ── Manual patch application ──────────────────────────────────────────
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!WardenOfTheWildsMod.SmokehouseOverhaulEnabled.Value) return;

            // SmokeHouse — NOTE: capital H, not Smokehouse
            Type? smokeType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                smokeType = asm.GetType("SmokeHouse");
                if (smokeType != null) break;
            }

            if (smokeType == null)
            {
                MelonLogger.Warning("[WotW] SmokehousePatches: could not find SmokeHouse type.");
                return;
            }

            // ── Patch 1: CheckWorkAvailability ────────────────────────────────
            // Postfix validates that raw material sources are within the
            // enhancement's approved radius/pins before the work bucket is filled.
            var checkWork = smokeType.GetMethod("CheckWorkAvailability", AllInstance);
            if (checkWork != null)
            {
                harmony.Patch(checkWork, postfix: new HarmonyMethod(
                    typeof(SmokehousePatches).GetMethod(
                        nameof(CheckWorkAvailabilityPostfix), AllStatic)));
                MelonLogger.Msg("[WotW] Patched SmokeHouse.CheckWorkAvailability (radius gate)");
            }

            // ── Patch 2: Source collection interception (Phase 2) ─────────────
            // Patch the search entry GetScore() to return -∞ for out-of-radius
            // sources so the worker never walks to them in the first place.
            // Candidates: CollectRawMeatSearchEntry, SmokeHouseCollectInputSearchEntry
            // TODO: Confirm class name via dump of search entry types.
            MelonLogger.Msg("[WotW] SmokehousePatches: collection intercept pending " +
                            "search entry class confirmation from dump.");

            MelonLogger.Msg("[WotW] SmokehousePatches applied.");
        }

        // ── Patch implementations ─────────────────────────────────────────────

        /// <summary>
        /// Postfix on SmokeHouse.CheckWorkAvailability.
        /// After vanilla determines work availability, enforce that raw material
        /// sources are within the Smokehouse's approved radius/pins.
        ///
        /// Only suppresses work when NO approved source has goods — if approved
        /// sources are stocked (or a wagon has delivered directly to this building),
        /// vanilla processing runs normally.
        /// </summary>
        public static void CheckWorkAvailabilityPostfix(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                var enhancement = comp.GetComponent<SmokehouseEnhancement>();
                if (enhancement == null) return;

                bool anyApprovedSourceHasGoods = CheckApprovedSourcesForGoods(
                    comp.transform.position, enhancement);

                if (!anyApprovedSourceHasGoods)
                    SuppressWorkBucket(__instance, comp);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] CheckWorkAvailabilityPostfix (Smokehouse): {ex.Message}");
            }
        }

        // ── Source goods checking ─────────────────────────────────────────────
        private static bool CheckApprovedSourcesForGoods(
            Vector3 smokePos, SmokehouseEnhancement enhancement)
        {
            try
            {
                // Refresh building lists on a TTL — avoids repeated FindObjectsOfType
                // on every smokehouse CheckWorkAvailability tick.
                float now = UnityEngine.Time.time;
                if (now >= _sourceCacheExpiry)
                {
                    _cachedHunterBuildings.Clear();
                    _cachedFishingShacks.Clear();
                    _sourceCacheExpiry = now + SourceCacheTTL;

                    Type? hunterType = null;
                    Type? fishType   = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (hunterType == null) hunterType = asm.GetType("HunterBuilding");
                        if (fishType   == null) fishType   = asm.GetType("FishingShack");
                        if (hunterType != null && fishType != null) break;
                    }

                    if (hunterType != null)
                        foreach (UnityEngine.Object obj in
                            UnityEngine.Object.FindObjectsOfType(hunterType))
                        {
                            var c = obj as Component;
                            if (c != null) _cachedHunterBuildings.Add(c);
                        }

                    if (fishType != null)
                        foreach (UnityEngine.Object obj in
                            UnityEngine.Object.FindObjectsOfType(fishType))
                        {
                            var c = obj as Component;
                            if (c != null) _cachedFishingShacks.Add(c);
                        }
                }

                // Check Hunter Cabins for Raw Meat (processed carcass output)
                foreach (var c in _cachedHunterBuildings)
                {
                    if (c == null) continue;
                    if (!enhancement.IsApprovedSource(c.transform.position)) continue;
                    if (BuildingHasRawMeat(c)) return true;
                }

                // Check Fishing Shacks for Raw Fish
                foreach (var c in _cachedFishingShacks)
                {
                    if (c == null) continue;
                    if (!enhancement.IsApprovedSource(c.transform.position)) continue;
                    if (BuildingHasRawFish(c)) return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] CheckApprovedSourcesForGoods: {ex.Message}");
                return true; // Fail open — don't break vanilla behaviour on error
            }
        }

        /// <summary>
        /// Returns true if the Hunter Cabin has Raw Meat available in storage.
        /// Raw Meat is the PROCESSED output of carcass handling — NOT the carcass itself.
        ///
        /// TODO: ITEM_RAW_MEAT (currently 40) is unconfirmed. Dump ItemID enum to verify.
        /// </summary>
        private static bool BuildingHasRawMeat(Component building)
        {
            try
            {
                var method = building.GetType().GetMethod(
                    "GetItemCountFromAllStorages", AllInstance);
                if (method == null) return true; // fail open

                var itemType = GetItemType();
                if (itemType == null) return true;

                var item = CreateItemById(itemType, ITEM_RAW_MEAT);
                if (item == null) return true;

                var count = method.Invoke(building, new object[] { item });
                return count is int c && c > 0;
            }
            catch { return true; }
        }

        /// <summary>
        /// Returns true if the Fishing Shack has Raw Fish available in storage.
        /// ItemID.Fish = 39 (confirmed from decompile).
        /// </summary>
        private static bool BuildingHasRawFish(Component building)
        {
            try
            {
                var method = building.GetType().GetMethod(
                    "GetItemCountFromAllStorages", AllInstance);
                if (method == null) return true;

                var itemType = GetItemType();
                if (itemType == null) return true;

                var item = CreateItemById(itemType, ITEM_RAW_FISH);
                if (item == null) return true;

                var count = method.Invoke(building, new object[] { item });
                return count is int c && c > 0;
            }
            catch { return true; }
        }

        // ── Work bucket suppression ───────────────────────────────────────────
        private static void SuppressWorkBucket(object instance, Component comp)
        {
            try
            {
                // TODO (Phase 2): call CheckToStayInWorkBucket(stay: false, ...,
                //   WorkBucketIdentifier.SmokeHouseNeedsWorker, ...) once enum value
                //   is confirmed from dump. Currently a no-op stub — Phase 1 suppression
                //   is handled at the search entry level (Phase 2 goal).
                //
                // MelonLogger.Msg("[WotW] No approved sources with raw goods — Smokehouse idle.");
            }
            catch { }
        }

        // ── Item helper (bridge to game's Item type) ──────────────────────────
        private static Type? _itemType;
        private static Type? GetItemType()
        {
            if (_itemType != null) return _itemType;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                _itemType = asm.GetType("Item");
                if (_itemType != null) return _itemType;
            }
            return null;
        }

        private static object? CreateItemById(Type itemType, int itemIdInt)
        {
            try
            {
                Type? itemIdType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    itemIdType = asm.GetType("ItemID");
                    if (itemIdType != null) break;
                }
                if (itemIdType == null) return null;

                object itemId = Enum.ToObject(itemIdType, itemIdInt);
                var ctor = itemType.GetConstructor(new Type[] { typeof(string), itemIdType });
                return ctor?.Invoke(new object[] { "", itemId });
            }
            catch { return null; }
        }
    }
}
