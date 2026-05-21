using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Patches <c>DespawnFoxSearchEntry.WantsToRetreat</c> so WotW-spawned
    /// wild foxes don't despawn after a "kill count" or year-end trigger.
    ///
    /// Vanilla DLC behavior: a fox spawns to raid chickens, kills N chickens
    /// (configurable), then triggers WantsToRetreat → flees back to spawn
    /// point → despawns. Year-end cleanup also despawns active raiders.
    ///
    /// WotW wild foxes are wildlife — they should persist on the map and
    /// roam like wolves, not flee after eating a few birds. We let the HP-
    /// based retreat through (a wounded fox SHOULD flee) but suppress the
    /// other two triggers.
    ///
    /// Identification: any fox with a <see cref="WotWWildFoxMarker"/>
    /// MonoBehaviour attached is ours. Untagged foxes (vanilla DLC raiders)
    /// flow through unchanged.
    ///
    /// Soft-fail: if any reflection or type lookup misses, we fall through
    /// to vanilla — never block the original method.
    /// </summary>
    internal static class WildFoxDespawnPatch
    {
        private static MethodInfo _patched;
        private static FieldInfo  _foxField;
        private static PropertyInfo _lifePctProp;

        /// <summary>Configured retreat threshold from the vanilla class
        /// (lifePercToRetreat = 0.6f). Hard-coded as a fallback if the
        /// field can't be reflected; matches DLC v1.0 vanilla.</summary>
        private const float FallbackLifePercToRetreat = 0.6f;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type entryType = AccessTools.TypeByName("DespawnFoxSearchEntry");
                if (entryType == null)
                {
                    MelonLogger.Msg(
                        "[WotW] WildFoxDespawnPatch: DespawnFoxSearchEntry type " +
                        "not found (pre-DLC build?). Skipping — non-issue.");
                    return;
                }

                _patched = AccessTools.Method(entryType, "WantsToRetreat");
                if (_patched == null)
                {
                    MelonLogger.Warning(
                        "[WotW] WildFoxDespawnPatch: WantsToRetreat method not found.");
                    return;
                }

                _foxField = AccessTools.Field(entryType, "fox");

                Type foxType = AccessTools.TypeByName("Fox");
                if (foxType != null)
                {
                    // damageableComp.lifePercentage  — read for HP retreat
                    // We don't bind this directly; reflection occurs in Prefix
                    // to stay resilient to renames.
                }

                harmony.Patch(_patched,
                    prefix: new HarmonyMethod(typeof(WildFoxDespawnPatch),
                                              nameof(WantsToRetreatPrefix)));
                MelonLogger.Msg(
                    "[WotW] WildFoxDespawnPatch: patched DespawnFoxSearchEntry.WantsToRetreat");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] WildFoxDespawnPatch.Register: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on WantsToRetreat. Returns false (skip vanilla) only when
        /// the receiver is a WotW wild fox; otherwise returns true and the
        /// vanilla implementation runs.
        /// </summary>
        public static bool WantsToRetreatPrefix(object __instance, ref bool __result)
        {
            try
            {
                if (__instance == null || _foxField == null) return true;
                var fox = _foxField.GetValue(__instance) as Component;
                if (fox == null) return true;

                // Vanilla raider — flow through to original logic.
                if (fox.GetComponent<WotWWildFoxMarker>() == null) return true;

                // v1.0.14 — Wild fox NEVER retreats. Wildlife wolves don't,
                // wildlife foxes shouldn't either. Originally we kept the
                // HP-based retreat thinking "wounded animal flees" was
                // realistic, but the downstream effect is that ANY damage
                // (hunter's first arrow, dog's first bite) immediately
                // triggers the despawn-via-retreat-to-spawn-point flow, and
                // the fox vanishes at whatever HP it was when it reached
                // the despawn point. Result: hunters never get a kill,
                // wild fox population craters to 0 between top-up ticks.
                //
                // Now: marked wild foxes fight in place until dead. Vanilla
                // chicken-raid foxes (untagged) flow through unchanged.
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] WildFoxDespawnPatch.WantsToRetreatPrefix: {ex.Message}");
                return true;
            }
        }

        private static float ReadLifePercentage(Component fox)
        {
            try
            {
                // fox.damageableComp.lifePercentage — both members exposed
                // public per Fox/AggressiveAnimal API.
                var damageableProp = fox.GetType().GetProperty("damageableComp")
                    ?? fox.GetType().BaseType?.GetProperty("damageableComp");
                if (damageableProp == null) return 1f;

                var damageable = damageableProp.GetValue(fox);
                if (damageable == null) return 1f;

                var lifePctProp = damageable.GetType().GetProperty("lifePercentage");
                if (lifePctProp == null) return 1f;

                object val = lifePctProp.GetValue(damageable);
                return val is float f ? f : 1f;
            }
            catch { return 1f; }
        }
    }
}
