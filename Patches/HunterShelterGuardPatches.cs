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

                // Threat present — block emergence. Clear forcedOut since we're
                // saying "still hiding, not forced out."
                __result = false;
                forcedOutFromHiding = false;

                int vKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(villager);
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
    }
}
