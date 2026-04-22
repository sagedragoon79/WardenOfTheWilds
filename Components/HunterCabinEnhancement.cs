using MelonLoader;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  HunterCabinEnhancement
//  MonoBehaviour attached to every HunterCabin at scene load.
//
//  Manages per-building T2 path selection and applies stat changes.
//  Follows the WagonShopEnhancement pattern from Manifest Delivery:
//    - Position-based persistence (survives save/load and upgrades)
//    - Backing-field reflection for maxWorkers
//    - WorkArea visual circle
//    - OnGUI label when selected
//
//  T2 Paths:
//    Vanilla     — no change until player chooses a path
//    TrapperLodge — primary focus: traps + pelts/tallow
//                  • Traps remain enabled (they ARE the mechanic)
//                  • Pelt/tallow yield multiplied (see Plugin preferences)
//                  • Unlocks willow trap crafting (if Tended Wilds active)
//                  • 1 worker (trapper doing rounds of trap lines)
//    HuntingLodge — primary focus: meat + big game
//                  • Traps DISABLED (hunter free-roams only)
//                  • 2 worker slots
//                  • Work radius multiplied (see Plugin preferences)
//                  • Unlocks Deer Stand placement
//                  • Access to bear/boar hunting (if game supports it)
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Components
{
    public enum HunterT2Path
    {
        Vanilla,       // T1 or T2 not yet specialised
        TrapperLodge,  // Pelt/trap focus
        HuntingLodge,  // Meat/range focus — unlocks Deer Stands
    }

    public class HunterCabinEnhancement : MonoBehaviour
    {
        // ── Shared binding flags ──────────────────────────────────────────────
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Persistence: position-keyed dictionary (survives save/load) ───────
        private static readonly Dictionary<int, HunterT2Path> SavedPaths =
            new Dictionary<int, HunterT2Path>();

        private int GetBuildingKey()
        {
            var pos = transform.position;
            return Mathf.RoundToInt(pos.x * 1000f + pos.z);
        }

        // ── State ─────────────────────────────────────────────────────────────
        private HunterT2Path _path = HunterT2Path.Vanilla;
        private bool _initialized = false;
        private WorkArea? _workArea;

        public HunterT2Path Path
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value;
                SavedPaths[GetBuildingKey()] = value;
                OnPathChanged();
            }
        }

        // ── Scene reset (called from Plugin.OnSceneWasLoaded) ─────────────────
        public static void OnMapLoaded()
        {
            SavedPaths.Clear();
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────
        private void Start()
        {
            StartCoroutine(InitializeDelayed());
        }

        private IEnumerator InitializeDelayed()
        {
            yield return null; // One frame for Building component to settle
            if (_initialized) yield break;
            _initialized = true;

            RestoreSavedPath();
            ApplyPath();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] HunterCabinEnhancement attached to '{gameObject.name}' " +
                $"(key={GetBuildingKey()}, path={_path})");
        }

        private void RestoreSavedPath()
        {
            if (SavedPaths.TryGetValue(GetBuildingKey(), out HunterT2Path saved))
                _path = saved;
        }

        // ── Path application ──────────────────────────────────────────────────
        private void OnPathChanged()
        {
            ApplyPath();
            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] Hunter '{gameObject.name}' path → {_path}");

            PostPathChangeNotification();
        }

        /// <summary>
        /// Posts an event-log entry when the path transitions to one of the
        /// named T2 specializations. Silent on the "Vanilla" (balanced middle)
        /// transition and on T1 hunters — those don't deserve a banner.
        /// </summary>
        private void PostPathChangeNotification()
        {
            try
            {
                string summary = _path switch
                {
                    HunterT2Path.HuntingLodge =>
                        "A hunter cabin has committed to Big Game Hunting. " +
                        "Bears, wolves, and boars are now fair game.",
                    HunterT2Path.TrapperLodge =>
                        "A hunter cabin has become a Trap Master's lodge. " +
                        "Foxes and groundhogs beware.",
                    _ => null,  // Vanilla / other transitions: no notification
                };
                if (summary == null) return;

                // Reuse UIEventLogWindow.AddEventToLog the same way
                // HunterDeathNotificationPatch does.
                var window = UnityEngine.Object.FindObjectOfType<UIEventLogWindow>();
                if (window == null) return;

                var addMethod = typeof(UIEventLogWindow).GetMethod(
                    "AddEventToLog",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                addMethod?.Invoke(window, new object[] { summary, null });

                WardenOfTheWildsMod.Log.Msg($"[WotW] Event log: {summary}");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] PostPathChangeNotification: {ex.Message}");
            }
        }

        private void ApplyPath()
        {
            // Trapping is T2-only — T1 has no trap slider or spawn interval.
            // Confirmed from log: T1 trappingCarcassSpawnInterval = 0d (uninitialized).
            // Only apply trap changes when at T2.
            var building = GetComponent<Building>();
            if (building == null) return;

            bool isT2 = building.tier >= 2;

            switch (_path)
            {
                case HunterT2Path.TrapperLodge:
                    // Trap specialist — relies on the T2 trap system entirely.
                    // Trap count increased above vanilla (1 → configurable),
                    // spawn interval reduced proportionally to pelt multiplier.
                    SetWorkerSlots(1);
                    SetWorkRadius(1.0f);            // Same radius — trap lines are local
                    SetWorkerSpeed(1.0f);           // No speed boost — trappers walk routes
                    if (isT2)
                    {
                        SetTrapsEnabled(true);
                        SetTrapCount(WardenOfTheWildsMod.TrapperLodgeTrapCount.Value);
                        SetTrapSpawnInterval(CalculateTrapperPeltMult());
                    }
                    UpdateWorkAreaCircle(0f);
                    break;

                case HunterT2Path.HuntingLodge:
                    // Bow hunter — disable traps entirely, free up the hunter's full time
                    // for active pursuit. 2 workers, expanded radius, speed boost.
                    SetWorkerSlots(2);
                    SetWorkRadius(WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value);
                    SetWorkerSpeed(WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value);
                    if (isT2)
                    {
                        SetTrapsEnabled(false);     // No traps — hunter free-roams only
                        SetTrapSpawnInterval(1.0f); // Restore baseline (traps off anyway)
                    }
                    UpdateWorkAreaCircle(GetCurrentWorkRadius());
                    LogKitingParameters();
                    break;

                case HunterT2Path.Vanilla:
                default:
                    // Restore vanilla T2 defaults (1 trap, 26d interval, 1 worker)
                    SetWorkerSlots(1);
                    SetWorkRadius(1.0f);
                    SetWorkerSpeed(1.0f);
                    if (isT2)
                    {
                        SetTrapsEnabled(true);
                        SetTrapCount(1);            // Vanilla = 1 trap
                        SetTrapSpawnInterval(1.0f); // Restore 26d baseline
                    }
                    UpdateWorkAreaCircle(0f);
                    break;
            }
        }

        // ── Trapper environment bonuses ───────────────────────────────────────
        // All three fields are cached at path-selection time (ApplyPath → SetTrapSpawnInterval).
        // OnGUI reads these cached values — never calls FindObjectsOfType on the render thread.
        private bool  _trapperNearWater = false;
        private float _cachedPeltMult   = 1.0f;

        /// <summary>
        /// Calculates the effective pelt multiplier for TrapperLodge, factoring in
        /// water proximity bonus. (Coop proximity was dropped — foxes don't actually
        /// interact with coops in vanilla, so the flavor was misleading.)
        /// Result is passed directly to SetTrapSpawnInterval as the multiplier.
        /// </summary>
        private float CalculateTrapperPeltMult()
        {
            float mult = WardenOfTheWildsMod.TrapperLodgePeltMult.Value;

            _trapperNearWater = IsWaterNearby(
                transform.position,
                WardenOfTheWildsMod.TrapperWaterBonusRadius.Value);

            if (_trapperNearWater)
                mult *= WardenOfTheWildsMod.TrapperWaterBonus.Value;

            _cachedPeltMult = mult; // Cache for OnGUI — never recompute on the render thread

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] Trapper '{gameObject.name}' pelt mult: {mult:F2} " +
                $"(water: {_trapperNearWater})");

            return mult;
        }

        /// <summary>
        /// Returns true if a water body (river, pond, lake) exists within <paramref name="radius"/>
        /// world units of <paramref name="position"/>.
        /// Candidate class names sourced from known Farthest Frontier types; fallbacks kept for
        /// version resilience.
        /// </summary>
        private static bool IsWaterNearby(Vector3 position, float radius)
        {
            try
            {
                float rSqr = radius * radius;
                // Confirmed candidates from FF assembly; Pond is used by FishingShack
                string[] waterClasses = { "Pond", "WaterBody", "RiverSection", "Lake", "WaterArea" };

                foreach (string className in waterClasses)
                {
                    System.Type? waterType = null;
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        waterType = asm.GetType(className);
                        if (waterType != null) break;
                    }
                    if (waterType == null) continue;

                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(waterType))
                    {
                        var comp = obj as Component;
                        if (comp == null) continue;
                        if ((comp.transform.position - position).sqrMagnitude <= rSqr)
                            return true;
                    }
                    // Found the type — if nothing was in range, no need to try other names
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] IsWaterNearby: {ex.Message}");
            }
            return false;
        }

        // ── Worker slot adjustment (Manifest Delivery pattern) ───────────────
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
                            $"[WotW] '{gameObject.name}' maxWorkers: {current} → {targetMax}");
                    }
                }

                if (building.userDefinedMaxWorkers > targetMax)
                    building.userDefinedMaxWorkers = targetMax;
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SetWorkerSlots failed on '{gameObject.name}': {ex.Message}");
            }
        }

        // ── Work radius adjustment ────────────────────────────────────────────
        // Confirmed from method dump: huntingRadius is a PROPERTY with get/set.
        // Use the property setter directly — cleaner than backing field search.
        private void SetWorkRadius(float multiplier)
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;

                float current = GetCurrentWorkRadius();
                if (_baselineRadius < 0f) _baselineRadius = current;

                float newRadius = _baselineRadius * multiplier;

                // Invoke set_huntingRadius(float) via the property setter
                var setter = building.GetType().GetMethod("set_huntingRadius", AllInstance);
                if (setter != null)
                {
                    setter.Invoke(building, new object[] { newRadius });
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' huntingRadius: {_baselineRadius:F1} → {newRadius:F1}");
                }
                else
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] SetWorkRadius: set_huntingRadius not found on HunterBuilding.");
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetWorkRadius failed: {ex.Message}");
            }
        }

        private float _baselineRadius = -1f;

        private float GetCurrentWorkRadius()
        {
            var building = GetComponent<Building>();
            if (building == null) return 100f;
            try
            {
                var getter = building.GetType().GetMethod("get_huntingRadius", AllInstance);
                if (getter != null)
                {
                    object? raw = getter.Invoke(building, null);
                    return raw is float f ? f : 100f;
                }
            }
            catch { }
            return 100f; // Confirmed from log: vanilla huntingRadius = 100.0 world units
        }

        // ── Worker movement speed ─────────────────────────────────────────────
        // Confirmed from 26-4-18 dump: CombatManager has a `_hunterMoveSpeedBonus`
        // float field (line 1314). This is likely how the tech tree applies its
        // off-road speed bonus to hunters. We read the current value as baseline
        // and multiply it on HuntingLodge path selection.
        //
        // Fallback chain:
        //   A) CombatManager._hunterMoveSpeedBonus  (confirmed field — primary)
        //   B) HunterBuilding speed field/property  (if A not found)
        //   C) NavMeshAgent.speed on this GameObject (last resort)
        private float _baselineWorkerSpeed = -1f;

        private void SetWorkerSpeed(float multiplier)
        {
            try
            {
                // ── Path A: CombatManager._hunterMoveSpeedBonus ───────────────
                // Confirmed field from dump. CombatManager is likely a singleton —
                // find it with FindObjectOfType, then reflect the field.
                System.Type? cmType = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    cmType = asm.GetType("CombatManager");
                    if (cmType != null) break;
                }

                if (cmType != null)
                {
                    var cmObj = UnityEngine.Object.FindObjectOfType(cmType) as UnityEngine.Object;
                    if (cmObj != null)
                    {
                        var speedField = cmType.GetField("_hunterMoveSpeedBonus", AllInstance);
                        if (speedField != null)
                        {
                            object? raw = speedField.GetValue(cmObj);
                            float current = raw != null ? System.Convert.ToSingle(raw) : 1f;

                            // Cache baseline on first call (before any path has changed it)
                            if (_baselineWorkerSpeed < 0f)
                                _baselineWorkerSpeed = current > 0f ? current : 1f;

                            float newVal = _baselineWorkerSpeed * multiplier;
                            speedField.SetValue(cmObj, newVal);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' CombatManager._hunterMoveSpeedBonus: " +
                                $"{_baselineWorkerSpeed:F3} → {newVal:F3}");
                            return;
                        }
                    }
                }

                // ── Path B: HunterBuilding speed field/property ───────────────
                var building = GetComponent<Building>();
                if (building != null)
                {
                    string[] buildingSpeedFields = {
                        "workerSpeedMultiplier", "hunterSpeedMultiplier",
                        "offRoadSpeedMultiplier", "terrainSpeedMultiplier",
                        "movementSpeedMultiplier", "speedMult",
                    };
                    var bType = building.GetType();
                    foreach (string name in buildingSpeedFields)
                    {
                        var prop = bType.GetProperty(name, AllInstance);
                        if (prop != null && prop.CanWrite)
                        {
                            if (_baselineWorkerSpeed < 0f)
                            {
                                object? cur = prop.GetValue(building);
                                _baselineWorkerSpeed = cur != null ? System.Convert.ToSingle(cur) : 1f;
                            }
                            prop.SetValue(building, _baselineWorkerSpeed * multiplier);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' {name}: " +
                                $"{_baselineWorkerSpeed:F2} → {_baselineWorkerSpeed * multiplier:F2}");
                            return;
                        }
                        var field = bType.GetField(name, AllInstance);
                        if (field != null)
                        {
                            if (_baselineWorkerSpeed < 0f)
                            {
                                object? cur = field.GetValue(building);
                                _baselineWorkerSpeed = cur != null ? System.Convert.ToSingle(cur) : 1f;
                            }
                            field.SetValue(building, _baselineWorkerSpeed * multiplier);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' {name}: " +
                                $"{_baselineWorkerSpeed:F2} → {_baselineWorkerSpeed * multiplier:F2}");
                            return;
                        }
                    }
                }

                // ── Path C: NavMeshAgent on the building GameObject ───────────
                var agent = gameObject.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null)
                {
                    if (_baselineWorkerSpeed < 0f) _baselineWorkerSpeed = agent.speed;
                    agent.speed = _baselineWorkerSpeed * multiplier;
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' NavMeshAgent.speed: " +
                        $"{_baselineWorkerSpeed:F2} → {agent.speed:F2}");
                    return;
                }

                WardenOfTheWildsMod.Log.Msg(
                    "[WotW] SetWorkerSpeed: CombatManager._hunterMoveSpeedBonus not found " +
                    "and no fallback succeeded — speed unchanged.");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetWorkerSpeed failed: {ex.Message}");
            }
        }

        // ── Kiting parameter log ──────────────────────────────────────────────
        // Called on HuntingLodge path selection. Logs the computed kiting
        // parameters for each dangerous animal at current hunter stats so the
        // optimal retreat distances are visible in the MelonLoader log.
        private void LogKitingParameters()
        {
            float speed  = _baselineWorkerSpeed > 0f
                ? _baselineWorkerSpeed * WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value
                : WardenOfTheWilds.Systems.AnimalBehaviorSystem.KitingCalculator.FallbackHunterSpeed
                  * WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value;

            // Reload time: read from trappingCarcassSpawnInterval (days→seconds estimate)
            // or fall back to the known constant. This will sharpen once the shoot
            // cooldown field is confirmed from the combat dump.
            float reload = WardenOfTheWilds.Systems.AnimalBehaviorSystem
                               .KitingCalculator.FallbackReloadSeconds
                           / WardenOfTheWildsMod.HuntingLodgeBigGameShootMult.Value;

            WardenOfTheWildsMod.Log.Msg($"[WotW] HuntingLodge '{gameObject.name}' kiting parameters:");
            WardenOfTheWildsMod.Log.Msg($"[WotW]   Hunter speed:  {speed:F2} u/s  " +
                                      $"(base × {WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value:F2})");
            WardenOfTheWildsMod.Log.Msg($"[WotW]   Reload time:   {reload:F2} s  " +
                                      $"(÷ shoot mult {WardenOfTheWildsMod.HuntingLodgeBigGameShootMult.Value:F2})");

            foreach (string animal in new[] { "Boar", "Wolf", "Bear" })
            {
                string summary = WardenOfTheWilds.Systems.AnimalBehaviorSystem
                    .KitingCalculator.Summarize(animal, reload, speed);
                WardenOfTheWildsMod.Log.Msg($"[WotW]   {summary}");
            }
        }

        // ── Trap count (max deployed traps slider) ────────────────────────────
        // Confirmed from log: vanilla T2 = 1 trap (userDefinedMaxDeployedTraps = 1).
        // TrapperLodge raises this so the trapper can run more lines simultaneously.
        private void SetTrapCount(int count)
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;
                var type = building.GetType();

                var setter = type.GetMethod("set_userDefinedMaxDeployedTraps", AllInstance);
                if (setter == null) return;

                setter.Invoke(building, new object[] { count });
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' userDefinedMaxDeployedTraps → {count}");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetTrapCount failed: {ex.Message}");
            }
        }

        // ── Trap enable/disable ───────────────────────────────────────────────
        // Confirmed from method dump: userDefinedMaxDeployedTraps is a PROPERTY.
        // get_userDefinedMaxDeployedTraps() / set_userDefinedMaxDeployedTraps(Int32)
        // Setting to 0 prevents trap deployment; restoring the cached value re-enables.
        private int _vanillaTrapCount = -1;

        private void SetTrapsEnabled(bool enabled)
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;
                var type = building.GetType();

                var getter = type.GetMethod("get_userDefinedMaxDeployedTraps", AllInstance);
                var setter = type.GetMethod("set_userDefinedMaxDeployedTraps", AllInstance);
                if (setter == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] SetTrapsEnabled: set_userDefinedMaxDeployedTraps not found.");
                    return;
                }

                // Cache vanilla value on first call
                if (_vanillaTrapCount < 0 && getter != null)
                {
                    object? raw = getter.Invoke(building, null);
                    _vanillaTrapCount = raw is int i ? i : 1; // Confirmed from log: vanilla = 1 trap
                }

                int newVal = enabled ? System.Math.Max(1, _vanillaTrapCount) : 0;
                setter.Invoke(building, new object[] { newVal });
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' userDefinedMaxDeployedTraps → {newVal} " +
                    $"(traps {(enabled ? "ON" : "OFF")})");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetTrapsEnabled failed: {ex.Message}");
            }
        }

        // ── Trap spawn interval (TrapperLodge output multiplier) ─────────────
        // Confirmed from method dump: trappingCarcassSpawnInterval is a read/write
        // property on HunterBuilding (get_trappingCarcassSpawnInterval / set_trappingCarcassSpawnInterval).
        // Dividing the interval by PeltMult makes traps fire proportionally more often.
        private int _baselineSpawnInterval = -1;

        private void SetTrapSpawnInterval(float multiplier)
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;
                var type = building.GetType();

                var getter = type.GetMethod("get_trappingCarcassSpawnInterval", AllInstance);
                var setter = type.GetMethod("set_trappingCarcassSpawnInterval", AllInstance);
                if (getter == null || setter == null) return;

                // Cache baseline on first call
                if (_baselineSpawnInterval < 0)
                {
                    object? raw = getter.Invoke(building, null);
                    // Confirmed from log: T2 HunterShack trappingCarcassSpawnInterval = 26 days
                    // T1 reports 0 (uses a different hunting recipe system, not interval-based)
                    int read = raw is int i ? i : 0;
                    _baselineSpawnInterval = read > 0 ? read : 26;
                }

                // multiplier > 1 = more output → divide interval (shorter = faster)
                int newInterval = multiplier <= 1f
                    ? _baselineSpawnInterval
                    : (int)System.Math.Max(1, System.Math.Round(_baselineSpawnInterval / (double)multiplier));

                setter.Invoke(building, new object[] { newInterval });
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' trappingCarcassSpawnInterval: " +
                    $"{_baselineSpawnInterval}d → {newInterval}d (×{multiplier:F1})");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetTrapSpawnInterval failed: {ex.Message}");
            }
        }

        // ── Work area visual circle (Manifest Delivery pattern, verbatim) ─────
        private void UpdateWorkAreaCircle(float radius)
        {
            try
            {
                if (radius <= 0f)
                {
                    _workArea?.SetEnabled(false);
                    return;
                }

                if (_workArea == null)
                {
                    _workArea = gameObject.GetComponent<WorkArea>();
                    if (_workArea == null)
                        _workArea = gameObject.AddComponent<WorkArea>();
                }

                _workArea.Init(transform.position, radius);
                RegenerateSelectionCircleEdges(_workArea);

                var sel = GetComponent<SelectableComponent>();
                _workArea.SetEnabled(sel != null && sel.IsSelected);
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] UpdateWorkAreaCircle failed: {ex.Message}");
            }
        }

        private static void RegenerateSelectionCircleEdges(WorkArea workArea)
        {
            try
            {
                var scField = typeof(WorkArea).GetField("selectionCircle", AllInstance);
                if (scField == null) return;
                var sc = scField.GetValue(workArea);
                if (sc == null) return;
                sc.GetType()
                  .GetMethod("CreateEdgeObjects", BindingFlags.Public | BindingFlags.Instance)
                  ?.Invoke(sc, null);
            }
            catch { }
        }

        // ── Selection / UI ────────────────────────────────────────────────────
        private bool _lastSelected = false;

        // Track the trap slider so we only react when it changes (not every frame)
        private int _lastSliderValue = -1;
        private float _lastSliderCheck = 0f;
        private const float SliderCheckInterval = 0.5f;

        private void Update()
        {
            var sel = GetComponent<SelectableComponent>();
            bool selected = sel != null && sel.IsSelected;

            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                if (_workArea != null && _path == HunterT2Path.HuntingLodge)
                    _workArea.SetEnabled(selected);
            }

            var building = GetComponent<Building>();
            if (building == null) return;

            // ── Slider-as-path-selector (T2 only) ─────────────────────────────
            // The trap count slider IS the path selector:
            //   slider = 0                  → HuntingLodge (BGH — no traps)
            //   slider = max                → TrapperLodge (full specialization)
            //   slider = 1..max-1           → Vanilla (balanced middle)
            //
            // Poll at 0.5s intervals rather than per-frame — slider value only
            // changes when the player clicks it. Cheap enough regardless.
            if (building.tier >= 2 && Time.time - _lastSliderCheck >= SliderCheckInterval)
            {
                _lastSliderCheck = Time.time;
                var hunterBuilding = building as HunterBuilding;
                if (hunterBuilding != null)
                {
                    int slider = hunterBuilding.userDefinedMaxDeployedTraps;
                    int maxTraps = hunterBuilding.maxDeployedTraps;
                    if (slider != _lastSliderValue)
                    {
                        _lastSliderValue = slider;
                        HunterT2Path nextPath =
                            slider == 0                      ? HunterT2Path.HuntingLodge :
                            (maxTraps > 0 && slider >= maxTraps) ? HunterT2Path.TrapperLodge :
                                                                    HunterT2Path.Vanilla;

                        if (nextPath != _path)
                        {
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' slider → {slider}/{maxTraps} " +
                                $"→ path auto-set to {nextPath}.");
                            Path = nextPath;
                        }
                    }
                }
            }

            // ── Legacy path cycle key (still works but slider overrides) ─────
            // Kept for users who prefer keyboard control. At T2 the slider check
            // above will reassert the path on next tick if the slider disagrees.
            if (!selected) return;
            if (building.tier < 2) return;

            if (Input.GetKeyDown(WardenOfTheWildsMod.HunterPathKey))
            {
                HunterT2Path next = _path switch
                {
                    HunterT2Path.Vanilla       => HunterT2Path.TrapperLodge,
                    HunterT2Path.TrapperLodge  => HunterT2Path.HuntingLodge,
                    HunterT2Path.HuntingLodge  => HunterT2Path.Vanilla,
                    _                           => HunterT2Path.TrapperLodge,
                };
                Path = next;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' path changed to {next} via keypress " +
                    "(slider may reassert on next tick).");
            }
        }

        private void OnGUI()
        {
            var sel = GetComponent<SelectableComponent>();
            if (sel == null || !sel.IsSelected) return;

            Vector3 screenPos = Camera.main != null
                ? Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 7f)
                : Vector3.zero;
            if (screenPos.z <= 0) return;

            float y = Screen.height - screenPos.y;
            var building = GetComponent<Building>();
            bool isT2 = building != null && building.tier >= 2;

            // T1 cabin: show nothing
            if (!isT2) return;

            GUI.color = _path switch
            {
                HunterT2Path.TrapperLodge => new Color(0.9f, 0.75f, 0.3f, 1f),  // Amber
                HunterT2Path.HuntingLodge => new Color(0.5f, 0.85f, 0.5f, 1f),  // Green
                _                          => new Color(0.85f, 0.85f, 0.85f, 1f), // Light grey
            };

            string keyName = WardenOfTheWildsMod.HunterPathKey.ToString();

            string details = _path switch
            {
                HunterT2Path.TrapperLodge =>
                    $"Pelts x{_cachedPeltMult:F2}  1 worker  |  Traps: Active\n" +
                    $"Specialises: Fox  Groundhog\n" +
                    $"{(_trapperNearWater ? "Water bonus" : "")}\n" +
                    $"[{keyName}] cycle path",
                HunterT2Path.HuntingLodge =>
                    $"Radius x{WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value:F1}  " +
                    $"2 workers  |  Traps: Off\n" +
                    $"Kiting: {(WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value ? "Active" : "Off")}  " +
                    $"Deer Stands: {(WardenOfTheWildsMod.DeerStandsEnabled.Value ? "Unlocked" : "Off")}\n" +
                    $"[{keyName}] cycle path",
                _ =>
                    // Vanilla T2 — prompt the player to pick a specialisation
                    $"Not specialised yet.\n" +
                    $"[{keyName}] Trapper Lodge  |  Hunting Lodge",
            };

            GUI.Label(new Rect(screenPos.x - 145, y - 75, 290, 75),
                $"[WotW] {PathDisplayName}\n{details}");
            GUI.color = Color.white;
        }

        public string PathDisplayName => _path switch
        {
            HunterT2Path.TrapperLodge => "Trapper Lodge",
            HunterT2Path.HuntingLodge => "Hunting Lodge",
            _                          => "Hunter Cabin",
        };

        // ── Helper: choose a T2 path (called from patch UI) ──────────────────
        public void ChoosePath(HunterT2Path path)
        {
            var building = GetComponent<Building>();
            if (building == null || building.tier < 2)
            {
                WardenOfTheWildsMod.Log.Warning(
                    "[WotW] ChoosePath: building is not at T2 yet.");
                return;
            }
            Path = path;
        }

        // ── Reflection helpers ────────────────────────────────────────────────
        private static FieldInfo? FindBackingField(System.Type startType, string propertyName)
        {
            string backingName = $"<{propertyName}>k__BackingField";
            System.Type? t = startType;
            while (t != null)
            {
                var f = t.GetField(backingName, AllInstance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static FieldInfo? FindField(System.Type startType, string fieldName)
        {
            System.Type? t = startType;
            while (t != null)
            {
                var f = t.GetField(fieldName, AllInstance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }
    }
}
