using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

// ---------------------------------------------------------------------------
//  FishingShackEnhancement
//  MonoBehaviour attached to every FishingShack at scene load.
//
//  Binary mode selector (UI slider — no hotkey, no overlay text):
//    Angler  — 2 rod fishers (faster, bigger catches). Pre-tech default.
//    Creeler — 2 willow-creel trap workers. Expanded radius, passive income,
//              works year-round (creels under ice). Post-tech only.
//
//  "Creeler" rather than "Creeler" — FF has no crabs, but willow creels are
//  already a thematic catch (willow material exists, traps work under ice).
//
//  All enhanced shacks get 2 worker slots (up from vanilla 1).
//  Creeler mode also unlocks the hidden fishSchoolBonusInWinter flag on
//  FishingManager so fish-school productivity bonuses apply year-round.
//
//  Water tile counting (ported from HunterCabinEnhancement) provides a bonus
//  to Creeler spawn intervals on water-heavy maps — the "big deep lake" payoff.
// ---------------------------------------------------------------------------

namespace WardenOfTheWilds.Components
{
    public enum FishingShackMode
    {
        Angler  = 0,  // 2 rod fishers
        Creeler = 1,  // 2 willow-creel trap workers
    }

    public class FishingShackEnhancement : MonoBehaviour
    {
        // -- Shared reflection flags ----------------------------------------
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // -- Persistence: position-keyed (survives save/load) ---------------
        private static readonly Dictionary<int, FishingShackMode> SavedModes =
            new Dictionary<int, FishingShackMode>();

        private int GetBuildingKey()
        {
            var pos = transform.position;
            return Mathf.RoundToInt(pos.x * 1000f + pos.z);
        }

        /// <summary>
        /// Called from FishingShackLoadPatches when a save is loaded: stores
        /// the saved mode in SavedModes keyed by position, so the component
        /// that attaches to the shack shortly after (via LateInit) can pick
        /// up the right mode in InitializeDelayed.
        /// </summary>
        internal static void SetSavedModeForPosition(Vector3 pos, FishingShackMode mode)
        {
            int key = Mathf.RoundToInt(pos.x * 1000f + pos.z);
            SavedModes[key] = mode;
        }

        // -- Static tracking ------------------------------------------------
        private static bool _winterBonusApplied = false;

        /// <summary>Called from Plugin.OnSceneWasLoaded("Map").</summary>
        public static void OnMapLoaded()
        {
            SavedModes.Clear();
            _winterBonusApplied = false;
            _cachedTrapPrefab = null;
            _trapPrefabLookupDone = false;
        }

        // -- State ----------------------------------------------------------
        private FishingShackMode _mode = FishingShackMode.Angler;
        private bool  _initialized = false;
        private int   _cachedWaterTiles = 0;
        private bool  _waterBonusActive = false;
        // (No custom SelectionCircle — see UpdateCreelerVisualCircle note below)

        // Creeler daily tick
        private int _lastDayChecked = -1;
        private int _daysSinceLastCrabSpawn = 0;

        // Cached storage reference for Creeler production
        private object _fishStorage = null;
        private MethodInfo _addItemsMethod = null;
        private MethodInfo _getItemCountMethod = null;
        private object _itemFishRef = null;
        private bool _storageLookupDone = false;

        /// <summary>
        /// Reads the current fish count from <see cref="_fishStorage"/> via
        /// reflection. Used to compute (a) available room before depositing
        /// (cap clamp) and (b) the actual delta added (vanilla AddItems
        /// returns the new total, not the delta — we compute it from
        /// before/after counts). Returns 0 on any reflection failure.
        /// </summary>
        private uint GetFishCount()
        {
            try
            {
                if (_fishStorage == null || _itemFishRef == null) return 0u;
                if (_getItemCountMethod == null)
                {
                    _getItemCountMethod = _fishStorage.GetType().GetMethod(
                        "GetItemCount", new[] { _itemFishRef.GetType() });
                }
                if (_getItemCountMethod == null) return 0u;
                object result = _getItemCountMethod.Invoke(_fishStorage, new[] { _itemFishRef });
                return result is uint u ? u : 0u;
            }
            catch { return 0u; }
        }

        // Selection tracking
        private bool _lastSelected = false;
        // Cached once — avoids per-frame GetComponent across many shacks
        private SelectableComponent _cachedSelectable;

        // Creeler trap deployment — decorative traps placed at water points,
        // using the vanilla AnimalTrapResource prefab (for the pin-frame visual)
        // with the widget icon overridden to show a basket. Fish production is
        // abstracted via ProduceCrabTrapFish; these traps are decorative only.
        private readonly List<GameObject> _deployedTraps = new List<GameObject>();

        // Cached prefab reference — resolved once per session from any HunterBuilding.
        private static AnimalTrapResource _cachedTrapPrefab = null;
        private static bool _trapPrefabLookupDone = false;

        // v1.0.12 — Per-instance guard against scheduling multiple retry
        // coroutines if SyncCreelerTraps is called repeatedly while the
        // prefab is still unresolved (e.g. mode-cycle spam during the first
        // few seconds of a save load).
        private bool _creelerRetryScheduled = false;

        // -- Public accessors -----------------------------------------------
        public FishingShackMode Mode => _mode;

        public int CreelerSlots => _mode == FishingShackMode.Creeler ? 2 : 0;
        public int AnglerSlots  => _mode == FishingShackMode.Angler  ? 2 : 0;

        public bool WaterBonusActive => _waterBonusActive;
        public int  CachedWaterTiles => _cachedWaterTiles;

        /// <summary>
        /// Rod-fishing output multiplier for Angler mode. In Creeler mode,
        /// rod output is zero (workers operate traps instead, not rods);
        /// creels produce fish via daily ticks from ProduceCreelerFish().
        /// </summary>
        public float GetAnglerOutputMult()
        {
            return _mode == FishingShackMode.Angler
                ? WardenOfTheWildsMod.AnglerCatchMult.Value
                : 0f;
        }

        // -- Unity lifecycle ------------------------------------------------
        private void Start()
        {
            StartCoroutine(InitializeDelayed());
        }

        private IEnumerator InitializeDelayed()
        {
            yield return null;
            if (_initialized) yield break;
            _initialized = true;

            if (SavedModes.TryGetValue(GetBuildingKey(), out FishingShackMode saved))
                _mode = saved;

            ApplyMode();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] FishingShackEnhancement on '{gameObject.name}' " +
                $"(mode={_mode}, anglerSlots={AnglerSlots}, creelerSlots={CreelerSlots}, " +
                $"waterTiles={_cachedWaterTiles}, waterBonus={_waterBonusActive})");
        }

        // -- Mode management ------------------------------------------------
        /// <summary>
        /// Public API called by the slider UI. Respects the tech gate —
        /// attempting to set Creeler mode pre-tech is a no-op with notification.
        /// </summary>
        public void SetMode(FishingShackMode newMode)
        {
            if (_mode == newMode) return;

            // Tech gate: Creeler is locked until Sustainable Fishing is researched
            if (newMode == FishingShackMode.Creeler)
            {
                WardenOfTheWildsMod.RefreshFishingTechState();
                if (!WardenOfTheWildsMod.SustainableFishingResearched)
                {
                    PostTechLockedNotification();
                    return;
                }
            }

            _mode = newMode;
            SavedModes[GetBuildingKey()] = newMode;
            _daysSinceLastCrabSpawn = 0;
            _storageLookupDone = false; // Re-discover storage on mode change
            ApplyMode();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] '{gameObject.name}' fishing mode -> {_mode} " +
                $"(Angler={AnglerSlots}, Creeler={CreelerSlots})");

            PostModeChangeNotification();

            // Notify subscribers (UI slider) — fires once per actual mode change
            try { OnModeChanged?.Invoke(this); } catch { }
        }

        /// <summary>
        /// Fires when any shack's mode changes via SetMode. UI code subscribes
        /// to this to refresh its display without polling.
        /// </summary>
        public static event System.Action<FishingShackEnhancement> OnModeChanged;

        /// <summary>Tells the player the Creeler slider position is locked.</summary>
        private void PostTechLockedNotification()
        {
            try
            {
                var window = UnityEngine.Object.FindObjectOfType<UIEventLogWindow>();
                if (window == null) return;
                var addMethod = typeof(UIEventLogWindow).GetMethod(
                    "AddEventToLog",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                addMethod?.Invoke(window, new object[]
                {
                    "Research Sustainable Fishing to unlock Creeler mode.",
                    null
                });
            }
            catch { }
        }

        private void PostModeChangeNotification()
        {
            try
            {
                string summary = _mode == FishingShackMode.Angler
                    ? "Fishing shack set to Angler mode. " +
                      "Two rod fishers with enhanced catches."
                    : "Fishing shack set to Creeler mode. " +
                      "Willow creels work even under ice. Year-round income.";

                var window = UnityEngine.Object.FindObjectOfType<UIEventLogWindow>();
                if (window == null) return;

                var addMethod = typeof(UIEventLogWindow).GetMethod(
                    "AddEventToLog",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                addMethod?.Invoke(window, new object[] { summary, null });
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] PostModeChangeNotification: {ex.Message}");
            }
        }

        // -- Mode application -----------------------------------------------
        //
        // DESIGN: Radius is driven by TECH STATE only, not mode.
        //   Pre-tech  (Sustainable Fishing not researched) → 30u vanilla
        //   Post-tech (researched)                         → 60u (both modes)
        //
        // Mode (Angler / Creeler) only affects rod-fishing output vs. trap
        // production — NOT the work-area size. This way ApplyMode doesn't
        // change the WorkArea radius on every slider click, which was breaking
        // the game's "Move Work Area" refresh of fish-node detection.
        private void ApplyMode()
        {
            if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

            // Tech gate: force Angler when Sustainable Fishing isn't researched.
            WardenOfTheWildsMod.RefreshFishingTechState();
            if (!WardenOfTheWildsMod.SustainableFishingResearched &&
                _mode != FishingShackMode.Angler)
            {
                _mode = FishingShackMode.Angler;
            }

            // Worker slots: Angler = 2 (rod-fishing scales linearly with workers),
            // Creeler = 1 (traps auto-harvest via ProduceCreelerFish daily tick;
            // the rod-fishing throttle drops Creeler rod catch to ~1/cast, so
            // a second worker is wasted slot — let the laborer pool use it).
            SetWorkerSlots(_mode == FishingShackMode.Creeler ? 1 : 2);

            // Fishing storage capacity — wagon-efficient pickup (baseline)
            SetFishStorageCapacity(WardenOfTheWildsMod.FishingShackStorageCap.Value);

            // Radius expansion: apply ONCE per session. Repeated setter calls
            // trigger an internal reset loop (fishermen go into "looking for
            // shore"). The one-shot flag prevents future ApplyMode calls from
            // re-touching the radius even if something else resets it.
            ApplyTechRadiusOnce();

            // Water tile count for Creeler water bonus — uses current radius
            float radius = GetCurrentFishingRadius();
            _cachedWaterTiles = CountWaterTiles(transform.position, radius);
            _waterBonusActive = _cachedWaterTiles >=
                WardenOfTheWildsMod.CreelerWaterTileThreshold.Value;

            // Winter fishing bonus
            UpdateGlobalWinterBonus();

            // Creeler trap deployment — physical traps in water for Creeler mode
            SyncCreelerTraps();
        }

        // -- Creeler trap deployment (Phase 1) ------------------------------
        //
        // Mirrors the hunter trap pattern: instantiate AnimalTrapResource
        // prefabs at water points within the shack's fishing radius. The
        // traps carry a CreelerTrapMarker component so future phases can
        // differentiate them from hunter traps in patches + task routing.
        //
        // Deploy on switch TO Creeler. Teardown on switch away. Idempotent:
        // repeat calls while already in the same state are no-ops.
        private void SyncCreelerTraps()
        {
            // Mode OFF or disabled → make sure no traps are out
            if (_mode != FishingShackMode.Creeler)
            {
                TeardownCreelerTraps();
                return;
            }

            // Prune any null entries (traps that Unity destroyed on us)
            _deployedTraps.RemoveAll(t => t == null);

            int target = WardenOfTheWildsMod.CreelerTrapCount.Value;
            if (_deployedTraps.Count >= target) return;

            var prefab = GetCreelerTrapPrefab();
            if (prefab == null)
            {
                // v1.0.12 — Event-driven attach (v1.0.11) can race the prefab
                // lookup: the FishingShack's Building.Awake may fire before
                // any HunterBuilding has spawned, so FindObjectsOfType<HunterBuilding>
                // returns empty and the prefab cache stays null. Pre-v1.0.11
                // we attached via LateInit polling so HunterBuildings always
                // existed by then. Schedule a retry coroutine that polls until
                // any HunterBuilding shows up, then re-runs SyncCreelerTraps.
                // Idempotent: if user toggles mode while waiting, the retry
                // re-checks _mode at fire time and bails cleanly.
                if (!_creelerRetryScheduled)
                {
                    _creelerRetryScheduled = true;
                    StartCoroutine(RetrySyncCreelerTrapsWhenReady());
                }
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] SyncCreelerTraps '{gameObject.name}': trap prefab not resolved " +
                    $"yet — retry coroutine scheduled.");
                return;
            }

            // Find water points — oversample so we have options after collision
            int needed = target - _deployedTraps.Count;
            float radius = GetCurrentFishingRadius();
            var waterPoints = FindWaterPoints(transform.position, radius, needed * 3);
            if (waterPoints.Count == 0)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SyncCreelerTraps '{gameObject.name}': no water points found " +
                    $"within radius {radius:F0}u.");
                return;
            }

            var gm = UnitySingleton<GameManager>.Instance;
            var itemTrapObj = FindFieldOrProperty(gm?.workBucketManager, "itemAnimalTrap");

            int placed = 0;
            for (int i = 0; i < waterPoints.Count && _deployedTraps.Count < target; i++)
            {
                Vector3 pos = waterPoints[i];
                var trap = UnityEngine.Object.Instantiate(
                    prefab,
                    pos,
                    Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));

                if (trap == null) continue;

                gm?.terrainManager?.PlaceObjectOnTerrain(trap.gameObject);

                // Null-out huntersResidence so this trap doesn't register as a
                // hunter's deployed trap. It still auto-registers with
                // resourceManager.AddOrRemoveAnimalTrapResource in Start, but
                // without a hunter owner it stays inert.
                trap.huntersResidence = null;

                // Add a trap-item bundle so isBroken returns false (some code
                // paths short-circuit on broken traps).
                if (itemTrapObj is Item itemTrap)
                {
                    try { trap.storage.AddItems(new ItemBundle(itemTrap, 1u, 100u)); }
                    catch { }
                }

                // Override the pin-frame icon from "animal trap" to "basket".
                // AnimalTrapResource.Start assigns the icon on the widget; we
                // need to wait one frame for Start to run, then overwrite.
                StartCoroutine(OverrideTrapIconToBasket(trap));

                // Mark it as ours so teardown can find and destroy these later.
                var marker = trap.gameObject.AddComponent<CreelerTrapMarker>();
                marker.OwnerShack = this;

                _deployedTraps.Add(trap.gameObject);
                placed++;
            }

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] SyncCreelerTraps '{gameObject.name}': deployed {placed} trap(s), " +
                $"total {_deployedTraps.Count}/{target}.");
        }

        /// <summary>
        /// v1.0.12 retry coroutine. Polls every 2 seconds until a
        /// HunterBuilding exists in the scene (so GetCreelerTrapPrefab can
        /// succeed), then re-runs SyncCreelerTraps. Exits if mode changes
        /// away from Creeler before the prefab resolves. Hard cap of 30
        /// attempts (~60s) so a save with literally zero hunter cabins
        /// doesn't spin forever.
        /// </summary>
        private IEnumerator RetrySyncCreelerTrapsWhenReady()
        {
            var wait = new WaitForSeconds(2f);
            const int MaxAttempts = 30;

            for (int i = 0; i < MaxAttempts; i++)
            {
                yield return wait;

                // Bail cleanly if mode changed or component was destroyed.
                if (this == null || gameObject == null) yield break;
                if (_mode != FishingShackMode.Creeler)
                {
                    _creelerRetryScheduled = false;
                    yield break;
                }

                if (GetCreelerTrapPrefab() != null)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] RetrySyncCreelerTrapsWhenReady '{gameObject.name}': " +
                        $"HunterBuilding found on attempt {i + 1} — deploying traps.");
                    _creelerRetryScheduled = false;
                    SyncCreelerTraps();
                    yield break;
                }
            }

            WardenOfTheWildsMod.Log.Warning(
                $"[WotW] RetrySyncCreelerTrapsWhenReady '{gameObject.name}': " +
                $"no HunterBuilding found after {MaxAttempts} attempts — " +
                $"traps will not deploy. (Build a Hunter Cabin and toggle " +
                $"the shack's mode to retry.)");
            _creelerRetryScheduled = false;
        }

        private void TeardownCreelerTraps()
        {
            int destroyed = 0;
            foreach (var trap in _deployedTraps)
            {
                if (trap == null) continue;
                UnityEngine.Object.Destroy(trap);
                destroyed++;
            }
            _deployedTraps.Clear();
            if (destroyed > 0)
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] TeardownCreelerTraps '{gameObject.name}': destroyed {destroyed} trap(s).");
        }

        private void OnDestroy()
        {
            TeardownCreelerTraps();
        }

        /// <summary>
        /// Returns the AnimalTrapResource prefab used by hunter buildings —
        /// same prefab the hunter deploys, so we get the identical pin-frame
        /// visual. Cached statically across the session.
        ///
        /// v1.0.12 — Resolution strategy upgraded to three tiers so we no
        /// longer depend on a HunterBuilding existing first (which broke
        /// after v1.0.11's event-driven attach order):
        ///
        ///   Tier 1: Resources.FindObjectsOfTypeAll&lt;AnimalTrapResource&gt;
        ///           filtered to prefab assets (scene.IsValid() == false).
        ///           Works from the moment the game loads the prefab into
        ///           memory — does NOT require any building to be in the
        ///           scene yet. This is the normal path on FF 1.1.0.
        ///
        ///   Tier 2: FindObjectsOfType&lt;AnimalTrapResource&gt; — picks up
        ///           any already-instanced trap in the world. We clone its
        ///           reference type as the prefab. Less ideal because
        ///           instances may carry per-trap state, but harmless when
        ///           we only read the prefab graph for Instantiate.
        ///
        ///   Tier 3: Original HunterBuilding.activeAnimalTrapPrefab path,
        ///           kept as a last resort in case the trap prefab uses
        ///           an unexpected hideFlags setup or Resources doesn't
        ///           surface it on some game version.
        /// </summary>
        private static AnimalTrapResource GetCreelerTrapPrefab()
        {
            if (_trapPrefabLookupDone) return _cachedTrapPrefab;
            try
            {
                // Tier 1 — Resources.FindObjectsOfTypeAll returns loaded
                // prefab assets in addition to scene instances. Prefabs
                // have no scene assigned, so scene.IsValid() == false is
                // the canonical filter.
                var all = Resources.FindObjectsOfTypeAll<AnimalTrapResource>();
                foreach (var trap in all)
                {
                    if (trap == null) continue;
                    var go = trap.gameObject;
                    if (go == null) continue;
                    if (go.scene.IsValid()) continue;     // skip scene instances
                    if (go.hideFlags == HideFlags.NotEditable
                        || go.hideFlags == HideFlags.HideAndDontSave)
                    {
                        // Unity sometimes flags internal/system objects we
                        // don't want to clone — skip them but keep looking.
                        continue;
                    }

                    _cachedTrapPrefab = trap;
                    _trapPrefabLookupDone = true;
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] GetCreelerTrapPrefab: resolved via Resources " +
                        $"(prefab='{go.name}').");
                    return _cachedTrapPrefab;
                }

                // Tier 2 — fall back to any in-scene trap instance.
                foreach (var trap in all)
                {
                    if (trap != null)
                    {
                        _cachedTrapPrefab = trap;
                        _trapPrefabLookupDone = true;
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] GetCreelerTrapPrefab: resolved via Resources " +
                            $"(in-scene instance, prefab unavailable).");
                        return _cachedTrapPrefab;
                    }
                }

                // Tier 3 — legacy path via HunterBuilding (pre-v1.0.12).
                foreach (var hb in UnityEngine.Object.FindObjectsOfType<HunterBuilding>())
                {
                    if (hb != null && hb.activeAnimalTrapPrefab != null)
                    {
                        _cachedTrapPrefab = hb.activeAnimalTrapPrefab;
                        _trapPrefabLookupDone = true;
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] GetCreelerTrapPrefab: resolved via HunterBuilding " +
                            $"(legacy path).");
                        return _cachedTrapPrefab;
                    }
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] GetCreelerTrapPrefab failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Waits one frame for AnimalTrapResource.Start to assign the default
        /// (animal trap) icon/name to the pin widget, then overwrites with the
        /// basket's icon + name. This keeps the pin-frame container but shows a
        /// basket inside.
        /// </summary>
        private IEnumerator OverrideTrapIconToBasket(AnimalTrapResource trap)
        {
            yield return null; // wait for Start() to assign default icon
            if (trap == null) yield break;

            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var basket = gm?.workBucketManager?.itemBasket;
                if (basket == null) yield break;

                var wb = trap.widgetBlackboard;
                if (wb == null) yield break;

                wb.icon = basket.icon;
                wb.hiRezImg = basket.hiRezImg;
                wb.displayName = basket.descriptionSingularLocTag;
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] OverrideTrapIconToBasket: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans a grid of points within radius of center and returns up to
        /// maxCount water-tile positions. Points are randomly shuffled so
        /// multiple shacks in the same lake don't all land their traps in
        /// the same corner.
        /// </summary>
        private static List<Vector3> FindWaterPoints(Vector3 center, float radius, int maxCount)
        {
            var result = new List<Vector3>();
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm == null || gm.terrainManager == null) return result;

                float step = 8f;
                // Collect all candidate water points
                var candidates = new List<Vector3>();
                for (float x = -radius; x <= radius; x += step)
                {
                    for (float z = -radius; z <= radius; z += step)
                    {
                        if (x * x + z * z > radius * radius) continue;

                        float wx = center.x + x;
                        float wz = center.z + z;
                        Vector2 samplePos = new Vector2(wx, wz);
                        bool isOcean;
                        if (!gm.terrainManager.GetIsWater(samplePos, step, step, step, out isOcean))
                            continue;

                        float wy = gm.terrainManager.GetHeightInWorldSpace(wx, wz);
                        candidates.Add(new Vector3(wx, wy, wz));
                    }
                }

                // Fisher–Yates shuffle so we don't always pick the same tiles
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    var tmp = candidates[i];
                    candidates[i] = candidates[j];
                    candidates[j] = tmp;
                }

                for (int i = 0; i < candidates.Count && result.Count < maxCount; i++)
                    result.Add(candidates[i]);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] FindWaterPoints: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Applies the tech-gated radius ONCE. All subsequent calls are no-ops,
        /// regardless of what ApplyMode is triggered by. This prevents the
        /// property-setter-reset loop we saw in logs (4-shack bursts every few
        /// minutes that were kicking fishermen into "looking for shore").
        /// </summary>
        private bool _techRadiusApplied = false;
        private void ApplyTechRadiusOnce()
        {
            if (_techRadiusApplied) return;
            if (!WardenOfTheWildsMod.SustainableFishingResearched) return; // pre-tech: stay at 30u

            _techRadiusApplied = true;
            SetFishingRadius(WardenOfTheWildsMod.CreelerRadiusMult.Value);
        }

        /// <summary>
        /// Re-applies tech-dependent radius to all shacks after tech unlock
        /// mid-game. Uses the one-shot flag so each shack only sets radius once.
        /// </summary>
        public static void RefreshAllShackRadii()
        {
            foreach (var enh in UnityEngine.Object.FindObjectsOfType<FishingShackEnhancement>())
                enh.ApplyTechRadiusOnce();
        }

        /// <summary>
        /// Sets the FishingShack's fishStorageCapacity via reflection. Baseline
        /// buff applied pre- and post-tech so a single wagon pickup can fill up
        /// instead of requiring multiple half-empty trips.
        /// </summary>
        private void SetFishStorageCapacity(int targetCap)
        {
            try
            {
                var shack = GetComponent<FishingShack>();
                if (shack == null) return;
                var field = shack.GetType().GetField("fishStorageCapacity", AllInstance);
                if (field != null && field.FieldType == typeof(uint))
                {
                    uint current = (uint)field.GetValue(shack);
                    uint target = (uint)Mathf.Max(1, targetCap);
                    if (current != target)
                    {
                        field.SetValue(shack, target);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' fishStorageCapacity: {current} → {target}");
                    }
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SetFishStorageCapacity: {ex.Message}");
            }
        }

        // -- Worker slots (same pattern as HunterCabinEnhancement) ----------
        private void SetWorkerSlots(int targetMax)
        {
            var building = GetComponent<Building>();
            if (building == null) return;
            try
            {
                var maxField = FindBackingField(building.GetType(), "maxWorkers");
                if (maxField != null)
                {
                    int current = (int)maxField.GetValue(building);
                    if (current != targetMax)
                    {
                        maxField.SetValue(building, targetMax);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' maxWorkers: {current} -> {targetMax}");
                    }
                }

                if (building.userDefinedMaxWorkers != targetMax)
                {
                    building.userDefinedMaxWorkers = targetMax;
                }

                // Trigger hiring path so new slot fills immediately
                int currentWorkers = building.workersRO?.Count ?? 0;
                if (currentWorkers < targetMax)
                    building.AttemptToAddMaxWorkers();
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] FishingShack.SetWorkerSlots: {ex.Message}");
            }
        }

        // -- Fishing radius -------------------------------------------------
        //
        // IMPORTANT: the load-time path (FishingShackLoadPatches) writes the
        // radius field directly during the game's load flow — BEFORE fishing
        // areas are created. That path doesn't need CreateFishingAreas.
        //
        // This runtime path, however, IS called from ApplyMode (slider click
        // post-load), where the radius actually changes mid-session. Here we
        // need the rescan.
        //
        // Idempotency guard: if the current radius already matches the target,
        // skip everything (write + visual sync + rescan). This prevents the
        // 10s-delayed InitializeDelayed's ApplyMode from re-triggering work
        // the load patch already completed.
        private void SetFishingRadius(float multiplier)
        {
            try
            {
                var shack = GetComponent<Building>();
                if (shack == null) return;

                // Baseline is ALWAYS the vanilla 30u — do NOT capture the
                // current field value because by the time this runs, the
                // load patch may have already set it to 60, which would
                // compound to 120 on the next multiplier application.
                const float VanillaBaseline = 30f;
                float newRadius = VanillaBaseline * multiplier;

                // Idempotency: skip if already at target
                float currentRadius = GetCurrentFishingRadius();
                if (Mathf.Approximately(currentRadius, newRadius))
                    return;

                // Direct field write — property setter has disruptive side effects
                var field = shack.GetType().GetField("_fishingRadius", AllInstance);
                if (field != null)
                {
                    field.SetValue(shack, newRadius);
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' _fishingRadius (field): " +
                        $"{currentRadius:F0} -> {newRadius:F0} (x{multiplier:F1})");
                }
                else
                {
                    var setter = shack.GetType().GetProperty("fishingRadius", AllInstance);
                    if (setter != null && setter.CanWrite)
                    {
                        setter.SetValue(shack, newRadius);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' _fishingRadius: " +
                            $"{currentRadius:F0} -> {newRadius:F0} (x{multiplier:F1})");
                    }
                }

                // Sync visual ring
                SyncWorkAreaVisualRadius(newRadius);

                // Runtime mid-session rescan required — radius actually changed
                InvokeFishingAreaRescan(shack);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SetFishingRadius: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds and invokes the FishingShack's area-rescan method via
        /// reflection. Tries a list of likely names; logs which one fired
        /// (and what's available if none match) so we can pin it down.
        /// </summary>
        private static bool _rescanMethodDumped = false;
        private void InvokeFishingAreaRescan(Building shack)
        {
            try
            {
                string[] candidates = {
                    "CreateFishingAreas",
                    "RefreshFishingAreas",
                    "RebuildFishingAreas",
                    "RegenerateFishingAreas",
                    "UpdateFishingAreas",
                    "ScanForFishingAreas",
                };

                var shackType = shack.GetType();
                foreach (string name in candidates)
                {
                    var m = shackType.GetMethod(name,
                        AllInstance, null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    m.Invoke(shack, null);
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' invoked rescan: {name}()");
                    return;
                }

                // Not found — dump methods once so we can find the right name
                if (!_rescanMethodDumped)
                {
                    _rescanMethodDumped = true;
                    WardenOfTheWildsMod.Log.Warning(
                        $"[WotW] No fishing-area rescan method found on {shackType.Name}. " +
                        "Available 0-arg methods containing 'Fish' or 'Area':");
                    foreach (var m in shackType.GetMethods(AllInstance))
                    {
                        if (m.GetParameters().Length > 0) continue;
                        string n = m.Name;
                        if (n.IndexOf("Fish", StringComparison.OrdinalIgnoreCase) < 0 &&
                            n.IndexOf("Area", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW]   candidate: {m.ReturnType.Name} {n}()");
                    }
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] InvokeFishingAreaRescan: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the native WorkArea's SelectionCircle visual radius. Does NOT
        /// call SelectionCircle.Init() (which resets WorkArea state and breaks
        /// fish-node refresh). Instead: find the radius-type field on the
        /// SelectionCircle whose value matches the previous radius, overwrite
        /// it with the new radius, then rebuild the edge mesh.
        /// </summary>
        private void SyncWorkAreaVisualRadius(float newRadius)
        {
            try
            {
                var workArea = GetComponent<WorkArea>();
                if (workArea == null) return;

                var scField = typeof(WorkArea).GetField("selectionCircle", AllInstance);
                if (scField == null) return;
                var sc = scField.GetValue(workArea) as SelectionCircle;
                if (sc == null) return;

                // One-time diagnostic dump of SelectionCircle float fields so we
                // can identify the radius field by name/value.
                if (!_selectionCircleDumped)
                {
                    _selectionCircleDumped = true;
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW][DIAG] SelectionCircle type: {sc.GetType().FullName}");
                    Type walk = sc.GetType();
                    while (walk != null && walk != typeof(MonoBehaviour) && walk != typeof(object))
                    {
                        foreach (var f in walk.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                        {
                            if (f.FieldType != typeof(float)) continue;
                            try
                            {
                                float val = (float)f.GetValue(sc);
                                WardenOfTheWildsMod.Log.Msg(
                                    $"[WotW][DIAG]   [{walk.Name}] {f.Name} = {val}");
                            }
                            catch { }
                        }
                        walk = walk.BaseType;
                    }
                }

                // Try the list of known/likely field names.
                // Confirmed from [DIAG] log: the real field is the C# auto-
                // property backing field "<radius>k__BackingField".
                string[] candidates = {
                    "<radius>k__BackingField",
                    "radius", "_radius", "currentRadius", "m_Radius",
                    "ringRadius", "circleRadius", "outerRadius", "size"
                };

                FieldInfo radiusField = null;
                Type t = sc.GetType();
                while (t != null && radiusField == null)
                {
                    foreach (string name in candidates)
                    {
                        var f = t.GetField(name, AllInstance | BindingFlags.DeclaredOnly);
                        if (f != null && f.FieldType == typeof(float))
                        {
                            radiusField = f;
                            break;
                        }
                    }
                    t = t.BaseType;
                }

                if (radiusField != null)
                {
                    float oldVal = (float)radiusField.GetValue(sc);
                    radiusField.SetValue(sc, newRadius);
                    sc.CreateEdgeObjects();
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' SelectionCircle.{radiusField.Name}: " +
                        $"{oldVal:F1} -> {newRadius:F1}");
                }
                else
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] SyncWorkAreaVisualRadius: no matching radius field on SelectionCircle. " +
                        "See [DIAG] lines above for available fields.");
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SyncWorkAreaVisualRadius: {ex.Message}");
            }
        }

        // One-shot diagnostic flag so we only dump SelectionCircle fields once
        private static bool _selectionCircleDumped = false;

        private float GetCurrentFishingRadius()
        {
            try
            {
                var shack = GetComponent<Building>();
                if (shack == null) return 30f;

                var prop = shack.GetType().GetProperty("fishingRadius", AllInstance);
                if (prop != null)
                {
                    object val = prop.GetValue(shack);
                    if (val is float f) return f;
                }

                var field = shack.GetType().GetField("_fishingRadius", AllInstance);
                if (field != null)
                {
                    object val = field.GetValue(shack);
                    if (val is float f) return f;
                }
            }
            catch { }
            return 30f; // Vanilla default
        }

        // -- Water tile counting (ported from HunterCabinEnhancement) -------
        private static int CountWaterTiles(Vector3 center, float radius)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm == null || gm.terrainManager == null) return 0;

                int waterCount = 0;
                float step = 8f;

                for (float x = -radius; x <= radius; x += step)
                {
                    for (float z = -radius; z <= radius; z += step)
                    {
                        if (x * x + z * z > radius * radius) continue;

                        Vector2 samplePos = new Vector2(center.x + x, center.z + z);
                        bool isOcean;
                        if (gm.terrainManager.GetIsWater(samplePos, step, step, step, out isOcean))
                            waterCount++;
                    }
                }

                return waterCount;
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] CountWaterTiles: {ex.Message}");
            }
            return 0;
        }

        // -- Winter bonus management ----------------------------------------
        /// <summary>
        /// Scans all FishingShackEnhancements. If any have Creeler slots,
        /// enables FishingManager.fishSchoolBonusInWinter (traps work under ice).
        /// </summary>
        private static void UpdateGlobalWinterBonus()
        {
            try
            {
                bool anyCreeler = false;
                foreach (var enh in UnityEngine.Object.FindObjectsOfType<FishingShackEnhancement>())
                {
                    if (enh.CreelerSlots > 0) { anyCreeler = true; break; }
                }

                if (anyCreeler == _winterBonusApplied) return;
                _winterBonusApplied = anyCreeler;

                // Find FishingManager and set the hidden winter flag
                Type fmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    fmType = asm.GetType("FishingManager");
                    if (fmType != null) break;
                }
                if (fmType == null) return;

                var fm = UnityEngine.Object.FindObjectOfType(fmType);
                if (fm == null) return;

                var field = fmType.GetField("fishSchoolBonusInWinter", AllInstance);
                if (field != null)
                {
                    field.SetValue(fm, anyCreeler);
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] FishingManager.fishSchoolBonusInWinter -> {anyCreeler} " +
                        $"(Creeler traps work under ice)");
                }
                else
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] fishSchoolBonusInWinter field not found on FishingManager.");
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] UpdateGlobalWinterBonus: {ex.Message}");
            }
        }

        // -- Update: mode cycling + Creeler daily tick ----------------------
        private void Update()
        {
            if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

            // -- Selection tracking for work area circle --------------------
            if (_cachedSelectable == null)
                _cachedSelectable = GetComponent<SelectableComponent>();
            bool selected = _cachedSelectable != null && _cachedSelectable.IsSelected;

            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                // (No custom visual circle to toggle — see UpdateCreelerVisualCircle note)
            }

            // Mode switching is handled by the slider UI (injected separately),
            // no hotkey. Players click the slider on the shack's info panel.

            // -- Creeler daily production tick ------------------------------
            if (CreelerSlots <= 0) return;

            int currentDay = GetCurrentGameDay();
            if (currentDay < 0) return;

            if (_lastDayChecked < 0)
            {
                _lastDayChecked = currentDay;
                return;
            }

            if (currentDay > _lastDayChecked)
            {
                int elapsed = currentDay - _lastDayChecked;
                _lastDayChecked = currentDay;
                _daysSinceLastCrabSpawn += elapsed;

                int interval = GetCrabSpawnInterval();
                if (_daysSinceLastCrabSpawn >= interval)
                {
                    _daysSinceLastCrabSpawn = 0;
                    // Defer the deposit by a per-shack random offset so multiple
                    // shacks firing on the same game-day boundary don't pile their
                    // AddItems cascades (work-bucket re-eval + wagon notifications)
                    // onto the same frame. With 1 shack this just shifts the spike
                    // off the day-tick frame; at 12+ shacks it spreads the cascades
                    // across ~30 frames so no single frame is overloaded.
                    Invoke(nameof(ProduceCrabTrapFish), UnityEngine.Random.Range(0f, 0.5f));
                }
            }
        }

        private int GetCrabSpawnInterval()
        {
            int baseInterval = WardenOfTheWildsMod.CrabTrapSpawnDays.Value;
            if (_waterBonusActive)
            {
                float bonus = WardenOfTheWildsMod.CreelerWaterTileBonus.Value;
                baseInterval = Math.Max(2, (int)Math.Round(baseInterval / (double)bonus));
            }
            return Math.Max(1, baseInterval);
        }

        // -- Creeler fish production ----------------------------------------
        private void ProduceCrabTrapFish()
        {
            try
            {
                int fishPerSlot = WardenOfTheWildsMod.CrabTrapFishPerSpawn.Value;
                int totalFish = fishPerSlot * CreelerSlots;
                if (totalFish <= 0) return;

                // Lazy-init storage and item references
                if (!_storageLookupDone)
                {
                    _storageLookupDone = true;
                    DiscoverFishStorageAndItem();
                }

                if (_fishStorage == null || _addItemsMethod == null || _itemFishRef == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        $"[WotW] CrabTrap '{gameObject.name}': storage/item not resolved. " +
                        $"(storage={_fishStorage != null}, method={_addItemsMethod != null}, " +
                        $"item={_itemFishRef != null})");
                    return;
                }

                // Clamp deposit to FishingShackStorageCap. Vanilla AddItems on the
                // shack's storage writes past the per-item cap as long as total
                // bldg capacity has room — without this clamp, a shack whose fish
                // never gets hauled would silently accumulate fish forever (same
                // failure mode as the Trap Master bear bonus on hunter lodges).
                // Mirrors TrapMasterBearChancePatch.DepositClamped, but resolves
                // GetItemCount via reflection since _fishStorage is typed as object.
                int cap = WardenOfTheWildsMod.FishingShackStorageCap.Value;
                uint current = GetFishCount();
                uint deposit = 0u;
                if ((uint)totalFish > 0u && current < (uint)cap)
                {
                    uint room = (uint)cap - current;
                    deposit = (uint)Math.Min(totalFish, (int)room);
                }

                uint added = 0u;
                if (deposit > 0u)
                {
                    var bundle = new ItemBundle((Item)_itemFishRef, deposit, 100u);
                    _addItemsMethod.Invoke(_fishStorage, new object[] { bundle });
                    uint after = GetFishCount();
                    added = after >= current ? after - current : 0u;
                }

                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] CrabTrap '{gameObject.name}': +{added}/{totalFish} fish " +
                    $"({CreelerSlots} slots x {fishPerSlot}/spawn, " +
                    $"interval={GetCrabSpawnInterval()}d" +
                    $"{(_waterBonusActive ? " [water bonus]" : "")})");

                int dropped = totalFish - (int)added;
                if (dropped > 0)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        $"[WotW] CrabTrap '{gameObject.name}' at cap " +
                        $"(FishingShackStorageCap={cap}) — forfeit {dropped} of {totalFish} fish.");
                }

                // Wake-up call removed (2026-05-05). Was: TriggerWorkAvailabilityCheck()
                // here forced vanilla's work-bucket re-eval so wagons would notice the
                // new fish "immediately" rather than waiting for vanilla's next poll.
                //
                // Two existing safeguards already cover that gap:
                //   • Vanilla runs CheckWorkAvailability on storage events + day-tick.
                //   • Manifest Delivery's wagon AI continuously scans for haul targets
                //     (~500-1500ms cycle) and picks up fresh fish without prompting.
                //
                // The cascade through vanilla → MD's CampHaul scan was producing a
                // visible day-boundary freeze on every Creeler tick. Letting natural
                // polling do the work is both faster (no synchronous cascade) and
                // simpler (no reflection-driven nudge to maintain).
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] ProduceCrabTrapFish: {ex.Message}");
            }
        }

        // TriggerWorkAvailabilityCheck() removed 2026-05-05. See ProduceCrabTrapFish
        // for the rationale — vanilla and Manifest Delivery already poll work
        // availability frequently enough that an explicit nudge is redundant, and
        // the synchronous cascade was the source of a day-boundary freeze.

        /// <summary>
        /// Discovers fish storage and ItemFish reference via reflection.
        /// Tries multiple storage candidates on the FishingShack building.
        /// </summary>
        private void DiscoverFishStorageAndItem()
        {
            try
            {
                // Get ItemFish from WorkBucketManager
                var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
                if (wbm == null) return;

                _itemFishRef = FindFieldOrProperty(wbm, "itemFish");
                if (_itemFishRef == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] DiscoverFishStorage: itemFish not found on WorkBucketManager.");
                    return;
                }

                // Find storage on the building
                var building = GetComponent<Building>();
                if (building == null) return;

                // Try common storage fields/properties
                string[] storageNames = {
                    "fishStorage", "outputStorage", "manufacturingStorage",
                    "storage", "itemStorage", "_fishStorage", "_outputStorage",
                    "_storage", "productStorage"
                };

                foreach (string name in storageNames)
                {
                    object storage = FindFieldOrProperty(building, name);
                    if (storage != null)
                    {
                        // Verify it has AddItems method
                        var method = storage.GetType().GetMethod("AddItems",
                            new Type[] { typeof(ItemBundle) });
                        if (method != null)
                        {
                            _fishStorage = storage;
                            _addItemsMethod = method;
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] CrabTrap: using storage '{name}' on '{gameObject.name}'");
                            return;
                        }
                    }
                }

                // Fallback: search all components for any ItemStorage type
                foreach (var comp in GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var method = comp.GetType().GetMethod("AddItems",
                        new Type[] { typeof(ItemBundle) });
                    if (method != null)
                    {
                        _fishStorage = comp;
                        _addItemsMethod = method;
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] CrabTrap: using component '{comp.GetType().Name}' " +
                            $"as storage on '{gameObject.name}'");
                        return;
                    }
                }

                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] CrabTrap: no suitable storage found on '{gameObject.name}'. " +
                    $"Building type: {building.GetType().Name}");
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] DiscoverFishStorage: {ex.Message}");
            }
        }

        // -- Game day detection ---------------------------------------------
        // Direct property access — TimeManager.currentDate is a CEDateTime struct
        // with public year/month/day properties. The previous reflection lookup
        // searched for fields like "currentDay", "totalDaysPassed", etc. — none
        // of which exist. That lookup always failed, GetCurrentGameDay always
        // returned -1, and ProduceCrabTrapFish never fired (Creeler trap fish
        // never deposited into shack storage). Fixed by using the actual API.

        private static int GetCurrentGameDay()
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm == null) return -1;
                var tm = gm.timeManager;
                if (tm == null) return -1;

                // Compute strictly-increasing absolute day since game start.
                // CETimeSpan uses 360 days/year, 30 days/month — same convention.
                CEDateTime d = tm.currentDate;
                return d.year * 360 + (d.month - 1) * 30 + Mathf.FloorToInt(d.day);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] GetCurrentGameDay: {ex.Message}");
                return -1;
            }
        }

        // -- No custom radius circle ----------------------------------------
        //
        // Decision: we do NOT draw our own visual ring for the expanded Creeler
        // radius. The FishingShack's native WorkArea already draws a ring, and
        // when the player moves the work area it only repositions that one.
        // Drawing a second ring anchored to the shack's transform causes the
        // two to visibly drift apart — a poor UX.
        //
        // The shack's fishing logic uses our modified _fishingRadius value
        // (set via SetFishingRadius), so Creeler gets its 2x range invisibly.
        // Players see the mode in the slider and its description in the
        // tooltip — that's clear enough signalling without the visual drift.

        // -- Reflection helpers ---------------------------------------------
        private static FieldInfo FindBackingField(Type startType, string propertyName)
        {
            string backingName = $"<{propertyName}>k__BackingField";
            Type t = startType;
            while (t != null)
            {
                var f = t.GetField(backingName, AllInstance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>
        /// Finds a field or property value on an object, walking the type hierarchy.
        /// Returns null if not found.
        /// </summary>
        private static object FindFieldOrProperty(object target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            while (t != null)
            {
                var prop = t.GetProperty(name, AllInstance);
                if (prop != null && prop.CanRead)
                    return prop.GetValue(target);
                var field = t.GetField(name, AllInstance);
                if (field != null)
                    return field.GetValue(target);
                t = t.BaseType;
            }
            return null;
        }
    }
}
