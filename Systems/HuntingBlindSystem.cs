using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  HuntingBlindSystem
//  Manages placed Hunting Blinds — fixed elevated firing positions for hunters.
//
//  DESIGN:
//    A Hunting Blind is a civilian-staffed defensive/hunting structure that
//    mirrors the game's guard tower pattern:
//      • Staffed by a regular villager (hunter job preferred, not military)
//      • Small gold upkeep per assigned hunter — same as guard tower
//      • Provides an elevated fixed firing position
//      • Grants a shoot range bonus (height advantage, like tower archers)
//      • Unlocked by Hunting Lodge T2
//
//  Hunting Blind — active, 1 assigned worker, hunter shoots FROM it.
//                  Gold upkeep. Used for both raids and wilderness hunting.
//
//  (An earlier design considered a passive "Deer Stand" companion building.
//   That was dropped — the announced Cat & Dog DLC introduces dogs as a deer
//   deterrent, which covers that gameplay niche.)
//
//  DUAL USE:
//    1. WILDERNESS HUNTING:
//       Place near game trails. The hunter in the blind shoots from elevation,
//       getting hits in before the animal reaches melee range.
//       No kiting needed — the blind IS the safe position.
//
//    2. RAID DEFENSE:
//       Place on town perimeter. Assigned hunter acts like a guard tower archer
//       but cheaper (no gold for the building itself, just worker upkeep).
//       Can be ctrl+N hotkeyed alongside other combat units.
//       Can be reassigned back to hunting when the raid ends — stays civilian.
//
//  GUARD TOWER RELATIONSHIP:
//    Guard towers: civilian worker + small gold upkeep + fixed shoot position
//    Hunting Blind: same pattern, cheaper structure, hunter-role preferred,
//                   wilderness placement context.
//    BuildingData template: clone from guard tower once class name confirmed.
//    Gold upkeep: match guard tower cost or slightly less (blind is smaller).
//
//  KITING INTERACTION:
//    Hunters in a Hunting Blind do NOT kite — they are stationary by design.
//    Free-roaming HuntingLodge hunters use the dynamic kiting system.
//    When a free-roaming hunter retreats, the Hunting Blind is the destination:
//      field hunter retreats → blind position → shoots from elevated cover.
//    This makes the blind act as both a static turret AND a kiting backstop.
//
//  CONFIRMED CLASS NAME (pending dump):
//    ? GuardTower / WatchTower / ArcherTower / TowerBuilding
//    → Will be confirmed from HunterCombatPatches.DumpCombatMethods()
//
//  GOLD UPKEEP IMPLEMENTATION (pending dump):
//    ? Building.goldUpkeep / Building.maintenanceCost / goldCostPerWorker
//    → Guard tower dump will reveal the correct field name
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Data record for a single placed Hunting Blind.
    /// </summary>
    public class HuntingBlind
    {
        public Vector3 Position;
        public Vector3 AssignedHunterCabinPos;  // Which HuntingLodge supplies the worker
        public bool    IsOccupied;              // Worker currently assigned
        public float   ShootRangeBonus;         // World units added on top of base bow range

        // Runtime: last time this blind's worker fired (for kiting interval calc)
        public float LastShotTime = 0f;

        public bool HasCabinAssigned => AssignedHunterCabinPos != Vector3.zero;
    }

    public static class HuntingBlindSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Persistence ───────────────────────────────────────────────────────
        private static readonly Dictionary<int, HuntingBlind> ActiveBlinds =
            new Dictionary<int, HuntingBlind>();

        private static int PositionKey(Vector3 pos) =>
            Mathf.RoundToInt(pos.x * 1000f + pos.z);

        public static void OnMapLoaded()
        {
            ActiveBlinds.Clear();
            MelonLogger.Msg("[WotW] HuntingBlindSystem: map loaded, blinds cleared.");
        }

        // ── Registration ──────────────────────────────────────────────────────
        public static HuntingBlind RegisterBlind(Vector3 position)
        {
            int key = PositionKey(position);
            if (ActiveBlinds.ContainsKey(key))
            {
                MelonLogger.Warning($"[WotW] HuntingBlindSystem: blind already at {position}");
                return ActiveBlinds[key];
            }

            var blind = new HuntingBlind
            {
                Position        = position,
                ShootRangeBonus = WardenOfTheWildsMod.HuntingBlindRangeBonus.Value,
            };
            ActiveBlinds[key] = blind;

            MelonLogger.Msg(
                $"[WotW] Hunting Blind placed at {position} " +
                $"(range +{blind.ShootRangeBonus:F1}u, total: {ActiveBlinds.Count})");
            return blind;
        }

        public static void RemoveBlind(Vector3 position)
        {
            ActiveBlinds.Remove(PositionKey(position));
            MelonLogger.Msg($"[WotW] Hunting Blind removed at {position}");
        }

        public static HuntingBlind? GetBlind(Vector3 position) =>
            ActiveBlinds.TryGetValue(PositionKey(position), out HuntingBlind b) ? b : null;

        public static IEnumerable<HuntingBlind> AllBlinds => ActiveBlinds.Values;

        // ── Nearest blind lookup ──────────────────────────────────────────────
        /// <summary>
        /// Returns the nearest Hunting Blind within <paramref name="maxRange"/>
        /// of <paramref name="position"/>, or null if none found.
        /// Used by the kiting system to find the hunter's retreat destination.
        /// </summary>
        public static HuntingBlind? FindNearestBlind(Vector3 position, float maxRange)
        {
            float maxRangeSqr = maxRange * maxRange;
            HuntingBlind? best = null;
            float bestDistSqr = float.MaxValue;

            foreach (var blind in ActiveBlinds.Values)
            {
                float dSqr = (blind.Position - position).sqrMagnitude;
                if (dSqr <= maxRangeSqr && dSqr < bestDistSqr)
                {
                    bestDistSqr = dSqr;
                    best = blind;
                }
            }
            return best;
        }

        /// <summary>
        /// Finds the best blind for a retreating hunter to fall back to.
        /// Prefers blinds assigned to the hunter's own cabin over unassigned ones.
        /// </summary>
        public static HuntingBlind? FindRetreatBlind(
            Vector3 hunterPosition, Vector3 cabinPosition, float workRadius)
        {
            HuntingBlind? assignedBlind = null;
            HuntingBlind? nearestBlind  = null;
            float bestAssignedDist = float.MaxValue;
            float bestNearestDist  = float.MaxValue;
            float workRadiusSqr    = workRadius * workRadius;

            foreach (var blind in ActiveBlinds.Values)
            {
                // Must be within the hunter cabin's work radius
                float cabinDistSqr = (blind.Position - cabinPosition).sqrMagnitude;
                if (cabinDistSqr > workRadiusSqr) continue;

                float hunterDist = Vector3.Distance(blind.Position, hunterPosition);

                if (blind.HasCabinAssigned &&
                    Vector3.Distance(blind.AssignedHunterCabinPos, cabinPosition) < 5f)
                {
                    // Assigned to this specific cabin — strongly prefer
                    if (hunterDist < bestAssignedDist)
                    {
                        bestAssignedDist = hunterDist;
                        assignedBlind    = blind;
                    }
                }
                else
                {
                    // Unassigned blind — use as fallback
                    if (hunterDist < bestNearestDist)
                    {
                        bestNearestDist = hunterDist;
                        nearestBlind    = blind;
                    }
                }
            }

            return assignedBlind ?? nearestBlind;
        }

        // ── Gold upkeep application ───────────────────────────────────────────
        // Mirrors the guard tower gold upkeep pattern — applied per occupied blind.
        // Field name TBD from guard tower dump; stub logs until confirmed.
        public static void ApplyGoldUpkeep(HuntingBlind blind, Component buildingComponent)
        {
            try
            {
                if (!blind.IsOccupied) return;

                float cost = WardenOfTheWildsMod.HuntingBlindGoldUpkeep.Value;
                if (cost <= 0f) return;

                // TODO: Apply gold cost using the same mechanism as guard towers.
                // Candidate: Building.AddGoldUpkeep(float), Building.goldUpkeep field,
                // or a TownResources.SpendGold(float) call.
                // Confirm field name from guard tower dump.
                MelonLogger.Msg(
                    $"[WotW] Hunting Blind at {blind.Position}: " +
                    $"gold upkeep {cost:F1}/period (pending field name from dump)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HuntingBlindSystem.ApplyGoldUpkeep: {ex.Message}");
            }
        }

        // ── Shoot range bonus application ─────────────────────────────────────
        /// <summary>
        /// Applies the blind's shoot range bonus to the assigned worker.
        /// Called when a hunter is assigned to the blind.
        /// Requires confirming the shoot range / attack range field from the dump.
        /// </summary>
        public static void ApplyRangeBonus(HuntingBlind blind, Component worker)
        {
            try
            {
                if (worker == null) return;

                string[] rangeCandidates = {
                    "shootRange", "attackRange", "bowRange",
                    "maxRange", "engageRange",
                };

                var type = worker.GetType();
                foreach (string name in rangeCandidates)
                {
                    var field = type.GetField(name, AllInstance);
                    if (field != null && (field.FieldType == typeof(float) ||
                                         field.FieldType == typeof(int)))
                    {
                        float current = Convert.ToSingle(field.GetValue(worker));
                        field.SetValue(worker, current + blind.ShootRangeBonus);
                        MelonLogger.Msg(
                            $"[WotW] Blind at {blind.Position}: " +
                            $"worker range {current:F1} → {current + blind.ShootRangeBonus:F1}");
                        return;
                    }

                    var prop = type.GetProperty(name, AllInstance);
                    if (prop != null && prop.CanWrite)
                    {
                        float current = Convert.ToSingle(prop.GetValue(worker));
                        prop.SetValue(worker, current + blind.ShootRangeBonus);
                        MelonLogger.Msg(
                            $"[WotW] Blind at {blind.Position}: " +
                            $"worker range {current:F1} → {current + blind.ShootRangeBonus:F1}");
                        return;
                    }
                }

                MelonLogger.Msg(
                    "[WotW] ApplyRangeBonus: range field not yet confirmed — " +
                    "awaiting combat/guard tower dump.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HuntingBlindSystem.ApplyRangeBonus: {ex.Message}");
            }
        }
    }
}
