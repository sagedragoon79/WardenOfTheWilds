using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Foxes die without dropping a carcass in vanilla DLC — the Fox prefab's
    /// <c>LandAnimalResource.items</c> list is empty, so
    /// <c>AggressiveAnimal.OnCombatDeath → resource.GenerateProducts</c>
    /// instantiates nothing. Hunters can kill foxes (after the team-mask
    /// patch) but walk back empty-handed.
    ///
    /// This postfix on <c>AggressiveAnimal.OnCombatDeath</c> spawns a
    /// <c>WolfCarcassResource</c> at the fox's position when the dead
    /// AggressiveAnimal is a Fox carrying our <see cref="WotWWildFoxMarker"/>.
    /// Vanilla raider foxes (no marker) flow through unchanged.
    ///
    /// Why WolfCarcass instead of SmallCarcass:
    ///   SmallCarcass in vanilla exists ONLY as trap output — its pickup
    ///   pipeline assumes the item lives inside a trap's storage. The
    ///   free-standing SmallCarcassResource prefab variant
    ///   (Resource_Villager_SmallCarcass01A) only spawns when a villager
    ///   dies while carrying SmallCarcass — different bucket subscription,
    ///   doesn't register with hunters' SmallCarcassToCollect via the
    ///   normal cabin-bound flow.
    ///   WolfCarcass uses the standard "hunter killed an aggressive animal"
    ///   pipeline — proven to work because hunters routinely kill wolves
    ///   and bring back carcasses. Foxes are small predators like wolves,
    ///   so lore-wise this maps cleanly. Player sees a wolf-sized carcass
    ///   model but the loot pipeline functions.
    /// </summary>
    internal static class FoxCarcassDropPatch
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type _foxType;
        private static GameObject _carcassPrefab;
        private static bool _prefabResolutionFailed;

        // v1.0.14 — Switched from SmallCarcass to WolfCarcass for the
        // reasons in the class comment. Item key for itemDict lookup is
        // the canonical Item class name (matches `_name` field set by the
        // Item subclass ctor). Both must agree.
        private const string CarcassClassName = "WolfCarcassResource";
        private const string CarcassItemKey   = "ItemWolfCarcass";

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type aggType = AccessTools.TypeByName("AggressiveAnimal");
                if (aggType == null)
                {
                    MelonLogger.Warning(
                        "[WotW] FoxCarcassDropPatch: AggressiveAnimal type not found.");
                    return;
                }

                var onDeath = AccessTools.Method(aggType, "OnCombatDeath");
                if (onDeath == null)
                {
                    MelonLogger.Warning(
                        "[WotW] FoxCarcassDropPatch: OnCombatDeath not found on AggressiveAnimal.");
                    return;
                }

                harmony.Patch(onDeath,
                    postfix: new HarmonyMethod(typeof(FoxCarcassDropPatch),
                                               nameof(OnCombatDeathPostfix)));

                _foxType = AccessTools.TypeByName("Fox");
                MelonLogger.Msg(
                    $"[WotW] FoxCarcassDropPatch: patched AggressiveAnimal.OnCombatDeath " +
                    $"(Fox type resolved: {_foxType != null}).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] FoxCarcassDropPatch.Register: {ex.Message}");
            }
        }

        public static void OnCombatDeathPostfix(object __instance, GameObject damageCauser)
        {
            try
            {
                if (__instance == null || _foxType == null) return;

                // Only fire for Fox instances
                if (!_foxType.IsInstanceOfType(__instance)) return;

                var foxComp = __instance as Component;
                if (foxComp == null) return;

                // v1.0.14 — Drop carcass for ANY fox killed in combat, marked
                // or not. Rationale:
                //   1. The WotWWildFoxMarker doesn't persist across save/load,
                //      so a marked-only gate misses foxes from prior sessions
                //      and any whose marker was lost.
                //   2. OnCombatDeath only fires when a fox is actually KILLED.
                //      Chicken-raid foxes that flee + despawn never trigger
                //      this, so we're not changing raider balance — we only
                //      affect foxes a hunter/dog brought down. A dead fox
                //      leaving a pelt is correct regardless of why it spawned.
                // DLC-gate still applies via PetsDlcActive (foxes only exist
                // with the DLC), so non-DLC saves never reach here.
                if (!Systems.DlcDetection.PestGameplayActive) return;

                Vector3 deathPos = foxComp.transform.position;

                if (!ResolveCarcassPrefab()) return;
                if (_carcassPrefab == null) return;

                SpawnSmallCarcassAt(deathPos);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] FoxCarcassDropPatch.Postfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves the carcass-resource prefab from the game's asset registry,
        /// using the same lookup vanilla <c>LandAnimalResource.GenerateProducts</c>
        /// uses when wolves die:
        /// <code>
        ///   Item.itemDict[CarcassItemKey].loadedPrefab
        /// </code>
        /// <c>itemDict</c> is populated by <c>ResourceManager.Awake</c>
        /// (each Item subclass ctor registers itself), so the prefab is
        /// available from the moment WotW's patches run on a fresh load —
        /// no need to wait for a wolf kill.
        ///
        /// Same pattern Tended Wilds uses for forageable templates via
        /// <c>GlobalAssets.buildingSetupData.GetBuildingData(...)</c>: pull
        /// the canonical prefab asset directly from the registry instead of
        /// cloning a scene instance.
        /// </summary>
        private static bool ResolveCarcassPrefab()
        {
            if (_carcassPrefab != null) return true;
            if (_prefabResolutionFailed) return false;

            try
            {
                var itemType = AccessTools.TypeByName("Item");
                var dictProp = itemType?.GetProperty("itemDict",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var dict = dictProp?.GetValue(null, null)
                    as System.Collections.IDictionary;
                if (dict == null)
                {
                    _prefabResolutionFailed = true;
                    MelonLogger.Warning(
                        "[WotW] FoxCarcassDropPatch: Item.itemDict property not accessible.");
                    return false;
                }

                if (!dict.Contains(CarcassItemKey))
                {
                    _prefabResolutionFailed = true;
                    MelonLogger.Warning(
                        $"[WotW] FoxCarcassDropPatch: '{CarcassItemKey}' not in " +
                        $"Item.itemDict ({dict.Count} entries).");
                    return false;
                }

                object itemData = dict[CarcassItemKey];
                var loadedPrefabProp = itemData.GetType().GetProperty(
                    "loadedPrefab", AllInstance);
                _carcassPrefab = loadedPrefabProp?.GetValue(itemData) as GameObject;

                if (_carcassPrefab == null)
                {
                    _prefabResolutionFailed = true;
                    MelonLogger.Warning(
                        $"[WotW] FoxCarcassDropPatch: ItemData.loadedPrefab is null " +
                        $"for {CarcassItemKey}.");
                    return false;
                }

                MelonLogger.Msg(
                    $"[WotW] FoxCarcassDropPatch: {CarcassClassName} prefab resolved " +
                    $"via Item.itemDict['{CarcassItemKey}'].loadedPrefab " +
                    $"('{_carcassPrefab.name}').");
                return true;
            }
            catch (Exception ex)
            {
                _prefabResolutionFailed = true;
                MelonLogger.Warning(
                    $"[WotW] FoxCarcassDropPatch.ResolveCarcassPrefab: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Instantiates a SmallCarcass at <paramref name="pos"/>, places it
        /// on the terrain, and registers it as an available pickup for
        /// hunters/laborers. Mirrors the post-instantiation setup from
        /// vanilla <c>LandAnimalResource.GenerateProducts</c>.
        /// </summary>
        private static void SpawnSmallCarcassAt(Vector3 pos)
        {
            try
            {
                GameObject go = UnityEngine.Object.Instantiate(_carcassPrefab);
                go.transform.position = pos;

                // Set quantity to 1 (one small carcass) — mirrors vanilla
                // call: component.SetQuantitesByIndex(0, (uint)GetInitialItemQtyByIndex(i))
                Type goResourceType = AccessTools.TypeByName("Resource");
                Component resource = go.GetComponent(goResourceType);
                if (resource != null)
                {
                    var setQty = resource.GetType().GetMethod("SetQuantitesByIndex",
                        new[] { typeof(int), typeof(uint) });
                    setQty?.Invoke(resource, new object[] { 0, 1u });

                    // priority = Priority.Elevated (vanilla)
                    var priorityProp = resource.GetType().GetProperty("priority", AllInstance);
                    if (priorityProp != null && priorityProp.PropertyType.IsEnum)
                    {
                        try
                        {
                            object elevated = Enum.Parse(priorityProp.PropertyType, "Elevated");
                            priorityProp.SetValue(resource, elevated);
                        }
                        catch { /* enum lookup failed — leave default */ }
                    }

                    var availableProp = resource.GetType().GetProperty("available", AllInstance);
                    availableProp?.SetValue(resource, true);
                }

                // Place on terrain so it sits at correct ground height
                var gm = UnitySingleton<GameManager>.Instance;
                var tm = gm?.terrainManager;
                if (tm != null)
                {
                    var place = tm.GetType().GetMethod("PlaceObjectOnTerrain",
                        new[] { typeof(GameObject) });
                    place?.Invoke(tm, new object[] { go });
                }

                // v1.0.14 (take 3) — assign a hunter cabin as residence.
                AssignNearestHunterCabin(go, pos);

                // v1.0.14 (take 4) — Diagnostic + explicit work-bucket
                // registration. The vanilla path is:
                //   Start() → PerformFirstWorkAvailabilityCheck() →
                //   CheckWorkAvailability() → AddToWorkBucket(...) IF
                //   availableForWork && storage has unreserved items.
                //
                // If either gate fails (storage empty, availableForWork
                // false), the carcass exists but never enters the
                // SmallCarcassToCollect bucket. Log the storage state and
                // try an explicit re-check to be defensive.
                ForceWorkBucketRegistration(go);

                MelonLogger.Msg(
                    $"[WotW] Fox carcass spawned at {pos:F1} — hunter can collect.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] FoxCarcassDropPatch.SpawnSmallCarcassAt: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the nearest HunterBuilding (within
        /// <c>MaxCabinAssignmentDistance</c> units) and sets it as the
        /// carcass's <c>huntersResidence</c>. The setter side-effect
        /// registers the carcass with that cabin's SmallCarcassToCollect
        /// work bucket so a hunter from that cabin will walk over to
        /// collect it.
        /// </summary>
        private const float MaxCabinAssignmentDistance = 300f;

        private static void AssignNearestHunterCabin(GameObject carcassGo, Vector3 carcassPos)
        {
            try
            {
                Type hbType = AccessTools.TypeByName("HunterBuilding");
                if (hbType == null) return;

                // Use the resourceManager's hunterBuildingsRO list for an
                // efficient lookup (avoids FindObjectsOfType every kill).
                var rm = UnitySingleton<GameManager>.Instance?.resourceManager;
                if (rm == null) return;

                var listProp = rm.GetType().GetProperty("hunterBuildingsRO",
                    BindingFlags.Public | BindingFlags.Instance);
                var hunterBuildings = listProp?.GetValue(rm)
                    as System.Collections.IEnumerable;
                if (hunterBuildings == null) return;

                Component nearest = null;
                float bestSqr = MaxCabinAssignmentDistance * MaxCabinAssignmentDistance;
                foreach (var hb in hunterBuildings)
                {
                    var comp = hb as Component;
                    if (comp == null) continue;
                    float sqr = (comp.transform.position - carcassPos).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        nearest = comp;
                    }
                }

                if (nearest == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] FoxCarcassDropPatch: no HunterBuilding within " +
                        $"{MaxCabinAssignmentDistance}u of fox kill at {carcassPos:F0} — " +
                        "carcass will rot uncollected. Place a hunter cabin closer.");
                    return;
                }

                // Set huntersResidence on the carcass — the property setter
                // triggers OnHuntersResidenceChanged which adds the carcass
                // to that cabin's SmallCarcassToCollect work bucket.
                Type carcassBaseType = AccessTools.TypeByName("CarcassResourceBase");
                Component carcassComp = carcassBaseType != null
                    ? carcassGo.GetComponent(carcassBaseType)
                    : null;
                if (carcassComp == null) return;

                var residenceProp = carcassBaseType.GetProperty(
                    "huntersResidence", AllInstance);
                residenceProp?.SetValue(carcassComp, nearest);

                MelonLogger.Msg(
                    $"[WotW] FoxCarcassDropPatch: assigned to cabin " +
                    $"'{nearest.gameObject.name}' ({Mathf.Sqrt(bestSqr):F0}u away).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] FoxCarcassDropPatch.AssignNearestHunterCabin: {ex.Message}");
            }
        }

        /// <summary>
        /// Invokes CarcassResourceBase.CheckWorkAvailability (protected, via
        /// reflection) to force the carcass to register with its cabin's
        /// SmallCarcass/WolfCarcass collect bucket immediately rather than
        /// waiting for the next vanilla evaluation tick.
        /// </summary>
        private static void ForceWorkBucketRegistration(GameObject carcassGo)
        {
            try
            {
                Type carcassBaseType = AccessTools.TypeByName("CarcassResourceBase");
                Component carcassComp = carcassBaseType != null
                    ? carcassGo.GetComponent(carcassBaseType)
                    : null;
                if (carcassComp == null) return;

                var checkMethod = carcassBaseType.GetMethod(
                    "CheckWorkAvailability", AllInstance, null, Type.EmptyTypes, null);
                if (checkMethod != null)
                {
                    checkMethod.Invoke(carcassComp, null);
                }
                else
                {
                    var perform = carcassBaseType.GetMethod(
                        "PerformFirstWorkAvailabilityCheck", AllInstance, null, Type.EmptyTypes, null);
                    perform?.Invoke(carcassComp, null);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] FoxCarcassDropPatch.ForceWorkBucketRegistration: {ex.Message}");
            }
        }
    }
}
