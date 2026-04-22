using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  TendedWildsCompat
//  Soft bridge to Tended Wilds companion mod.
//  All methods are safe to call regardless of whether TW is loaded —
//  they return neutral/zero values when TW is absent.
//
//  Communication pattern:
//    This class uses reflection to call TendedWildsAPI (public static class
//    added to TendedWilds.dll as part of this companion update).
//    No compile-time reference to TendedWilds.dll is required — the bridge
//    is fully runtime-resolved, so Stalk & Smoke runs fine without TW.
//
//  Methods provided:
//    GetAttractionBonusNear(pos, radius)
//      → returns a float multiplier (1.0 = no bonus) based on cultivated
//        berries or greens within radius of a Deer Stand.
//
//    GetWillowStockNear(pos, radius)
//      → returns an int count of willow units available at ForagerShacks
//        within radius. Used to gate cheaper willow trap crafting.
//
//    GetHerbStockNear(pos, radius)
//      → returns int count of herbs/mushrooms available nearby.
//        Used by SmokehouseEnhancement.ShouldHerbCure().
//
//    ApplyFishOilFertilizer(shackPosition, bonusMultiplier)
//      → calls TendedWildsAPI.ApplyReplenishmentBonus() on the ForagerShack
//        at the given position. Called when Fish Oil is consumed as fertilizer.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Systems
{
    public static class TendedWildsCompat
    {
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ── Cached API type (resolved once per scene load) ────────────────────
        private static System.Type? _apiType = null;
        private static bool _resolved = false;

        public static void OnMapLoaded()
        {
            _apiType = null;
            _resolved = false;
        }

        private static System.Type? GetAPI()
        {
            if (_resolved) return _apiType;
            _resolved = true;

            if (!WardenOfTheWildsMod.TendedWildsActive) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // TendedWildsAPI lives in the TendedWilds namespace
                var t = asm.GetType("TendedWilds.TendedWildsAPI");
                if (t != null)
                {
                    _apiType = t;
                    MelonLogger.Msg("[WotW] TendedWildsCompat: TendedWildsAPI resolved.");
                    return _apiType;
                }
            }

            MelonLogger.Warning(
                "[WotW] TendedWildsCompat: Tended Wilds active but TendedWildsAPI " +
                "type not found. Ensure Tended Wilds is updated to v1.1.0+.");
            return null;
        }

        // ── Public API wrappers ───────────────────────────────────────────────

        /// <summary>
        /// Returns an attraction multiplier (≥1.0) for a Deer Stand at position.
        /// Bonus comes from cultivated berry bushes or greens within radius
        /// at nearby ForagerShacks (read via TendedWildsAPI).
        /// Returns 1.0 if TW not active or no relevant cultivated plants found.
        /// </summary>
        public static float GetAttractionBonusNear(Vector3 position, float radius)
        {
            var api = GetAPI();
            if (api == null) return 1.0f;

            try
            {
                var method = api.GetMethod("GetAttractionBonusNear", AllStatic);
                if (method == null) return 1.0f;
                var result = method.Invoke(null, new object[] { position, radius });
                return result is float f ? f : 1.0f;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] GetAttractionBonusNear: {ex.Message}");
                return 1.0f;
            }
        }

        /// <summary>
        /// Returns the count of willow units available at ForagerShacks near pos.
        /// Used to gate reduced willow cost for crafting hunting traps.
        /// </summary>
        public static int GetWillowStockNear(Vector3 position, float radius)
        {
            var api = GetAPI();
            if (api == null) return 0;

            try
            {
                var method = api.GetMethod("GetWillowStockNear", AllStatic);
                if (method == null) return 0;
                var result = method.Invoke(null, new object[] { position, radius });
                return result is int i ? i : 0;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] GetWillowStockNear: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Returns the count of herb/mushroom units available at ForagerShacks near pos.
        /// Used by SmokehouseEnhancement.ShouldHerbCure() to gate herb-cured smoking.
        /// </summary>
        public static int GetHerbStockNear(Vector3 position, float radius)
        {
            var api = GetAPI();
            if (api == null) return 0;

            try
            {
                var method = api.GetMethod("GetHerbStockNear", AllStatic);
                if (method == null) return 0;
                var result = method.Invoke(null, new object[] { position, radius });
                return result is int i ? i : 0;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] GetHerbStockNear: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Applies a fish oil fertilizer bonus to the ForagerShack nearest to pos.
        /// Calls TendedWildsAPI.ApplyReplenishmentBonus() with the given multiplier
        /// and a duration expressed in game months.
        /// </summary>
        public static void ApplyFishOilFertilizer(Vector3 nearPosition, float radius,
            float multiplier = 1.25f, int durationMonths = 2)
        {
            var api = GetAPI();
            if (api == null) return;

            try
            {
                var method = api.GetMethod("ApplyReplenishmentBonus", AllStatic);
                if (method == null)
                {
                    MelonLogger.Warning("[WotW] ApplyFishOilFertilizer: method not found in TendedWildsAPI.");
                    return;
                }

                // Find the nearest ForagerShack within radius
                System.Type? shackType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    shackType = asm.GetType("ForagerShack");
                    if (shackType != null) break;
                }
                if (shackType == null) return;

                float bestDist = radius * radius;
                Vector3 bestPos = Vector3.zero;
                bool found = false;

                foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(shackType))
                {
                    var comp = obj as Component;
                    if (comp == null) continue;
                    float dist = (comp.transform.position - nearPosition).sqrMagnitude;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPos  = comp.transform.position;
                        found    = true;
                    }
                }

                if (!found) return;

                method.Invoke(null, new object[] { bestPos, multiplier, durationMonths });
                MelonLogger.Msg(
                    $"[WotW] Fish Oil fertilizer applied to ForagerShack at {bestPos} " +
                    $"(×{multiplier:F2} for {durationMonths} months)");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyFishOilFertilizer: {ex.Message}");
            }
        }
    }
}
