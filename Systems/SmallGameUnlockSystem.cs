using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Restores foxes and groundhogs to the game world by overriding the
    /// vanilla spawn delays (1500 days each = effectively never) with
    /// playable values.
    ///
    /// Vanilla state (per AnimalManager decompile):
    ///   foxSpawnDelayInDays = 1500
    ///   groundhogSpawnDelayInDays = 1500
    ///   foxKillCountToDespawn = 1500   (kept — controls end-of-season)
    ///
    /// Both species have COMPLETE behaviour implementations in game code:
    ///   - Fox: extends AggressiveAnimal, wander + crop pursuit + retreat,
    ///     attackTime = 9999f so they never attack villagers
    ///   - FoxWanderSearchEntry + FoxWanderSubTask exist with full AI
    ///   - Groundhog: similar unrated passive-wander class
    ///
    /// Crate gated them off via the delay knobs, not by removing logic —
    /// which means overriding the delays is all it takes to enable them.
    /// </summary>
    public static class SmallGameUnlockSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── DORMANT (April 2026): Cat & Dog DLC announced ─────────────────────
        //
        // Crate announced the Cat & Dog DLC, in which Foxes (chicken predators)
        // and Groundhogs (crop pests) are first-class antagonists, balanced by
        // Crate. Our previous override pipeline (delay 1500→90, spawn-interval
        // 220→60, _minSpawnDistance 500→100, presence-counter seeding) was
        // designed to bypass the dormant 1500-day delay that gated these
        // species pre-DLC.
        //
        // With the DLC ON THE WAY, Crate will ship their own balancing on these
        // creatures. If our overrides remained active, they'd clobber whatever
        // tuning the DLC chooses — potentially flooding the player's town with
        // foxes/groundhogs at speeds Crate didn't design for.
        //
        // To prevent that scenario for users who have UnlockFoxSpawns=true /
        // UnlockGroundhogSpawns=true cached in their prefs from older versions
        // of WotW, the entire system is short-circuited here regardless of
        // pref values. We'll revisit on DLC release: re-enable, dump the new
        // catalog data, and decide whether any overrides are still useful.
        //
        // The diagnostic patches (SpawnAnimals/UpdateAnimalGroups Harmony
        // tracing) are also skipped to avoid noise.
        private static bool _dormantNoticeLogged = false;

        public static void OnMapLoaded()
        {
            if (!_dormantNoticeLogged)
            {
                _dormantNoticeLogged = true;
                MelonLogger.Msg(
                    "[WotW] SmallGameUnlock: DORMANT pending Cat & Dog DLC. Fox/Groundhog " +
                    "spawn overrides skipped to avoid clobbering Crate's DLC balancing. " +
                    "Will re-evaluate on DLC release.");
            }
            return; // ← intentional early-out. Keep the rest of the file
                    //   functional for the day we re-enable.

#pragma warning disable CS0162 // Unreachable code: kept intentionally for fast re-enable
            try
            {
                var animalManager = UnitySingleton<GameManager>.Instance?.animalManager;
                if (animalManager == null)
                {
                    MelonLogger.Warning(
                        "[WotW] SmallGameUnlock: animalManager not available — skipping.");
                    return;
                }

                // Apply diagnostic patches once. Idempotent: Harmony's PatchAll
                // would re-patch — manual patching here checks first via a flag.
                ApplyDiagnosticPatchesOnce(animalManager.GetType());

                Type amType = animalManager.GetType();

                if (WardenOfTheWildsMod.UnlockFoxSpawns.Value)
                {
                    OverrideIntField(amType, animalManager,
                        "foxSpawnDelayInDays",
                        WardenOfTheWildsMod.FoxSpawnDelayDays.Value,
                        "fox");

                    // Pre-seed daysOfChickenPresence so saves with coops built
                    // BEFORE the mod was installed don't have to wait another
                    // 90 days after install. If the counter is already past
                    // the delay, leave it; otherwise fast-forward.
                    SeedPresenceCounter(amType, animalManager,
                        "daysOfChickenPresence",
                        WardenOfTheWildsMod.FoxSpawnDelayDays.Value,
                        "chicken");
                }

                if (WardenOfTheWildsMod.UnlockGroundhogSpawns.Value)
                {
                    OverrideIntField(amType, animalManager,
                        "groundhogSpawnDelayInDays",
                        WardenOfTheWildsMod.GroundhogSpawnDelayDays.Value,
                        "groundhog");

                    SeedPresenceCounter(amType, animalManager,
                        "daysOfFieldPresence",
                        WardenOfTheWildsMod.GroundhogSpawnDelayDays.Value,
                        "field");
                }

                // ── Override spawn intervals so fox/groundhog don't take 8+
                //    in-game months per spawn cycle. Vanilla intervals are 220d
                //    (Fox) and 240d (Groundhog), and the counter resets to 0
                //    on every save load — so the user effectively has to play
                //    most of an in-game year without saving to see one spawn.
                //    With config defaults (~60d) the cycle hits 2-3 months.
                OverrideSpawnIntervalsAndSeedCounters(animalManager, amType);

                // ── Diagnostic dump ───────────────────────────────────────
                DumpSpawnDiagnostic(animalManager, amType);

                // ── Live map scan ─────────────────────────────────────────
                ScanLiveAnimalCounts();

                // ── Direct-spawn fallback ────────────────────────────────
                // Vanilla SpawnAnimals fires correctly (gate passes, counter
                // resets) but every actual spawn returns 0 — the edge-point /
                // GetGroupSpawnPoints search exhausts without finding any
                // valid position on typical maps regardless of minSpawnDistance.
                // Bypass that broken path with SpawnAnimal(group, position)
                // which just snaps to nearest navmesh and instantiates.
                ForceInitialSpawn(animalManager, amType);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] SmallGameUnlockSystem: {ex.Message}");
            }
#pragma warning restore CS0162
        }

        /// <summary>
        /// Pre-seeds a "days-of-X-presence" counter so installs on
        /// existing saves don't wait another full delay period. If the
        /// counter is already past the threshold, leave it alone (don't
        /// rewind progress).
        /// </summary>
        private static void SeedPresenceCounter(
            Type type, object instance, string fieldName, int minValue, string label)
        {
            try
            {
                var field = type.GetField(fieldName, AllInstance);
                if (field == null) return;

                int current = (int)field.GetValue(instance);
                if (current >= minValue)
                {
                    MelonLogger.Msg(
                        $"[WotW] {label} presence already {current}d (≥ {minValue}d threshold).");
                    return;
                }

                // Fast-forward to one past the threshold so spawn gate passes
                // on the next check.
                int seeded = minValue + 1;
                field.SetValue(instance, seeded);
                MelonLogger.Msg(
                    $"[WotW] {label} presence seeded {current}d → {seeded}d " +
                    $"(unblocks spawn gate for existing saves).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] SeedPresenceCounter({fieldName}): {ex.Message}");
            }
        }

        // ── Diagnostic Harmony patches ────────────────────────────────────────
        // Trace SpawnAnimals + UpdateAnimalGroups so we can see WHY foxes /
        // groundhogs aren't appearing despite all the gates passing on paper.

        private static bool _diagPatchesApplied = false;

        private static void ApplyDiagnosticPatchesOnce(Type animalManagerType)
        {
            if (_diagPatchesApplied) return;
            _diagPatchesApplied = true;
            try
            {
                var harmony = WardenOfTheWildsMod.Instance?.HarmonyInstance;
                if (harmony == null) return;

                // SpawnAnimals(AnimalGroupDefinition, int, int) — fires when an
                // actual spawn happens. If we never see this for Fox/Groundhog,
                // the spawn gate isn't passing.
                MethodInfo spawnInt = AccessTools.Method(animalManagerType, "SpawnAnimals",
                    new[] { AccessTools.TypeByName("AnimalGroupDefinition"),
                            typeof(int), typeof(int) });
                if (spawnInt != null)
                {
                    harmony.Patch(spawnInt, postfix: new HarmonyMethod(
                        typeof(SmallGameUnlockSystem), nameof(SpawnAnimals_Postfix)));
                    MelonLogger.Msg("[WotW] Diag: patched AnimalManager.SpawnAnimals(def,int,int)");
                }

                // UpdateAnimalGroups — fires once per in-game day. Postfix logs
                // the per-day state of both species so we can see the gate
                // evaluation (counter, season, live count).
                MethodInfo updateMethod = AccessTools.Method(
                    animalManagerType, "UpdateAnimalGroups",
                    new[] { typeof(bool), typeof(bool) });
                if (updateMethod != null)
                {
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(
                        typeof(SmallGameUnlockSystem), nameof(UpdateAnimalGroups_Postfix)));
                    MelonLogger.Msg("[WotW] Diag: patched AnimalManager.UpdateAnimalGroups");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyDiagnosticPatchesOnce: {ex.Message}");
            }
        }

        public static void SpawnAnimals_Postfix(object animalGroup, int numAnimalsToSpawn, int __result)
        {
            try
            {
                if (animalGroup == null) return;
                var atProp = animalGroup.GetType().GetProperty("animalType", AllInstance);
                string at = atProp?.GetValue(animalGroup, null)?.ToString() ?? "?";
                if (at != "Fox" && at != "Groundhog") return;
                MelonLogger.Msg(
                    $"[WotW] SpawnAnimals fired — type={at} requested={numAnimalsToSpawn} " +
                    $"actuallySpawned={__result}");
            }
            catch { }
        }

        private static int _lastUpdateLogDay = -1;

        public static void UpdateAnimalGroups_Postfix(object __instance)
        {
            try
            {
                // Throttle: log once per in-game day at most. UpdateAnimalGroups
                // fires daily so this typically means one log line per day.
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm?.timeManager == null) return;
                var date = gm.timeManager.currentDate;
                int day = date.year * 360 + (date.month - 1) * 30 + Mathf.FloorToInt(date.day);
                if (day == _lastUpdateLogDay) return;
                _lastUpdateLogDay = day;

                Type amType = __instance.GetType();
                var dictField = amType.GetField("spawnIntervalsByAnimalGroupDict", AllInstance);
                var dict = dictField?.GetValue(__instance) as System.Collections.IDictionary;
                int foxCounter = -1, grhCounter = -1;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        if (entry.Key == null) continue;
                        var atProp = entry.Key.GetType().GetProperty("animalType", AllInstance);
                        var at = atProp?.GetValue(entry.Key, null)?.ToString() ?? "?";
                        if (at == "Fox") foxCounter = (int)entry.Value;
                        else if (at == "Groundhog") grhCounter = (int)entry.Value;
                    }
                }

                int liveFox = UnityEngine.Object.FindObjectsOfType<Fox>()?.Length ?? 0;
                int liveGrh = UnityEngine.Object.FindObjectsOfType<Groundhog>()?.Length ?? 0;

                // Season — read via reflection in case API changes
                string season = "?";
                try
                {
                    // GetSeason(out Season, out SubSeason)
                    var getSeasonMethod = gm.timeManager.GetType().GetMethod(
                        "GetSeason", AllInstance);
                    if (getSeasonMethod != null)
                    {
                        var prms = getSeasonMethod.GetParameters();
                        var args = new object[prms.Length];
                        getSeasonMethod.Invoke(gm.timeManager, args);
                        if (args.Length > 0) season = args[0]?.ToString() ?? "?";
                    }
                }
                catch { }

                MelonLogger.Msg(
                    $"[WotW] DayTick d{day} ({season}): " +
                    $"foxCounter={foxCounter}, foxLive={liveFox}, " +
                    $"grhCounter={grhCounter}, grhLive={liveGrh}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] UpdateAnimalGroups_Postfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Direct-spawn fallback for fox + groundhog. If the player has 0 live
        /// foxes/groundhogs after vanilla had a chance to spawn them, we force
        /// one of each at a reasonable position (near a chicken coop / crop
        /// field). Bypasses MiscUtilities.GetGroupSpawnPoints which fails on
        /// most maps even after our minSpawnDistance + interval overrides.
        /// </summary>
        private static void ForceInitialSpawn(object am, Type amType)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm == null) return;
                var rm = gm.resourceManager;
                if (rm == null) return;

                // Find Fox + Groundhog group definitions in validAnimalGroups
                var validField = amType.GetField("validAnimalGroups", AllInstance);
                var groups = validField?.GetValue(am) as System.Collections.IEnumerable;
                if (groups == null) return;
                object foxGroup = null, grhGroup = null;
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    var atProp = g.GetType().GetProperty("animalType", AllInstance);
                    var at = atProp?.GetValue(g, null)?.ToString();
                    if (at == "Fox") foxGroup = g;
                    else if (at == "Groundhog") grhGroup = g;
                }

                // Diagnostic: dump the animal prefab lists for Fox + Groundhog
                // so we can see if the AnimalGroupDefinition has the prefab data
                // Crate ships, or if it was gutted along with the spawn delay.
                DumpGroupPrefabs(foxGroup, "Fox");
                DumpGroupPrefabs(grhGroup, "Groundhog");

                // Compare against a KNOWN-WORKING group (Wolf spawns reliably).
                // If Wolf entries also have prefab=null, my field reading is
                // wrong and there's an Addressable-reference field I missed.
                // If Wolf entries have populated prefabs, then Fox/Groundhog
                // were specifically gutted and we need a load-by-key strategy.
                object wolfGroup = null, bearGroup = null;
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    var atProp = g.GetType().GetProperty("animalType", AllInstance);
                    var at = atProp?.GetValue(g, null)?.ToString();
                    if (at == "Wolf") wolfGroup = g;
                    else if (at == "Bear") bearGroup = g;
                }
                DumpGroupPrefabs(wolfGroup, "Wolf");
                DumpGroupPrefabs(bearGroup, "Bear");

                // Crate gutted the prefab refs on the AnimalGroupDefinition
                // (entry count = 3, every prefab null). Find the prefabs via
                // Resources.FindObjectsOfTypeAll which sees ALL loaded objects
                // including inactive prefab roots, and patch them back into
                // the entry list so vanilla's GetWeightedAnimalPrefab works.
                RestoreMissingPrefabs<Fox>(foxGroup, "Fox");
                RestoreMissingPrefabs<Groundhog>(grhGroup, "Groundhog");

                // Resolve SpawnAnimal(group, Vector3)
                MethodInfo spawnAnimal = AccessTools.Method(amType, "SpawnAnimal",
                    new[] { AccessTools.TypeByName("AnimalGroupDefinition"), typeof(Vector3) });
                if (spawnAnimal == null)
                {
                    MelonLogger.Warning("[WotW] ForceInitialSpawn: SpawnAnimal(def,Vector3) not found.");
                    return;
                }

                // Fox: only spawn if user wants foxes AND none currently exist.
                if (foxGroup != null && WardenOfTheWildsMod.UnlockFoxSpawns.Value
                    && (UnityEngine.Object.FindObjectsOfType<Fox>()?.Length ?? 0) == 0)
                {
                    Vector3 pos = PickFoxSpawnPosition(rm);
                    if (pos != Vector3.zero)
                    {
                        var spawned = spawnAnimal.Invoke(am, new object[] { foxGroup, pos }) as GameObject;
                        MelonLogger.Msg(
                            $"[WotW] ForceInitialSpawn: fox at ({pos.x:F0},{pos.z:F0}) " +
                            $"→ {(spawned != null ? "SUCCESS" : "FAILED")}");
                    }
                    else
                    {
                        MelonLogger.Msg(
                            "[WotW] ForceInitialSpawn: fox skipped (no chicken coop on map yet).");
                    }
                }

                if (grhGroup != null && WardenOfTheWildsMod.UnlockGroundhogSpawns.Value
                    && (UnityEngine.Object.FindObjectsOfType<Groundhog>()?.Length ?? 0) == 0)
                {
                    Vector3 pos = PickGroundhogSpawnPosition(rm);
                    if (pos != Vector3.zero)
                    {
                        var spawned = spawnAnimal.Invoke(am, new object[] { grhGroup, pos }) as GameObject;
                        MelonLogger.Msg(
                            $"[WotW] ForceInitialSpawn: groundhog at ({pos.x:F0},{pos.z:F0}) " +
                            $"→ {(spawned != null ? "SUCCESS" : "FAILED")}");
                    }
                    else
                    {
                        MelonLogger.Msg(
                            "[WotW] ForceInitialSpawn: groundhog skipped (no crop fields planted).");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ForceInitialSpawn: {ex.Message}");
            }
        }

        /// <summary>
        /// Crate disabled small-game spawning by setting `_animalEntries[i].prefab = null`
        /// on the AnimalGroupDefinition (in addition to the 1500-day delay). The
        /// shape stays — 3 entries, weight=50 each — but every prefab ref is null,
        /// so GetWeightedAnimalPrefab always returns null and SpawnAnimal silently
        /// returns 0. Restore the missing prefabs by searching loaded memory for
        /// any GameObject that has a Fox/Groundhog MonoBehaviour and writing it
        /// back into the entry's prefab field. After this, vanilla's spawn loop
        /// AND our direct-spawn fallback both work.
        /// </summary>
        private static void RestoreMissingPrefabs<T>(object group, string label) where T : Component
        {
            if (group == null) return;
            try
            {
                GameObject prefab = null;

                // Strategy 1: scan loaded memory for any GameObject with T component.
                T[] candidates = Resources.FindObjectsOfTypeAll<T>();
                if (candidates != null && candidates.Length > 0)
                {
                    foreach (var c in candidates)
                    {
                        if (c == null || c.gameObject == null) continue;
                        var go = c.gameObject;
                        if (!go.scene.IsValid() || go.scene.name == null)
                        {
                            prefab = go;
                            break;
                        }
                        if (prefab == null) prefab = go;
                    }
                }

                // Strategy 2: iterate GlobalAssets.prefabAssetMap.prefabAssetListRO.
                // This map is a ScriptableObject loaded via Resources.Load — its
                // prefab refs are populated by Unity's serialization system.
                if (prefab == null)
                {
                    prefab = FindPrefabInAssetMap<T>(label);
                }

                // Strategy 3: search ALL loaded GameObjects (not just those with
                // T component) by name. The Wolf/Bear comparison dump confirmed
                // the canonical naming convention is "AnimalWild_<species>01A":
                //   AnimalWild_Wolf01A, AnimalWild_Wolf01B, AnimalWild_Wolf01C
                //   AnimalWild_BearBlack01A_Adult, AnimalWild_BearGrizzly01A_Adult
                // Earlier searches keyed on T.GetComponent — if Crate moved the
                // Fox/Groundhog component to a child node, the root prefab won't
                // match GetComponent<Fox>(). Searching by name avoids that.
                if (prefab == null)
                {
                    prefab = FindPrefabByNamePrefix($"AnimalWild_{label}", label);
                }

                // Strategy 4: name-based PrefabAssetMap iteration. The map is
                // 1146 prefabs, definitely includes wildlife. If the prefab
                // exists in there but the GetComponent<T>() check missed it
                // (component on a child rather than root), name-prefix search
                // will catch it.
                if (prefab == null)
                {
                    prefab = FindPrefabInAssetMapByName($"AnimalWild_{label}", label);
                }

                if (prefab == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] RestoreMissingPrefabs({label}): no {label} prefab found via " +
                        "Strategy 1-4. Dumping every AnimalWild_* prefab name to confirm " +
                        $"whether '{label}' is in the asset map at all:");
                    DumpAllAnimalWildPrefabs();
                    DumpAssetMapAnimalCandidates(label);
                    return;
                }

                // Write the prefab into every entry's prefab field
                var t = group.GetType();
                var entriesField = t.GetField("animalEntries", AllInstance);
                var list = entriesField?.GetValue(group) as System.Collections.IList;
                if (list == null || list.Count == 0)
                {
                    MelonLogger.Warning(
                        $"[WotW] RestoreMissingPrefabs({label}): animalEntries empty/null.");
                    return;
                }

                int patched = 0;
                foreach (var entry in list)
                {
                    if (entry == null) continue;
                    var et = entry.GetType();
                    var prefabField = et.GetField("prefab", AllInstance)
                                   ?? et.GetField("_prefab", AllInstance);
                    if (prefabField == null) continue;
                    var current = prefabField.GetValue(entry) as GameObject;
                    if (current != null) continue; // already populated, leave alone
                    prefabField.SetValue(entry, prefab);
                    patched++;
                }

                MelonLogger.Msg(
                    $"[WotW] RestoreMissingPrefabs({label}): patched {patched} entry(ies) " +
                    $"with prefab '{prefab.name}'.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] RestoreMissingPrefabs({label}): {ex.Message}");
            }
        }

        /// <summary>
        /// Iterates GlobalAssets.prefabAssetMap.prefabAssetListRO looking for
        /// a prefab whose root GameObject carries a T component. The map is
        /// loaded via `Resources.Load&lt;PrefabAssetMap&gt;("ScriptableObjects/AssetMaps/PrefabAssetMap")`
        /// at boot, and Unity's serialization pulls in the referenced prefabs.
        /// </summary>
        private static GameObject FindPrefabInAssetMap<T>(string label) where T : Component
        {
            try
            {
                var map = GlobalAssets.prefabAssetMap;
                if (map == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] FindPrefabInAssetMap({label}): GlobalAssets.prefabAssetMap is null");
                    return null;
                }
                var list = map.prefabAssetListRO;
                if (list == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] FindPrefabInAssetMap({label}): prefabAssetListRO is null");
                    return null;
                }

                foreach (var entry in list)
                {
                    if (entry == null) continue;
                    var prefabField = entry.GetType().GetField("prefab", AllInstance);
                    var prefab = prefabField?.GetValue(entry) as GameObject;
                    if (prefab == null) continue;
                    if (prefab.GetComponent<T>() != null)
                    {
                        MelonLogger.Msg(
                            $"[WotW] FindPrefabInAssetMap({label}): found '{prefab.name}' in PrefabAssetMap.");
                        return prefab;
                    }
                }
                MelonLogger.Msg(
                    $"[WotW] FindPrefabInAssetMap({label}): no {label} component on any of " +
                    $"{list.Count} entries in PrefabAssetMap.");
                return null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] FindPrefabInAssetMap({label}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Last-resort diagnostic when no prefab is found anywhere. Dumps the
        /// names of every prefab in PrefabAssetMap whose name even resembles
        /// "Fox"/"Groundhog" so we can see what IS in there.
        /// </summary>
        private static void DumpAssetMapAnimalCandidates(string label)
        {
            try
            {
                var map = GlobalAssets.prefabAssetMap;
                if (map?.prefabAssetListRO == null) return;
                int total = map.prefabAssetListRO.Count;
                int matched = 0;
                foreach (var entry in map.prefabAssetListRO)
                {
                    if (entry == null) continue;
                    var prefabField = entry.GetType().GetField("prefab", AllInstance);
                    var prefab = prefabField?.GetValue(entry) as GameObject;
                    if (prefab == null) continue;
                    string n = prefab.name ?? "";
                    if (n.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        MelonLogger.Msg(
                            $"[WotW] AssetMap candidate (name match '{label}'): '{n}'");
                        matched++;
                    }
                }
                MelonLogger.Msg(
                    $"[WotW] AssetMap dump for '{label}': {matched} name-matches in {total} prefabs.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpAssetMapAnimalCandidates: {ex.Message}");
            }
        }

        /// <summary>
        /// Strategy 3: searches all loaded GameObjects in memory by name prefix.
        /// Prefers asset prefabs (no scene) over scene instances. Catches the
        /// case where Fox/Groundhog prefabs are loaded but their MonoBehaviour
        /// is on a child, not the root.
        /// </summary>
        private static GameObject FindPrefabByNamePrefix(string prefix, string label)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                if (all == null || all.Length == 0) return null;

                GameObject sceneMatch = null;
                foreach (var go in all)
                {
                    if (go == null) continue;
                    if (go.name == null) continue;
                    if (!go.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    // Prefer asset prefabs (no valid scene = asset, not instance)
                    if (!go.scene.IsValid() || string.IsNullOrEmpty(go.scene.name))
                    {
                        MelonLogger.Msg(
                            $"[WotW] FindPrefabByNamePrefix({label}): asset '{go.name}' " +
                            "(no scene — prefab root)");
                        return go;
                    }
                    if (sceneMatch == null) sceneMatch = go;
                }

                if (sceneMatch != null)
                {
                    MelonLogger.Msg(
                        $"[WotW] FindPrefabByNamePrefix({label}): scene instance '{sceneMatch.name}' " +
                        $"(scene='{sceneMatch.scene.name}') — using as fallback");
                    return sceneMatch;
                }

                MelonLogger.Msg(
                    $"[WotW] FindPrefabByNamePrefix({label}): no GameObject with name " +
                    $"starting '{prefix}' in {all.Length} loaded objects.");
                return null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] FindPrefabByNamePrefix({label}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Strategy 4: PrefabAssetMap iteration by name prefix instead of
        /// component type. Same map as Strategy 2 but the match criterion is
        /// the prefab's name starting with "AnimalWild_Fox" (etc).
        /// </summary>
        private static GameObject FindPrefabInAssetMapByName(string prefix, string label)
        {
            try
            {
                var map = GlobalAssets.prefabAssetMap;
                if (map?.prefabAssetListRO == null) return null;

                foreach (var entry in map.prefabAssetListRO)
                {
                    if (entry == null) continue;
                    var prefabField = entry.GetType().GetField("prefab", AllInstance);
                    var prefab = prefabField?.GetValue(entry) as GameObject;
                    if (prefab?.name == null) continue;
                    if (prefab.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        MelonLogger.Msg(
                            $"[WotW] FindPrefabInAssetMapByName({label}): found '{prefab.name}' " +
                            "in PrefabAssetMap.");
                        return prefab;
                    }
                }
                MelonLogger.Msg(
                    $"[WotW] FindPrefabInAssetMapByName({label}): no prefab named '{prefix}*' " +
                    $"in {map.prefabAssetListRO.Count} entries.");
                return null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] FindPrefabInAssetMapByName({label}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Diagnostic when all four strategies fail. Dumps every AnimalWild_*
        /// prefab in PrefabAssetMap so we can see what wildlife IS shipped in
        /// the asset map. If Fox/Groundhog are absent, they were truly removed
        /// from the build (probably moved to an unloaded Addressable bundle).
        /// </summary>
        private static void DumpAllAnimalWildPrefabs()
        {
            try
            {
                var map = GlobalAssets.prefabAssetMap;
                if (map?.prefabAssetListRO == null) return;
                int matched = 0;
                MelonLogger.Msg("[WotW] All AnimalWild_* prefabs in PrefabAssetMap:");
                foreach (var entry in map.prefabAssetListRO)
                {
                    if (entry == null) continue;
                    var prefabField = entry.GetType().GetField("prefab", AllInstance);
                    var prefab = prefabField?.GetValue(entry) as GameObject;
                    if (prefab?.name == null) continue;
                    if (prefab.name.StartsWith("AnimalWild_", StringComparison.OrdinalIgnoreCase))
                    {
                        MelonLogger.Msg($"[WotW]   {prefab.name}");
                        matched++;
                    }
                }
                MelonLogger.Msg($"[WotW] AnimalWild_* total: {matched} prefab(s).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpAllAnimalWildPrefabs: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps the animal prefab list for a group. SpawnAnimal returns null
        /// when GetWeightedAnimalPrefab can't find a valid entry — this lets
        /// us see whether the list is empty, has null prefabs, or has data
        /// that should work.
        /// </summary>
        private static void DumpGroupPrefabs(object group, string label)
        {
            if (group == null)
            {
                MelonLogger.Msg($"[WotW] DumpGroupPrefabs({label}): group is null");
                return;
            }
            try
            {
                var t = group.GetType();
                var entriesField = t.GetField("animalEntries", AllInstance);
                var list = entriesField?.GetValue(group) as System.Collections.IList;
                if (list == null)
                {
                    MelonLogger.Msg($"[WotW] DumpGroupPrefabs({label}): animalEntries is null");
                    return;
                }
                MelonLogger.Msg($"[WotW] DumpGroupPrefabs({label}): animalEntries count = {list.Count}");
                int idx = 0;
                foreach (var entry in list)
                {
                    if (entry == null)
                    {
                        MelonLogger.Msg($"[WotW]   [{idx}] entry is null");
                        idx++; continue;
                    }
                    var et = entry.GetType();
                    MelonLogger.Msg($"[WotW]   [{idx}] type={et.FullName}");
                    // Dump EVERY field on the entry — the hidden AssetReference
                    // (if Crate moved fox/groundhog to Addressables) will surface
                    // here as something like 'prefabReference' / 'AssetGUID'.
                    foreach (var f in et.GetFields(AllInstance))
                    {
                        try
                        {
                            object v = f.GetValue(entry);
                            string vs;
                            if (v == null) vs = "(null)";
                            else if (v is GameObject go) vs = $"GameObject:'{go.name}'";
                            else if (v is UnityEngine.Object uo) vs = $"{uo.GetType().Name}:'{uo.name}'";
                            else vs = v.ToString();
                            // Truncate long strings to keep log lines readable
                            if (vs.Length > 200) vs = vs.Substring(0, 200) + "...";
                            MelonLogger.Msg($"[WotW]     {f.Name} ({f.FieldType.Name}) = {vs}");
                        }
                        catch { /* one field shouldn't kill the whole dump */ }
                    }
                    idx++;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpGroupPrefabs({label}): {ex.Message}");
            }
        }

        private static Vector3 PickFoxSpawnPosition(ResourceManager rm)
        {
            try
            {
                // Find any building tagged ChickenCoop and spawn ~50u away.
                // Iterate allBuildingsRO since chickensRO doesn't directly
                // expose coop transforms.
                Vector3 coopPos = Vector3.zero;
                int count = 0;
                foreach (var b in rm.allBuildingsRO)
                {
                    if (b == null) continue;
                    string tag = b.gameObject.tag ?? "";
                    if (tag.IndexOf("Chicken", StringComparison.OrdinalIgnoreCase) < 0
                        && tag.IndexOf("Coop", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    coopPos += b.transform.position;
                    count++;
                }
                if (count == 0) return Vector3.zero;
                coopPos /= count;

                // Offset 50u in a random direction
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                return coopPos + new Vector3(Mathf.Cos(angle) * 50f, 0, Mathf.Sin(angle) * 50f);
            }
            catch { return Vector3.zero; }
        }

        private static Vector3 PickGroundhogSpawnPosition(ResourceManager rm)
        {
            try
            {
                // Spawn near average position of crop fields (~30u offset)
                Vector3 avg = Vector3.zero;
                int count = 0;
                foreach (var f in rm.cropFieldsRO)
                {
                    if (f == null) continue;
                    avg += f.transform.position;
                    count++;
                }
                if (count == 0) return Vector3.zero;
                avg /= count;
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                return avg + new Vector3(Mathf.Cos(angle) * 30f, 0, Mathf.Sin(angle) * 30f);
            }
            catch { return Vector3.zero; }
        }

        /// <summary>
        /// Lowers Fox + Groundhog spawnIntervalInDays on their AnimalGroupDefinition
        /// (vanilla 220/240 → config default ~60) and pre-seeds the per-group
        /// interval counter so the next UpdateAnimalGroups tick can fire a spawn
        /// rather than starting from 0. Without this, the counter takes ~8 in-game
        /// months to accumulate after every save load — most players never see one.
        /// </summary>
        private static void OverrideSpawnIntervalsAndSeedCounters(object am, Type amType)
        {
            try
            {
                int foxInterval = WardenOfTheWildsMod.FoxSpawnIntervalDays.Value;
                int grhInterval = WardenOfTheWildsMod.GroundhogSpawnIntervalDays.Value;

                // 1) Find the Fox + Groundhog AnimalGroupDefinitions in validAnimalGroups
                //    and override their spawnIntervalInDays field.
                var validField = amType.GetField("validAnimalGroups", AllInstance);
                var groups = validField?.GetValue(am) as System.Collections.IEnumerable;
                if (groups == null) return;

                object foxGroup = null, grhGroup = null;
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    var atProp = g.GetType().GetProperty("animalType", AllInstance);
                    var at = atProp?.GetValue(g, null)?.ToString();
                    if (at == "Fox") foxGroup = g;
                    else if (at == "Groundhog") grhGroup = g;
                }

                OverrideGroupInterval(foxGroup, foxInterval, "Fox");
                OverrideGroupInterval(grhGroup, grhInterval, "Groundhog");

                // Lower minSpawnDistance from vanilla 500u → config (default 100).
                // Vanilla's 500u means the spawn anchor (nearest building) and
                // edge point need 500+ world units of pathable space between
                // them; on typical FF maps this is rarely satisfied so spawns
                // silently return 0. Confirmed via SpawnAnimals trace:
                // "actuallySpawned=0" every time despite the gate passing.
                int foxMin = WardenOfTheWildsMod.FoxMinSpawnDistance.Value;
                int grhMin = WardenOfTheWildsMod.GroundhogMinSpawnDistance.Value;
                OverrideGroupMinSpawnDistance(foxGroup, foxMin, "Fox");
                OverrideGroupMinSpawnDistance(grhGroup, grhMin, "Groundhog");

                // 2) Seed spawnIntervalsByAnimalGroupDict[group] to (interval - 1)
                //    so the very next daily tick fires the spawn check.
                var dictField = amType.GetField("spawnIntervalsByAnimalGroupDict", AllInstance);
                var dict = dictField?.GetValue(am) as System.Collections.IDictionary;
                if (dict == null) return;

                if (foxGroup != null && dict.Contains(foxGroup))
                {
                    int target = Math.Max(0, foxInterval - 1);
                    dict[foxGroup] = target;
                    MelonLogger.Msg(
                        $"[WotW] Seeded Fox interval-counter to {target}d " +
                        $"(was {foxInterval - 1}d short of fresh interval).");
                }
                if (grhGroup != null && dict.Contains(grhGroup))
                {
                    int target = Math.Max(0, grhInterval - 1);
                    dict[grhGroup] = target;
                    MelonLogger.Msg(
                        $"[WotW] Seeded Groundhog interval-counter to {target}d.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OverrideSpawnIntervalsAndSeedCounters: {ex.Message}");
            }
        }

        private static void OverrideGroupMinSpawnDistance(object group, int newDistance, string label)
        {
            if (group == null) return;
            try
            {
                var t = group.GetType();
                FieldInfo field = t.GetField("_minSpawnDistance", AllInstance)
                               ?? t.GetField("minSpawnDistance", AllInstance);
                if (field == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] OverrideGroupMinSpawnDistance({label}): " +
                        "_minSpawnDistance backing field not found.");
                    return;
                }
                float prev = (float)field.GetValue(group);
                if (Math.Abs(prev - newDistance) < 0.5f) return;
                field.SetValue(group, (float)newDistance);
                MelonLogger.Msg(
                    $"[WotW] {label} minSpawnDistance {prev:F0}u → {newDistance}u.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] OverrideGroupMinSpawnDistance({label}): {ex.Message}");
            }
        }

        private static void OverrideGroupInterval(object group, int newInterval, string label)
        {
            if (group == null) return;
            try
            {
                // spawnIntervalInDays is a read-only property (`=>` getter) on
                // AnimalGroupDefinition — backed by private int _spawnIntervalInDays.
                // Write to the backing field directly. Fall back to the property
                // name in case Crate ever inlines the field-name.
                var t = group.GetType();
                FieldInfo field = t.GetField("_spawnIntervalInDays", AllInstance)
                               ?? t.GetField("spawnIntervalInDays", AllInstance);
                if (field == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] OverrideGroupInterval({label}): " +
                        "_spawnIntervalInDays backing field not found.");
                    return;
                }
                int prev = (int)field.GetValue(group);
                if (prev == newInterval) return;
                field.SetValue(group, newInterval);
                MelonLogger.Msg(
                    $"[WotW] {label} spawnIntervalInDays {prev}d → {newInterval}d.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OverrideGroupInterval({label}): {ex.Message}");
            }
        }

        /// <summary>
        /// Counts live Fox + Groundhog instances currently in the scene. If
        /// non-zero they spawned successfully but the player just hasn't seen
        /// them yet (probably out at the map edge).
        /// </summary>
        private static void ScanLiveAnimalCounts()
        {
            try
            {
                int foxCount = UnityEngine.Object.FindObjectsOfType<Fox>()?.Length ?? 0;
                int grhCount = UnityEngine.Object.FindObjectsOfType<Groundhog>()?.Length ?? 0;
                MelonLogger.Msg(
                    $"[WotW] Spawn-diag: live foxes on map = {foxCount}, " +
                    $"live groundhogs on map = {grhCount}");
                if (foxCount == 0)
                    MelonLogger.Msg("[WotW] Spawn-diag: no foxes spawned yet.");
                if (grhCount == 0)
                    MelonLogger.Msg("[WotW] Spawn-diag: no groundhogs spawned yet.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ScanLiveAnimalCounts: {ex.Message}");
            }
        }

        /// <summary>
        /// One-shot diagnostic at unlock time. Logs the runtime values of every
        /// known fox/groundhog spawn gate so we can see WHICH gate is keeping
        /// them from spawning even after the delay timer is bypassed.
        /// </summary>
        private static void DumpSpawnDiagnostic(object am, Type amType)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var rm = gm?.resourceManager;

                int chickenCount = 0, fieldCount = 0;
                try { chickenCount = rm?.chickensRO?.Count ?? 0; } catch { }
                try { fieldCount   = rm?.cropFieldsRO?.Count ?? 0; } catch { }

                MelonLogger.Msg(
                    $"[WotW] Spawn-diag: chickens={chickenCount}, fields={fieldCount}");

                // Per-map theme frequency scales (if 0 → species disabled on this map)
                try
                {
                    var theme = SettingsManager.mapTheme;
                    if (theme != null)
                    {
                        var foxFreq = theme.GetType().GetField("foxSpawnFrequencyScale", AllInstance);
                        var grhFreq = theme.GetType().GetField("groundhogSpawnFrequencyScale", AllInstance);
                        float foxScale = foxFreq != null ? Convert.ToSingle(foxFreq.GetValue(theme)) : -1f;
                        float grhScale = grhFreq != null ? Convert.ToSingle(grhFreq.GetValue(theme)) : -1f;
                        MelonLogger.Msg(
                            $"[WotW] Spawn-diag: mapTheme foxScale={foxScale:F2}, " +
                            $"groundhogScale={grhScale:F2} (0=disabled)");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Msg($"[WotW] Spawn-diag: mapTheme read failed: {ex.Message}");
                }

                // Curves: max-groups-by-X. If these evaluate to 0 the spawn
                // loop iterates 0 times and no animals spawn regardless.
                EvaluateAnimationCurve(am, amType, "maxFoxGroupsByChickenCount", chickenCount, "fox");
                EvaluateAnimationCurve(am, amType, "maxGroundhogGroupsByFieldCount", fieldCount, "groundhog");

                // validAnimalGroups membership — if Fox/Groundhog isn't here
                // the spawn loop doesn't see them at all.
                DumpValidAnimalGroups(am, amType);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpSpawnDiagnostic: {ex.Message}");
            }
        }

        private static void EvaluateAnimationCurve(
            object am, Type amType, string curveFieldName, int evaluateAt, string label)
        {
            try
            {
                var field = amType.GetField(curveFieldName, AllInstance);
                if (field == null)
                {
                    MelonLogger.Msg($"[WotW] Spawn-diag: {curveFieldName} not found");
                    return;
                }
                var curve = field.GetValue(am) as AnimationCurve;
                if (curve == null)
                {
                    MelonLogger.Msg($"[WotW] Spawn-diag: {curveFieldName} is null");
                    return;
                }
                float result = curve.Evaluate(evaluateAt);
                int rounded  = Mathf.RoundToInt(result);
                int keyCount = curve.keys?.Length ?? 0;
                MelonLogger.Msg(
                    $"[WotW] Spawn-diag: {curveFieldName}.Evaluate({evaluateAt}) = " +
                    $"{result:F2} (rounds to {rounded} {label}-groups). Curve has {keyCount} keys.");
                if (rounded == 0)
                {
                    MelonLogger.Warning(
                        $"[WotW] Spawn-diag: {label} curve evaluates to 0 — " +
                        "spawn loop will not iterate. Need more " +
                        (label == "fox" ? "chickens" : "fields") + " or curve override.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] EvaluateAnimationCurve({curveFieldName}): {ex.Message}");
            }
        }

        private static void DumpValidAnimalGroups(object am, Type amType)
        {
            try
            {
                var field = amType.GetField("validAnimalGroups", AllInstance);
                if (field == null)
                {
                    MelonLogger.Msg("[WotW] Spawn-diag: validAnimalGroups not found");
                    return;
                }
                var list = field.GetValue(am) as System.Collections.IEnumerable;
                if (list == null)
                {
                    MelonLogger.Msg("[WotW] Spawn-diag: validAnimalGroups is null");
                    return;
                }
                int total = 0; bool sawFox = false, sawGroundhog = false;
                foreach (var g in list)
                {
                    if (g == null) continue;
                    total++;
                    var animalTypeProp = g.GetType().GetProperty("animalType", AllInstance);
                    var v = animalTypeProp?.GetValue(g, null)?.ToString() ?? "?";
                    if (v == "Fox") { sawFox = true; DumpAnimalGroupDetail(g, "Fox"); }
                    if (v == "Groundhog") { sawGroundhog = true; DumpAnimalGroupDetail(g, "Groundhog"); }
                }
                MelonLogger.Msg(
                    $"[WotW] Spawn-diag: validAnimalGroups count={total}, " +
                    $"foxPresent={sawFox}, groundhogPresent={sawGroundhog}");
                if (!sawFox)
                    MelonLogger.Warning("[WotW] Spawn-diag: NO Fox group in validAnimalGroups — won't spawn ever.");
                if (!sawGroundhog)
                    MelonLogger.Warning("[WotW] Spawn-diag: NO Groundhog group in validAnimalGroups — won't spawn ever.");

                // Per-group counter state (how close are we to triggering a spawn?)
                DumpSpawnIntervalDict(am, amType);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpValidAnimalGroups: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps the per-group fields that gate spawning: spawnPointType,
        /// spawnIntervalInDays, spawnInSpring, cropBasedSpawning,
        /// buildingTagSpawningReq, maxAnimalCount.
        /// </summary>
        private static void DumpAnimalGroupDetail(object group, string label)
        {
            try
            {
                var t = group.GetType();
                string spt   = ReadFieldOrProp(group, t, "spawnPointType")    ?? "?";
                string sid   = ReadFieldOrProp(group, t, "spawnIntervalInDays") ?? "?";
                string sis   = ReadFieldOrProp(group, t, "spawnInSpring")     ?? "?";
                string cbs   = ReadFieldOrProp(group, t, "cropBasedSpawning") ?? "?";
                string btsr  = ReadFieldOrProp(group, t, "buildingTagSpawningReq") ?? "?";
                string mac   = ReadFieldOrProp(group, t, "maxAnimalCount")    ?? "?";
                MelonLogger.Msg(
                    $"[WotW] Spawn-diag: {label} group — spawnPointType={spt}, " +
                    $"spawnIntervalInDays={sid}, spawnInSpring={sis}, " +
                    $"cropBasedSpawning={cbs}, buildingTag='{btsr}', maxAnimalCount={mac}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpAnimalGroupDetail({label}): {ex.Message}");
            }
        }

        /// <summary>
        /// Reads spawnIntervalsByAnimalGroupDict and logs Fox + Groundhog
        /// counters so we can see how close they are to firing.
        /// </summary>
        private static void DumpSpawnIntervalDict(object am, Type amType)
        {
            try
            {
                var field = amType.GetField("spawnIntervalsByAnimalGroupDict", AllInstance);
                if (field == null) return;
                var dict = field.GetValue(am) as System.Collections.IDictionary;
                if (dict == null) return;

                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (entry.Key == null) continue;
                    var animalTypeProp = entry.Key.GetType().GetProperty("animalType", AllInstance);
                    var v = animalTypeProp?.GetValue(entry.Key, null)?.ToString() ?? "?";
                    if (v == "Fox" || v == "Groundhog")
                        MelonLogger.Msg(
                            $"[WotW] Spawn-diag: {v} interval-counter = {entry.Value} day(s)");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DumpSpawnIntervalDict: {ex.Message}");
            }
        }

        private static string ReadFieldOrProp(object obj, Type t, string name)
        {
            try
            {
                var f = t.GetField(name, AllInstance);
                if (f != null) return f.GetValue(obj)?.ToString() ?? "(null)";
                var p = t.GetProperty(name, AllInstance);
                if (p != null) return p.GetValue(obj, null)?.ToString() ?? "(null)";
            }
            catch { }
            return null;
        }

        private static void OverrideIntField(
            Type type, object instance, string fieldName, int newValue, string label)
        {
            try
            {
                var field = type.GetField(fieldName, AllInstance);
                if (field == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] SmallGameUnlock: field '{fieldName}' not found — " +
                        "vanilla field name may have changed.");
                    return;
                }

                int previous = (int)field.GetValue(instance);
                if (previous == newValue)
                {
                    MelonLogger.Msg(
                        $"[WotW] {label} spawn delay already {newValue}d (no change).");
                    return;
                }

                field.SetValue(instance, newValue);
                MelonLogger.Msg(
                    $"[WotW] Unlocked {label}: spawn delay {previous}d → {newValue}d.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] OverrideIntField({fieldName}): {ex.Message}");
            }
        }
    }
}
