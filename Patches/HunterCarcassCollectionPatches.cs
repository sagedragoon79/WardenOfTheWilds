using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Safety gate on auto carcass collection — tier and threat-aware.
    ///
    /// Problem: hunters chasing carcasses run straight into wolf packs and die.
    /// Vanilla's CollectCarcassesSearchEntry.ProcessNewTask doesn't check for
    /// danger at the carcass location; we wrap it to enforce survivability.
    ///
    /// Tier behavior:
    ///   T1 (no path):        block if any threat near carcass work area, UNLESS
    ///                        exactly 1 winnable threat and hunter is combat-ready
    ///                        (HP >70% + arrows + not a bear) — engage first,
    ///                        collect next cycle.
    ///   T2 Trap Master:      same as T1. Trappers will defend a single wolf on
    ///                        their trap but not wade into a pack.
    ///   T2 BGH:              can handle 1-2 wolves/boars at HP >50%. Bears still
    ///                        defer. Otherwise same logic.
    ///
    /// Player override:
    ///   This patch only affects AUTO task selection (ProcessNewTask).
    ///   ManuallyProcessWorkObjForCarcassCollection handles player-issued
    ///   commands and is untouched — player can always micro the hunter.
    /// </summary>
    [HarmonyPatch(typeof(CollectCarcassesSearchEntry), "ProcessNewTask")]
    internal static class HunterCarcassCollectionPatch
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Cooldown on commanding engagement to avoid SetTarget spam when the
        // hunter is already in combat.
        private static readonly System.Collections.Generic.Dictionary<int, float>
            _lastEngageTime = new System.Collections.Generic.Dictionary<int, float>();
        private const float EngageCooldown = 5f;

        [HarmonyPrefix]
        public static bool Prefix(
            CollectCarcassesSearchEntry __instance,
            ref Task __result)
        {
            try
            {
                // Get hunter + building
                var receiver = AccessTools.Field(typeof(TaskSearchEntry), "_receiver")
                    ?.GetValue(__instance);
                var hunter = receiver as Component;
                if (hunter == null) return true;  // can't evaluate, let vanilla run

                var getHB = typeof(CollectCarcassesSearchEntry).GetMethod(
                    "GetHunterBuilding", AllInstance);
                var hunterBuilding = getHB?.Invoke(__instance, null) as Component;
                if (hunterBuilding == null) return true;

                // Scan for threats in the hunter's work area
                float workRadius = GetBuildingRadius(hunterBuilding);
                Vector3 center = hunterBuilding.transform.position;
                int threatCount = 0;
                Component nearestThreat = null;
                float nearestDistSqr = float.MaxValue;
                bool anyBear = false;

                // Enumerate all aggressive animals in scene, filter by radius
                // (cheap here since count is typically <20)
                foreach (var animalObj in HunterCombatPatches.GetCachedAggressiveAnimals())
                {
                    if (animalObj == null) continue;
                    var animalComp = animalObj as Component;
                    if (animalComp == null) continue;

                    float distSqr = (animalComp.transform.position - center).sqrMagnitude;
                    if (distSqr > workRadius * workRadius) continue;

                    threatCount++;
                    if (animalObj is Bear) anyBear = true;

                    if (distSqr < nearestDistSqr)
                    {
                        nearestDistSqr = distSqr;
                        nearestThreat = animalComp;
                    }
                }

                if (threatCount == 0)
                    return true;  // clear — vanilla proceeds

                // Tier-aware decision
                var path = GetHunterPath(hunter);
                float hp = GetHpPercent(hunter);
                bool hasArrows = HunterHasArrows(hunter);

                int allowedSolo;
                float hpThreshold;
                bool allowBear;

                switch (path)
                {
                    case HunterT2Path.HuntingLodge:  // BGH
                        allowedSolo  = 2;
                        hpThreshold  = 0.5f;
                        allowBear    = false;
                        break;
                    case HunterT2Path.TrapperLodge:
                        allowedSolo  = 1;
                        hpThreshold  = 0.7f;
                        allowBear    = false;
                        break;
                    default:  // T1 or no path set
                        allowedSolo  = 1;
                        hpThreshold  = 0.7f;
                        allowBear    = false;
                        break;
                }

                bool canEngage =
                    !anyBear &&
                    threatCount <= allowedSolo &&
                    hp >= hpThreshold &&
                    hasArrows &&
                    nearestThreat != null;

                // Rate-limit engage commands
                int hunterKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(hunter);
                bool onCooldown = _lastEngageTime.TryGetValue(hunterKey, out float lastEngage)
                    && Time.time - lastEngage < EngageCooldown;

                if (canEngage && !onCooldown)
                {
                    var combatComp = hunter.GetComponent<CombatComponent>();
                    var targetDamageable = nearestThreat.GetComponent<IDamageable>()
                        ?? nearestThreat.GetComponentInChildren<IDamageable>();
                    if (combatComp != null && targetDamageable != null)
                    {
                        combatComp.SetTarget(
                            newTarget: targetDamageable,
                            newTargetCombatAction: CombatAction.Attack,
                            newTargetSourceIdentifier: TargetSourceIdentifier.Search);
                        _lastEngageTime[hunterKey] = Time.time;

                        MelonLogger.Msg(
                            $"[WotW] Carcass-defense engage: '{hunter.gameObject.name}' " +
                            $"→ '{nearestThreat.gameObject.name}' " +
                            $"(path={path}, threats={threatCount}, hp={hp:F2})");
                    }
                }
                else
                {
                    // Defer — log once per long cooldown so we don't spam
                    if (!onCooldown)
                    {
                        _lastEngageTime[hunterKey] = Time.time;
                        MelonLogger.Msg(
                            $"[WotW] Carcass-defer: '{hunter.gameObject.name}' " +
                            $"(path={path}, threats={threatCount}, hp={hp:F2}, " +
                            $"bear={anyBear}, arrows={hasArrows})");
                    }
                }

                // Either way we skip collection this cycle — hunter will either
                // engage the threat (clearing it for next cycle) or wait for the
                // threat to wander off (vanilla's 30s abandon handles deadlock).
                __result = null;
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterCarcassCollectionPatch: {ex.Message}");
                return true;  // safe default: let vanilla run
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static float GetBuildingRadius(Component building)
        {
            // Match HunterCombatPatches.GetHunterBuildingRadius pattern —
            // iterate known field candidates via reflection.
            const float Fallback = 100f;
            string[] candidates = {
                "huntingRadius", "_huntingRadius",
                "workRadius", "_workRadius",
                "radius", "_radius",
            };
            try
            {
                Type t = building.GetType();
                while (t != null && t.Name != "MonoBehaviour" && t.Name != "Object")
                {
                    foreach (var name in candidates)
                    {
                        var f = t.GetField(name, AllInstance);
                        if (f != null && f.FieldType == typeof(float))
                            return (float)f.GetValue(building);
                    }
                    t = t.BaseType;
                }
            }
            catch { }
            return Fallback;
        }

        private static HunterT2Path GetHunterPath(Component hunter)
        {
            // Find the hunter's residence (HunterBuilding) and check its
            // HunterCabinEnhancement.Path
            try
            {
                var residenceProp = hunter.GetType().GetProperty("residence", AllInstance);
                var residence = residenceProp?.GetValue(hunter) as Component;
                if (residence == null) return HunterT2Path.Vanilla;

                var enh = residence.GetComponent<HunterCabinEnhancement>();
                return enh?.Path ?? HunterT2Path.Vanilla;
            }
            catch { return HunterT2Path.Vanilla; }
        }

        private static float GetHpPercent(Component hunter)
        {
            try
            {
                var healthProp = hunter.GetType().GetProperty("villagerHealth", AllInstance);
                var vh = healthProp?.GetValue(hunter);
                if (vh == null) return 1f;

                var hpField = vh.GetType().GetProperty("health", AllInstance);
                return (float)(hpField?.GetValue(vh) ?? 1f);
            }
            catch { return 1f; }
        }

        private static bool HunterHasArrows(Component hunter)
        {
            try
            {
                var invProp = hunter.GetType().GetProperty("permanentInventory", AllInstance);
                var inv = invProp?.GetValue(hunter) as ItemStorage;
                if (inv == null) return false;

                // Arrow item via WorkBucketManager
                var gm = UnitySingleton<GameManager>.Instance;
                var wbm = gm?.workBucketManager;
                var arrowItem = wbm?.itemArrow;
                if (arrowItem == null) return false;

                return inv.GetItemCount(arrowItem) > 0;
            }
            catch { return false; }
        }
    }
}
