using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Spawns and maintains a population of wild foxes that roam the map
    /// like wolves do, instead of only appearing as chicken-coop raiders.
    ///
    /// How it works:
    ///   1. On map load, find the vanilla Fox <c>AnimalGroupDefinition</c>
    ///      in <c>AnimalManager.spawnIntervalsByAnimalGroupDict</c>. This
    ///      is the same SO the DLC uses; we reuse it so the spawned fox
    ///      gets the right prefab, audio, and AI components.
    ///   2. A coroutine ticks every <c>WildFoxRespawnIntervalSeconds</c>
    ///      seconds (wall-clock, not scaled) and tops the population back
    ///      up to <c>WildFoxMaxCount</c> by calling
    ///      <c>AnimalManager.SpawnAnimal(group, point)</c> at random
    ///      shuffledEdgePoints.
    ///   3. Each spawned fox gets a <see cref="WotWWildFoxMarker"/>
    ///      attached so the despawn patch can distinguish wild foxes
    ///      from chicken-raid foxes.
    ///
    /// DLC-gated:
    ///   The Fox prefab + AnimalGroupDefinition only exist when the Pets
    ///   DLC asset bundle is loaded. <c>DlcDetection.PetsDlcActive</c>
    ///   gates the system; non-DLC players never run this code.
    ///
    /// Save/load behavior:
    ///   Wild foxes are spawned via the vanilla SpawnAnimal entry point,
    ///   which registers them with the animal manager. They serialize
    ///   like any other Fox. The marker MonoBehaviour does NOT persist
    ///   across save/load (it's a runtime tag), so on reload our patches
    ///   may briefly miss WotW-spawned foxes — but the top-up loop will
    ///   re-spawn fresh ones, and the old ones either revert to vanilla
    ///   behavior (DLC raiders) or eventually despawn naturally. Future
    ///   improvement: persist the marker via the spawnTag field.
    /// </summary>
    public static class WildFoxSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static AnimalGroupDefinition _foxGroupDef;
        private static bool _coroutineRunning;

        /// <summary>
        /// Called from <c>OnSceneWasLoaded</c>. Safe to call repeatedly —
        /// the coroutine starts only once per session, and the group-def
        /// lookup re-resolves each time in case the DLC asset bundle was
        /// loaded mid-session.
        /// </summary>
        public static void OnMapLoaded()
        {
            try
            {
                if (!DlcDetection.PetsDlcActive)
                {
                    // Non-DLC or feature disabled — never enter the spawn loop.
                    return;
                }

                if (!WardenOfTheWildsMod.WildFoxEnabled.Value)
                    return;

                if (_coroutineRunning) return;
                _coroutineRunning = true;
                MelonCoroutines.Start(SpawnLoop());
                MelonLogger.Msg(
                    "[WotW] WildFoxSystem: spawn loop scheduled — " +
                    $"target {WardenOfTheWildsMod.WildFoxMaxCount.Value} foxes, " +
                    $"refresh every {WardenOfTheWildsMod.WildFoxRespawnIntervalSeconds.Value}s.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] WildFoxSystem.OnMapLoaded: {ex.Message}");
            }
        }

        private static IEnumerator SpawnLoop()
        {
            // Initial delay: let the save deserialize and the animal manager
            // wire up. 30s wall-clock is conservative — the existing animal-
            // manager poll completes in 1-4s for fresh starts, longer for
            // heavy saves. Better to wait too long once than spawn into a
            // half-loaded scene.
            yield return new WaitForSecondsRealtime(30f);

            var wait = new WaitForSecondsRealtime(
                Mathf.Max(30f, WardenOfTheWildsMod.WildFoxRespawnIntervalSeconds.Value));

            while (true)
            {
                // Re-check the DLC + pref every tick so toggling at runtime
                // takes effect on the next pass (no need to restart the
                // game to disable wild foxes).
                if (!DlcDetection.PetsDlcActive
                    || !WardenOfTheWildsMod.WildFoxEnabled.Value)
                {
                    yield return wait;
                    continue;
                }

                try { TopUpPopulation(); }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[WotW] WildFoxSystem.TopUpPopulation: {ex.Message}");
                }
                yield return wait;
            }
        }

        private static void TopUpPopulation()
        {
            var animalManager = UnitySingleton<GameManager>.Instance?.animalManager;
            if (animalManager == null) return;

            int target = Mathf.Max(0, WardenOfTheWildsMod.WildFoxMaxCount.Value);
            if (target == 0) return;

            EnsureFoxGroupDefResolved(animalManager);
            if (_foxGroupDef == null) return;

            // Count currently-living wild foxes
            int current = CountLivingWildFoxes(animalManager);
            int needed  = target - current;
            if (needed <= 0) return;

            // Map-edge spawn points (same pool wolves use)
            var pointsField = animalManager.GetType().GetField(
                "shuffledEdgePoints", AllInstance);
            var edgePoints = pointsField?.GetValue(animalManager) as List<Vector3>;
            if (edgePoints == null || edgePoints.Count == 0) return;

            int spawned = 0;
            for (int i = 0; i < needed; i++)
            {
                Vector3 pt = edgePoints[UnityEngine.Random.Range(0, edgePoints.Count)];
                GameObject go = animalManager.SpawnAnimal(_foxGroupDef, pt);
                if (go == null) continue;

                go.AddComponent<WotWWildFoxMarker>();
                spawned++;
            }

            if (spawned > 0)
                MelonLogger.Msg(
                    $"[WotW] WildFox: spawned {spawned} (population {current}→{current + spawned} " +
                    $"of {target}).");
        }

        private static int CountLivingWildFoxes(AnimalManager animalManager)
        {
            int count = 0;
            try
            {
                var listProp = animalManager.GetType().GetProperty("foxesRO",
                    BindingFlags.Public | BindingFlags.Instance);
                var foxes = listProp?.GetValue(animalManager)
                    as System.Collections.IEnumerable;
                if (foxes == null) return 0;

                foreach (var fox in foxes)
                {
                    var go = (fox as Component)?.gameObject;
                    if (go == null) continue;
                    if (go.GetComponent<WotWWildFoxMarker>() != null) count++;
                }
            }
            catch { /* return whatever we counted */ }
            return count;
        }

        /// <summary>
        /// Reflects <c>AnimalManager.spawnIntervalsByAnimalGroupDict</c> and
        /// finds the entry whose <c>animalType == AnimalType.Fox</c>. Cached
        /// once successful — the SO ref is stable for the session.
        /// </summary>
        private static void EnsureFoxGroupDefResolved(object animalManager)
        {
            if (_foxGroupDef != null) return;

            try
            {
                var dictField = animalManager.GetType().GetField(
                    "spawnIntervalsByAnimalGroupDict", AllInstance);
                var dict = dictField?.GetValue(animalManager)
                    as System.Collections.IDictionary;
                if (dict == null) return;

                foreach (var key in dict.Keys)
                {
                    if (key == null) continue;
                    var typeProp = key.GetType().GetProperty("animalType", AllInstance);
                    if (typeProp == null) continue;

                    string typeName = typeProp.GetValue(key)?.ToString();
                    if (typeName == "Fox")
                    {
                        _foxGroupDef = key as AnimalGroupDefinition;
                        MelonLogger.Msg(
                            "[WotW] WildFoxSystem: resolved Fox AnimalGroupDefinition.");
                        break;
                    }
                }

                if (_foxGroupDef == null)
                    MelonLogger.Warning(
                        "[WotW] WildFoxSystem: Fox AnimalGroupDefinition not found in " +
                        "spawn dict. Wild fox spawning disabled this session.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] WildFoxSystem.EnsureFoxGroupDefResolved: {ex.Message}");
            }
        }
    }
}
