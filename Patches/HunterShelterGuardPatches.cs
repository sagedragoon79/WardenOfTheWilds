using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Hunter-specific shelter guard: keeps a sheltering hunter inside their
    /// cabin while ANY aggressive animal (wolf, boar, bear) is nearby.
    ///
    /// Why this is needed:
    ///   Vanilla StayInShelterSubTask.HideFromEnemies() searches
    ///   Team.Raiders | Team.Bears only. Wolves and boars aren't in that
    ///   mask at all, so a wolf can stand right next to the cabin and the
    ///   hunter will emerge anyway (literally invisible to the check).
    ///   Extending HunterShelterSearchRadius alone does nothing because the
    ///   search doesn't even look for wolves.
    ///
    /// How this works:
    ///   Postfix on StayInShelterSubTask.CanLeaveBuilding. If vanilla says
    ///   the hunter can emerge, we do our own AggressiveAnimal proximity
    ///   scan. If any is within HunterShelterSearchRadius, flip the result
    ///   back to false. Hunter stays put until the threat has genuinely
    ///   moved off.
    ///
    /// Only affects hunters — non-hunter villagers hide per vanilla rules.
    /// </summary>
    [HarmonyPatch(typeof(StayInShelterSubTask), "CanLeaveBuilding")]
    internal static class HunterShelterGuardPatch
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Reflection cache
        private static FieldInfo? _villagerField = null;
        private static bool       _villagerFieldDone = false;

        // Suppress spam — log at most once per hunter per second
        private static readonly System.Collections.Generic.Dictionary<int, float>
            _lastLog = new System.Collections.Generic.Dictionary<int, float>();

        // ── Cabin Defense Fire registry ──────────────────────────────────────
        // Hunters currently dispatched out for cabin defense, mapped to the
        // cabin position they should return to after firing. Read by
        // HunterCombatPatches.OnPerformedAttackPostfix to issue the recall.
        // Cleared on attack-completion or when threat is gone.
        public static readonly System.Collections.Generic.Dictionary<int, Vector3>
            CabinDefenders = new System.Collections.Generic.Dictionary<int, Vector3>();

        // Per-hunter cooldown so we don't spam SetTarget every shelter tick.
        // 1.5s lets the attack animation play + projectile fly before re-issue.
        private static readonly System.Collections.Generic.Dictionary<int, float>
            _lastDefenseDispatch = new System.Collections.Generic.Dictionary<int, float>();
        private const float DefenseDispatchCooldown = 1.5f;

        // Animal-list cost is handled by HunterCombatPatches.GetCachedAggressiveAnimals
        // (shared 0.75s TTL cache). Per-hunter rate-limiting isn't needed on
        // top — the per-frame cost is now just a distance check loop.

        [HarmonyPostfix]
        public static void Postfix(
            object __instance,
            ref bool __result,
            ref bool forcedOutFromHiding)
        {
            if (!__result) return;  // vanilla says stay → nothing to do

            try
            {
                // Resolve villager field once
                if (!_villagerFieldDone)
                {
                    _villagerFieldDone = true;
                    Type? t = __instance.GetType();
                    while (t != null && t.Name != "Object")
                    {
                        _villagerField = t.GetField("villager", AllInstance);
                        if (_villagerField != null) break;
                        t = t.BaseType;
                    }
                }
                if (_villagerField == null) return;

                var villager = _villagerField.GetValue(__instance) as Villager;
                if (villager == null) return;

                // Only guard hunters — non-hunter villagers keep vanilla rules
                if (!HunterCombatPatches.IsAnyHunterPublic(villager)) return;

                float radius = WardenOfTheWildsMod.HunterShelterSearchRadius.Value;
                if (radius <= 0f) return;
                float radiusSqr = radius * radius;
                Vector3 pos = villager.transform.position;

                // Scan for any aggressive animal in range. Vanilla only checks
                // Raiders + Bears — we add Wolves, Boars, and anything else
                // deriving from AggressiveAnimal (the common base class).
                Component? nearest = null;
                float nearestSqr = float.MaxValue;
                foreach (var animal in HunterCombatPatches.GetCachedAggressiveAnimals())
                {
                    if (animal == null) continue;
                    var comp = animal as Component;
                    if (comp == null) continue;
                    float dSqr = (comp.transform.position - pos).sqrMagnitude;
                    if (dSqr <= radiusSqr && dSqr < nearestSqr)
                    {
                        nearestSqr = dSqr;
                        nearest = comp;
                    }
                }

                if (nearest == null) return;  // no threats → let vanilla decision stand

                int vKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(villager);

                // ── Cabin Defense Fire branch ──────────────────────────────
                // Threat is in range. Before locking the hunter inside, check
                // if they can fire a defense shot instead of cowering. If
                // yes: let them out, command attack, register for recall.
                if (TryDispatchDefenseFire(villager, nearest, vKey, pos))
                {
                    // Defense dispatched — let vanilla allow them out for
                    // combat. Don't alter __result / forcedOut; vanilla's
                    // own combat-priority logic will handle the exit.
                    return;
                }

                // Threat present + defense not eligible — block emergence.
                __result = false;
                forcedOutFromHiding = false;

                if (!_lastLog.TryGetValue(vKey, out float last)
                    || Time.time - last > 5f)
                {
                    _lastLog[vKey] = Time.time;
                    MelonLogger.Msg(
                        $"[WotW] Shelter guard: '{villager.gameObject.name}' " +
                        $"staying inside — '{nearest.gameObject.name}' at " +
                        $"{Mathf.Sqrt(nearestSqr):F0}u");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterShelterGuardPatch: {ex.Message}");
            }
        }

        // ── Cabin Defense Fire dispatch ──────────────────────────────────────
        //
        // Decides whether a sheltering hunter should step out and fire on a
        // nearby threat. If yes, issues a SetTarget command and registers the
        // hunter in CabinDefenders so HunterCombatPatches.OnPerformedAttack
        // can recall them to the cabin after their shot.
        //
        // Returns true when defense was dispatched (caller should let vanilla
        // allow the hunter out for combat). False when not eligible (caller
        // applies the normal stay-sheltered behaviour).
        private static bool TryDispatchDefenseFire(
            Villager villager, Component threat, int vKey, Vector3 cabinPos)
        {
            try
            {
                if (!WardenOfTheWildsMod.HunterCabinDefenseEnabled.Value) return false;

                // Per-hunter cooldown so we don't re-issue SetTarget on every
                // shelter-tick while the previous shot is in flight.
                if (_lastDefenseDispatch.TryGetValue(vKey, out float lastTime)
                    && Time.time - lastTime < DefenseDispatchCooldown)
                {
                    // Already dispatched recently — leave them on their task.
                    return true;
                }

                // HP gate — wounded hunters stay sheltered.
                float minHp = WardenOfTheWildsMod.HunterCabinDefenseMinHp.Value;
                if (minHp > 0f)
                {
                    float hp = HunterCombatPatches.GetHunterHealthPercentPublic(villager);
                    if (hp >= 0f && hp < minHp) return false;
                }

                // Range gate — short-range "tower" defense, not full hunting reach.
                // Two bounds:
                //   floor (default 20u) — too close = hunter steps into melee on emerge
                //   ceiling (default 30u) — too far = out of safe bow range
                float radius   = Mathf.Max(5f, WardenOfTheWildsMod.HunterCabinDefenseRadius.Value);
                float minDist  = Mathf.Max(0f, WardenOfTheWildsMod.HunterCabinDefenseMinDist.Value);
                float dSqr = (threat.transform.position - cabinPos).sqrMagnitude;
                if (dSqr > radius  * radius)  return false; // out of range
                if (dSqr < minDist * minDist) return false; // too close — let walls absorb

                // Issue the attack command. Vanilla combat AI handles
                // movement-to-target, attack animation, projectile.
                var combatComp = villager.GetComponent<CombatComponent>();
                var dmg = threat.GetComponent<IDamageable>()
                       ?? threat.GetComponentInChildren<IDamageable>();
                if (combatComp == null || dmg == null) return false;

                combatComp.SetTarget(
                    newTarget: dmg,
                    newTargetCombatAction: CombatAction.Attack,
                    newTargetSourceIdentifier: TargetSourceIdentifier.Search);

                CabinDefenders[vKey] = cabinPos;
                _lastDefenseDispatch[vKey] = Time.time;

                if (!_lastLog.TryGetValue(vKey, out float lastLogT)
                    || Time.time - lastLogT > 5f)
                {
                    _lastLog[vKey] = Time.time;
                    MelonLogger.Msg(
                        $"[WotW] Cabin defense: '{villager.gameObject.name}' " +
                        $"engaging '{threat.gameObject.name}' at " +
                        $"{Mathf.Sqrt(dSqr):F0}u from cabin (radius={radius:F0}u).");
                }
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TryDispatchDefenseFire: {ex.Message}");
                return false;
            }
        }
    }
}
