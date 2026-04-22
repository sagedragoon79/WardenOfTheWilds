using MelonLoader;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  DeerStandSystem
//  Manages all placed Deer Stands in the current map session.
//
//  Design:
//    Deer Stands are lightweight placeables, not full buildings.
//    They are placed by hunters (unlocked at Hunting Lodge T2) and persist
//    as world-space positions tracked in this static system.
//
//    Placement:
//      Option A (preferred): Reuse the wild-plant BuildingData template pattern
//        from Tended Wilds — use a small existing prefab (e.g. a hunting trap
//        or fence post visual) as the buildSitePrefab, and intercept
//        Input_PlaceBuilding.Construct to tag it as a Deer Stand placement.
//      Option B (fallback): Place as an invisible "building" and drive the
//        effect purely through proximity math in this system.
//      Decision pending decompile — see DESIGN.md § Deer Stand Placement.
//
//    Attraction mechanic:
//      Every N seconds, DeerAttractionTick() runs for each active stand.
//      It finds deer/wildlife within attraction radius and applies a velocity
//      or pathfinding bias toward the stand's position.
//      Farm proximity multiplies this bias.
//      Lure mode adds an extra multiplier but risks slight crop nibbling.
//
//      Key unknowns (to be filled from Assembly-CSharp decompile):
//        • Deer / wildlife class name (Animal? Deer? WildAnimal?)
//        • Pathfinding hook (SetDestination? ApplyAttraction? MoveTo?)
//        • FarmPlot class name and how crop-raiding probability is stored
//
//  Save/load: Stands are stored by world position using the same
//  position-hash pattern as all other mods in this suite.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Systems
{
    public enum DeerStandState
    {
        Passive, // Normal attraction rate
        Lure,    // Enhanced attraction but slight crop nibbling risk
    }

    /// <summary>
    /// Data record for a single placed Deer Stand.
    /// </summary>
    public class DeerStand
    {
        public Vector3      Position;
        public DeerStandState State   = DeerStandState.Passive;
        public Vector3      AssignedHunterCabinPos; // Zero = unassigned
        public bool         HasHunterAssigned => AssignedHunterCabinPos != Vector3.zero;

        // Runtime tracking
        public float LastAttractionTick = 0f;
    }

    public static class DeerStandSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Persistence ───────────────────────────────────────────────────────
        // Key = position hash (x*1000+z rounded), Value = stand data
        private static readonly Dictionary<int, DeerStand> ActiveStands =
            new Dictionary<int, DeerStand>();

        private static int PositionKey(Vector3 pos) =>
            Mathf.RoundToInt(pos.x * 1000f + pos.z);

        public static void OnMapLoaded()
        {
            ActiveStands.Clear();
            MelonLogger.Msg("[WotW] DeerStandSystem: map loaded, stands cleared.");
        }

        // ── Stand registration ────────────────────────────────────────────────
        /// <summary>
        /// Registers a newly placed Deer Stand.
        /// Called from the placement intercept patch (see Patches/DeerStandPatches.cs).
        /// </summary>
        public static DeerStand RegisterStand(Vector3 position)
        {
            int key = PositionKey(position);
            if (ActiveStands.ContainsKey(key))
            {
                MelonLogger.Warning($"[WotW] DeerStandSystem: stand already exists at {position}");
                return ActiveStands[key];
            }

            var stand = new DeerStand { Position = position };
            ActiveStands[key] = stand;

            MelonLogger.Msg($"[WotW] Deer Stand placed at {position} (total: {ActiveStands.Count})");
            return stand;
        }

        public static void RemoveStand(Vector3 position)
        {
            ActiveStands.Remove(PositionKey(position));
            MelonLogger.Msg($"[WotW] Deer Stand removed at {position}");
        }

        public static DeerStand? GetStand(Vector3 position) =>
            ActiveStands.TryGetValue(PositionKey(position), out DeerStand s) ? s : null;

        public static IEnumerable<DeerStand> AllStands => ActiveStands.Values;

        // ── Lure toggle ───────────────────────────────────────────────────────
        public static void ToggleLure(Vector3 standPosition)
        {
            var stand = GetStand(standPosition);
            if (stand == null) return;

            stand.State = stand.State == DeerStandState.Lure
                ? DeerStandState.Passive
                : DeerStandState.Lure;

            MelonLogger.Msg($"[WotW] Deer Stand {standPosition}: state → {stand.State}");
        }

        // ── Hunter cabin assignment ───────────────────────────────────────────
        /// <summary>
        /// Assigns a Hunter Cabin (by position) as the harvesting cabin for a stand.
        /// The Hunting Lodge patch will use this to direct the assigned hunter
        /// to patrol between their assigned stands first before free-roaming.
        /// </summary>
        public static void AssignHunterCabin(Vector3 standPosition, Vector3 cabinPosition)
        {
            var stand = GetStand(standPosition);
            if (stand == null) return;
            stand.AssignedHunterCabinPos = cabinPosition;
            MelonLogger.Msg($"[WotW] Stand {standPosition} assigned to cabin {cabinPosition}");
        }

        // ── Attraction tick ───────────────────────────────────────────────────
        /// <summary>
        /// Applies attraction effect for all active stands.
        /// Called every N seconds from the coroutine in AttractionWatcher.
        /// </summary>
        public static void TickAllStands()
        {
            if (!WardenOfTheWildsMod.DeerStandsEnabled.Value) return;

            foreach (var stand in ActiveStands.Values)
            {
                TickStand(stand);
            }
        }

        private static void TickStand(DeerStand stand)
        {
            float attractionRadius = WardenOfTheWildsMod.DeerStandAttractionRadius.Value;
            float baseBonus        = 1.0f;

            // Farm proximity bonus
            if (IsFarmNearby(stand.Position, 40f))
                baseBonus *= WardenOfTheWildsMod.DeerStandFarmBonus.Value;

            // Tended Wilds: cultivated berries/greens near stand add bonus
            if (WardenOfTheWildsMod.TendedWildsActive)
                baseBonus *= TendedWildsCompat.GetAttractionBonusNear(stand.Position, attractionRadius);

            // Lure mode
            if (stand.State == DeerStandState.Lure)
                baseBonus *= WardenOfTheWildsMod.DeerStandLureBonus.Value;

            // Apply attraction — Deer Stands target hunter prey only.
            // Fox and groundhog are TrapperLodge targets; they have their own
            // pest-suppression system via the TrapperLodge. Stands only pull
            // animals flagged as HunterTarget (deer, and eventually boar/bear
            // once the dangerous-game attraction system is designed).
            ApplyAttractionToDeer(stand.Position, attractionRadius, baseBonus);
        }

        // ── Farm proximity check ──────────────────────────────────────────────
        // Confirmed class name from Assembly-CSharp.dll decompile: Cropfield (lowercase f)
        private static bool IsFarmNearby(Vector3 position, float radius)
        {
            try
            {
                float radiusSqr = radius * radius;

                // Cropfield confirmed; others kept as fallbacks for version resilience
                string[] farmClassNames = { "Cropfield", "FarmPlot", "CropField", "TilledField" };

                foreach (string className in farmClassNames)
                {
                    System.Type? farmType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        farmType = asm.GetType(className);
                        if (farmType != null) break;
                    }
                    if (farmType == null) continue;

                    // Found the type — search for instances in range
                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(farmType))
                    {
                        var comp = obj as Component;
                        if (comp == null) continue;
                        if ((comp.transform.position - position).sqrMagnitude <= radiusSqr)
                            return true;
                    }
                    return false; // Found the type but no nearby instances
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] IsFarmNearby: {ex.Message}");
            }
            return false;
        }

        // ── Deer attraction application ───────────────────────────────────────
        // Confirmed from Assembly-CSharp.dll decompile:
        //   Class hierarchy: Deer : PassiveAnimal
        //   PassiveAnimal.SetWanderPoints(List<Vector3> points) — sets wander waypoints
        //   PassiveAnimal.wanderPoints — private List<Vector3> field
        //
        // Approach: for each deer within (radius * 3) of the stand, inject the
        // stand position at the FRONT of its wander points list and call
        // SetWanderPoints. The bonus multiplier scales how many injection attempts
        // to make (higher bonus = more deer redirected per tick).
        private static void ApplyAttractionToDeer(Vector3 standPos, float radius, float bonus)
        {
            try
            {
                // Resolve Deer type (confirmed name from decompile)
                // Keep PassiveAnimal as fallback base in case Deer isn't directly found
                System.Type? deerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    deerType = asm.GetType("Deer");
                    if (deerType != null) break;
                }
                if (deerType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        deerType = asm.GetType("PassiveAnimal");
                        if (deerType != null) break;
                    }
                }
                if (deerType == null) return;

                // Resolve SetWanderPoints on the type or its base (PassiveAnimal)
                System.Reflection.MethodInfo? setWanderPts = null;
                System.Type? checkType = deerType;
                while (checkType != null && setWanderPts == null)
                {
                    setWanderPts = checkType.GetMethod(
                        "SetWanderPoints",
                        AllInstance);
                    checkType = checkType.BaseType;
                }

                // Resolve wanderPoints backing field (private List<Vector3>)
                System.Reflection.FieldInfo? wanderField = null;
                checkType = deerType;
                while (checkType != null && wanderField == null)
                {
                    wanderField = checkType.GetField(
                        "wanderPoints",
                        AllInstance);
                    checkType = checkType.BaseType;
                }

                // Wider search radius: stand pulls deer from 3× its attraction radius
                float searchRadiusSqr = (radius * 3f) * (radius * 3f);

                // bonus > 1 lets us redirect more deer per tick; cap to avoid spam
                int maxToRedirect = Math.Max(1, (int)Math.Ceiling(bonus * 2f));
                int redirected = 0;

                foreach (UnityEngine.Object obj in
                    UnityEngine.Object.FindObjectsOfType(deerType))
                {
                    if (redirected >= maxToRedirect) break;

                    var comp = obj as Component;
                    if (comp == null) continue;
                    if ((comp.transform.position - standPos).sqrMagnitude > searchRadiusSqr)
                        continue;

                    // Get the deer's current wander points list
                    List<Vector3>? currentPoints = null;
                    if (wanderField != null)
                        currentPoints = wanderField.GetValue(comp) as List<Vector3>;

                    // Build updated list — stand position injected at front so it
                    // becomes the next wander destination for this deer
                    var newPoints = new List<Vector3> { standPos };
                    if (currentPoints != null && currentPoints.Count > 0)
                    {
                        // Keep a few existing points after the stand — don't trap deer permanently
                        int keepCount = Math.Min(currentPoints.Count, 3);
                        for (int i = 0; i < keepCount; i++)
                            newPoints.Add(currentPoints[i]);
                    }

                    if (setWanderPts != null)
                        setWanderPts.Invoke(comp, new object[] { newPoints });
                    else if (wanderField != null)
                        wanderField.SetValue(comp, newPoints); // Direct field fallback

                    redirected++;
                }

                if (redirected > 0)
                    MelonLogger.Msg(
                        $"[WotW] DeerStand at {standPos}: redirected {redirected} deer " +
                        $"(bonus={bonus:F2})");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyAttractionToDeer: {ex.Message}");
            }
        }

        // ── Hunter pre-positioning ────────────────────────────────────────────
        /// <summary>
        /// For each active Hunting Stand, finds the nearest HuntingLodge hunter
        /// and injects the stand position into their wander points list.
        ///
        /// This pre-positions hunters AT the stand before they engage an animal,
        /// so when combat starts they're already at the controlled engagement zone
        /// rather than wandering deep into the wilderness.
        ///
        /// Works the same way as deer attraction — uses SetWanderPoints on the
        /// hunter's assigned villager. Requires the villager/worker wander points
        /// field to be on the same class as deer (PassiveAnimal pattern), which
        /// needs confirming from the combat dump.
        ///
        /// Called from AttractionWatcher on the same 8-second tick as deer attraction.
        /// </summary>
        public static void TickHunterPositioning()
        {
            if (!WardenOfTheWildsMod.DeerStandsEnabled.Value) return;
            if (!WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value) return;

            foreach (var stand in ActiveStands.Values)
            {
                if (!stand.HasHunterAssigned) continue;
                PositionHunterAtStand(stand);
            }
        }

        private static void PositionHunterAtStand(DeerStand stand)
        {
            try
            {
                // Find the HuntingLodge at the assigned cabin position
                Type? hunterBuildingType = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    hunterBuildingType = asm.GetType("HunterBuilding");
                    if (hunterBuildingType != null) break;
                }
                if (hunterBuildingType == null) return;

                // Find the exact cabin
                Component? cabin = null;
                float bestDist = 5f; // Must be within 5u of the assigned position
                foreach (UnityEngine.Object obj in
                    UnityEngine.Object.FindObjectsOfType(hunterBuildingType))
                {
                    var c = obj as Component;
                    if (c == null) continue;
                    float d = Vector3.Distance(c.transform.position, stand.AssignedHunterCabinPos);
                    if (d < bestDist) { bestDist = d; cabin = c; }
                }
                if (cabin == null) return;

                // Only act on HuntingLodge path
                var enhancement = cabin.GetComponent<WardenOfTheWilds.Components.HunterCabinEnhancement>();
                if (enhancement?.Path != WardenOfTheWilds.Components.HunterT2Path.HuntingLodge) return;

                // Find the assigned worker/villager for this cabin.
                // Candidates (to confirm from decompile):
                //   HunterBuilding.GetAssignedWorker() / assignedWorker / workers[0]
                //   Building.GetWorkerList() / workerList / assignedVillagers
                var workerObj = GetAssignedWorker(cabin);
                if (workerObj == null) return;

                // Inject stand position into the worker's wander points
                // Uses the same mechanism as ApplyAttractionToDeer — confirmed for PassiveAnimal.
                // Workers likely use a different state machine; this may need adjusting
                // once the villager wander/patrol system is confirmed from the combat dump.
                InjectWanderPoint(workerObj, stand.Position);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] PositionHunterAtStand: {ex.Message}");
            }
        }

        private static Component? GetAssignedWorker(Component building)
        {
            try
            {
                var type = building.GetType();

                // Try common patterns for getting the assigned worker from a building
                string[] methodCandidates = {
                    "GetAssignedWorker", "GetWorker", "GetFirstWorker",
                    "GetAssignedVillager", "GetVillager",
                };
                foreach (string name in methodCandidates)
                {
                    var m = type.GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m == null) continue;
                    object? result = m.Invoke(building, null);
                    if (result is Component c) return c;
                }

                // Try common list/array field patterns
                string[] fieldCandidates = {
                    "assignedWorkers", "workers", "assignedVillagers", "villagers",
                    "workerList", "assignedWorker",
                };
                foreach (string name in fieldCandidates)
                {
                    var f = type.GetField(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f == null) continue;
                    object? val = f.GetValue(building);
                    if (val is System.Collections.IList list && list.Count > 0)
                        return list[0] as Component;
                    if (val is Component c) return c;
                }
            }
            catch { }
            return null;
        }

        private static void InjectWanderPoint(Component worker, Vector3 standPos)
        {
            try
            {
                // Try the same wander points approach used for deer (PassiveAnimal pattern).
                // If workers use a different system, this will silently fail and we'll
                // update after the combat dump.
                System.Type? checkType = worker.GetType();
                System.Reflection.MethodInfo? setWander = null;
                System.Reflection.FieldInfo? wanderField = null;

                while (checkType != null)
                {
                    setWander   ??= checkType.GetMethod("SetWanderPoints",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    wanderField ??= checkType.GetField("wanderPoints",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (setWander != null && wanderField != null) break;
                    checkType = checkType.BaseType;
                }

                if (wanderField == null && setWander == null) return;

                var current = wanderField?.GetValue(worker) as System.Collections.Generic.List<Vector3>;
                var newPoints = new System.Collections.Generic.List<Vector3> { standPos };
                if (current != null)
                {
                    int keep = System.Math.Min(current.Count, 4);
                    for (int i = 0; i < keep; i++) newPoints.Add(current[i]);
                }

                if (setWander != null)
                    setWander.Invoke(worker, new object[] { newPoints });
                else
                    wanderField!.SetValue(worker, newPoints);
            }
            catch { }
        }

        // ── Attraction watcher coroutine ──────────────────────────────────────
        // Started from Plugin.OnSceneWasLoaded via LateInit
        public static IEnumerator AttractionWatcher()
        {
            yield return new WaitForSeconds(15f); // Let scene settle

            while (true)
            {
                yield return new WaitForSeconds(8f); // Tick every 8 seconds
                if (ActiveStands.Count > 0)
                {
                    TickAllStands();
                    TickHunterPositioning(); // Pre-position HuntingLodge hunters at stands
                }
            }
        }
    }
}
