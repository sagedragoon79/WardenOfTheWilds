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
                    return;
                }

                foreach (var key in dict.Keys)
                {
                    if (key == null) continue;
                    ApplyTuning(key);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] AnimalSpawnTuningSystem: {ex.Message}");
            }
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
                if (Mathf.Approximately(mult, 1.0f)) return;  // no-op

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
