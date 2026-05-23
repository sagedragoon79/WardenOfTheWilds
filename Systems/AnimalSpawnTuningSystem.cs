using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Scales per-species animal spawn counts via config multipliers.
    ///
    /// Vanilla design:
    ///   AnimalGroupDefinition is a ScriptableObject — one asset per animal
    ///   type (Bear, Wolf, Boar, Deer). Each carries:
    ///     _maxAnimalCount    — population cap on the map
    ///     _initialSpawnCount — how many appear at game start
    ///     _spawnIntervalInDays — gap between spawn attempts
    ///
    /// Our approach:
    ///   On each map load, walk the AnimalManager's group dictionary and apply
    ///   the configured multipliers. ScriptableObject mutations persist for
    ///   the game session, so we cache originals on first touch and always
    ///   compute target = original × current_config. This keeps config edits
    ///   mid-session safe (no compounding).
    /// </summary>
    public static class AnimalSpawnTuningSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Cached originals per ScriptableObject instance
        private static readonly Dictionary<int, int> _origMaxCount =
            new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _origInitialCount =
            new Dictionary<int, int>();

        // ── DLC originals (per AnimalManager instance) ──────────────────────
        // The DLC fields live on AnimalManager directly, not on the per-
        // species ScriptableObject. We cache originals on first sighting
        // so map-load reapplication doesn't compound multipliers.
        private static int? _origFoxSpawnDelayDays;
        private static int? _origGroundhogSpawnDelayDays;
        // AnimationCurve keys cached as raw arrays — Keyframe is a struct
        // and AnimationCurve.keys gives us a fresh copy on each access.
        private static Keyframe[] _origFoxGroupsCurve;
        private static Keyframe[] _origGroundhogGroupsCurve;

        public static void OnMapLoaded()
        {
            try
            {
                var animalManager = UnitySingleton<GameManager>.Instance?.animalManager;
                if (animalManager == null)
                {
                    MelonLogger.Warning(
                        "[WotW] AnimalSpawnTuning: animalManager unavailable.");
                    return;
                }

                // Reflect the group dictionary — it's protected
                var dictField = animalManager.GetType().GetField(
                    "spawnIntervalsByAnimalGroupDict", AllInstance);
                var dict = dictField?.GetValue(animalManager)
                    as System.Collections.IDictionary;
                if (dict == null)
                {
                    MelonLogger.Warning(
                        "[WotW] AnimalSpawnTuning: group dictionary not found.");
                }
                else
                {
                    foreach (var key in dict.Keys)
                    {
                        if (key == null) continue;
                        ApplyTuning(key);
                    }
                }

                // DLC tuning (fox + groundhog) — runs only when Pets DLC is
                // active. The fields exist on AnimalManager in both pre- and
                // post-DLC builds, so reflection is safe either way; we just
                // gate execution on ownership so non-DLC sessions stay clean.
                if (DlcDetection.PetsDlcActive)
                    ApplyDlcSpawnTuning(animalManager);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] AnimalSpawnTuningSystem: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies fox + groundhog spawn-delay and group-count tuning to the
        /// live AnimalManager. Caches originals on first run so subsequent
        /// map loads (or mid-session config changes) recompute from the true
        /// baseline rather than compounding.
        /// </summary>
        private static void ApplyDlcSpawnTuning(object animalManager)
        {
            try
            {
                Type t = animalManager.GetType();

                // ── Spawn delays ─────────────────────────────────────────────
                ApplyDelayField(animalManager, t, "foxSpawnDelayInDays",
                    WardenOfTheWildsMod.FoxSpawnDelayDays.Value,
                    ref _origFoxSpawnDelayDays, "Fox");

                ApplyDelayField(animalManager, t, "groundhogSpawnDelayInDays",
                    WardenOfTheWildsMod.GroundhogSpawnDelayDays.Value,
                    ref _origGroundhogSpawnDelayDays, "Groundhog");

                // ── Group-count curves ───────────────────────────────────────
                ApplyCurveMultiplier(animalManager, t, "maxFoxGroupsByChickenCount",
                    WardenOfTheWildsMod.FoxSpawnMultiplier.Value,
                    ref _origFoxGroupsCurve, "Fox");

                ApplyCurveMultiplier(animalManager, t, "maxGroundhogGroupsByFieldCount",
                    WardenOfTheWildsMod.GroundhogSpawnMultiplier.Value,
                    ref _origGroundhogGroupsCurve, "Groundhog");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyDlcSpawnTuning: {ex.Message}");
            }
        }

        /// <summary>
        /// Overrides an integer spawn-delay field if the configured value is
        /// non-negative (-1 = "leave vanilla alone"). Caches the original on
        /// first sighting.
        /// </summary>
        private static void ApplyDelayField(
            object animalManager, Type t, string fieldName,
            int configValue, ref int? cachedOriginal, string label)
        {
            var field = t.GetField(fieldName, AllInstance);
            if (field == null)
            {
                MelonLogger.Warning(
                    $"[WotW] DLC spawn tuning: field '{fieldName}' not found on AnimalManager.");
                return;
            }

            int current = (int)field.GetValue(animalManager);
            if (!cachedOriginal.HasValue) cachedOriginal = current;

            // -1 = sentinel for "don't touch; restore vanilla if we'd
            // previously written something."
            int target = configValue < 0 ? cachedOriginal.Value : configValue;
            if (target == current) return;

            field.SetValue(animalManager, target);
            MelonLogger.Msg(
                $"[WotW] {label} spawn delay: {current} → {target} days " +
                $"(vanilla baseline {cachedOriginal.Value}).");
        }

        /// <summary>
        /// Scales every keyframe value in an AnimationCurve by the configured
        /// multiplier. Original keys are cached on first sighting so map-load
        /// reapplication uses a stable baseline.
        /// </summary>
        private static void ApplyCurveMultiplier(
            object animalManager, Type t, string fieldName,
            float multiplier, ref Keyframe[] cachedOriginal, string label)
        {
            var field = t.GetField(fieldName, AllInstance);
            if (field == null)
            {
                MelonLogger.Warning(
                    $"[WotW] DLC spawn tuning: field '{fieldName}' not found on AnimalManager.");
                return;
            }

            var curve = field.GetValue(animalManager) as AnimationCurve;
            if (curve == null) return;

            if (cachedOriginal == null)
            {
                // Deep-copy keyframes — AnimationCurve.keys returns a fresh
                // array but the Keyframe struct values are copied by value,
                // so this snapshot is independent of further mutations.
                cachedOriginal = (Keyframe[])curve.keys.Clone();
            }

            // No-op when multiplier == 1.0 and the curve already matches
            // baseline (re-applying after mid-session config edit).
            if (Mathf.Approximately(multiplier, 1.0f))
            {
                // Restore baseline if we'd previously scaled it.
                bool matchesBaseline = curve.keys.Length == cachedOriginal.Length;
                for (int i = 0; matchesBaseline && i < cachedOriginal.Length; i++)
                    if (!Mathf.Approximately(curve.keys[i].value, cachedOriginal[i].value))
                        matchesBaseline = false;

                if (matchesBaseline) return;
            }

            // Rebuild curve with scaled values
            var newKeys = new Keyframe[cachedOriginal.Length];
            for (int i = 0; i < cachedOriginal.Length; i++)
            {
                Keyframe k = cachedOriginal[i];
                k.value = k.value * multiplier;
                newKeys[i] = k;
            }
            curve.keys = newKeys;
            field.SetValue(animalManager, curve);

            MelonLogger.Msg(
                $"[WotW] {label} group-count curve scaled by {multiplier:F2}× " +
                $"({cachedOriginal.Length} keyframes).");
        }

        /// <summary>
        /// For a given AnimalGroupDefinition (passed as object to avoid tight
        /// type coupling), read its animalType, map to our config multiplier,
        /// and scale the ScriptableObject's serialized fields.
        /// </summary>
        private static void ApplyTuning(object group)
        {
            try
            {
                Type groupType = group.GetType();

                // Read animalType
                var animalTypeProp = groupType.GetProperty("animalType", AllInstance);
                if (animalTypeProp == null) return;
                var animalType = animalTypeProp.GetValue(group);
                string typeName = animalType?.ToString() ?? "";

                float mult = GetMultiplierFor(typeName);

                int instanceId = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(group);

                // Scale _maxAnimalCount only — gives populations room to grow
                // over time without causing a map-load spawn-flurry stutter.
                // _initialSpawnCount is intentionally NOT scaled; the game
                // instantiates that many animals at once on map load, and
                // scaling it produced a visible 15-20s stutter as the extra
                // bears/wolves spawned with their full component stacks.
                ScaleField(group, groupType, "_maxAnimalCount", mult,
                    _origMaxCount, instanceId, out int oldMax, out int newMax);

                if (oldMax != newMax)
                {
                    MelonLogger.Msg(
                        $"[WotW] {typeName} spawn tuned: " +
                        $"maxCount {oldMax} → {newMax} (mult {mult:F2}×, " +
                        $"initial count unchanged for load-time smoothness)");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyTuning: {ex.Message}");
            }
        }

        private static float GetMultiplierFor(string typeName)
        {
            switch (typeName)
            {
                case "Bear":      return WardenOfTheWildsMod.BearSpawnMultiplier.Value;
                case "Wolf":      return WardenOfTheWildsMod.WolfSpawnMultiplier.Value;
                case "Boar":      return WardenOfTheWildsMod.BoarSpawnMultiplier.Value;
                case "Deer":      return WardenOfTheWildsMod.DeerSpawnMultiplier.Value;
                default:          return 1.0f;  // Other species untouched
            }
        }

        /// <summary>
        /// Scales an int backing field. Caches the original on first touch so
        /// repeated map loads don't compound the multiplier.
        /// </summary>
        private static void ScaleField(
            object group, Type type, string fieldName, float mult,
            Dictionary<int, int> originals, int instanceId,
            out int oldValue, out int newValue)
        {
            oldValue = newValue = 0;

            var field = type.GetField(fieldName, AllInstance);
            if (field == null) return;

            // Cache original on first sighting
            if (!originals.ContainsKey(instanceId))
            {
                int current = (int)field.GetValue(group);
                originals[instanceId] = current;
            }

            int original = originals[instanceId];
            oldValue = (int)field.GetValue(group);
            newValue = Mathf.Max(0, Mathf.RoundToInt(original * mult));

            if (oldValue != newValue)
                field.SetValue(group, newValue);
        }
    }
}
