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
//                  • Access to bear/boar hunting
//
//  (Deer Stand placement was an early design idea but was dropped to keep
//   scope tight; the static-attractor mechanic added complexity without
//   meaningful upside.)
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Components
{
    public enum HunterT2Path
    {
        Vanilla,       // T1 or T2 not yet specialised
        TrapperLodge,  // Pelt/trap focus
        HuntingLodge,  // Meat/range focus
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
                HandlePathChanged();
            }
        }

        // ── Shared ScriptableObject state (ManufactureDefinition is a shared
        //    asset across all HunterBuilding instances, so zeroing its iron
        //    cost once applies to every cabin). Reset per map so a new save
        //    with different mod settings still applies cleanly.
        private static bool _trapRecipeZeroed = false;
        // Output caps live on the same shared ManufactureDefinition SOs as the
        // iron-zeroing — apply once per scene load, propagates to every cabin.
        private static bool _outputCapsBumped = false;

        // ── Scene reset (called from Plugin.OnSceneWasLoaded) ─────────────────
        public static void OnMapLoaded()
        {
            SavedPaths.Clear();
            _trapRecipeZeroed = false;
            _outputCapsBumped = false;
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

            // Zero out iron cost on the trap-production recipe so Trap Master
            // doesn't drain the iron economy. ManufactureDefinition is a shared
            // ScriptableObject, so the first cabin to run this applies the
            // change to every cabin — subsequent invocations short-circuit.
            if (!_trapRecipeZeroed)
            {
                _trapRecipeZeroed = true;
                ZeroTrapRecipeCosts();
            }

            // Bump produced-item output caps (Meat / Hide / Tallow → 200).
            // Same one-shot pattern; ScriptableObject is shared globally.
            // Addresses Trapper carcass-queue stalls when output buffer fills
            // before laborers haul to a Storehouse (reported May 2026).
            if (!_outputCapsBumped)
            {
                _outputCapsBumped = true;
                BumpHunterOutputCaps();
            }

            // ── Migrate stuck bear-bonus meat/hide/tallow from saves <= v1.0.7 ──
            //
            // v1.0.0 - v1.0.7 had a bug: BearBonusYieldPatches and
            // TrapMasterBearPatches added bonus items into cabin.manufacturingStorage
            // (the carcass INPUT pool) instead of cabin.storage (the OUTPUT pool).
            // Vanilla's HasItemMeat work bucket queries only base.storage, so
            // those items were invisible to smokehouses, wagons, and laborers.
            // Reports of 1000+ meat stuck in long-running Trap Master cabins.
            //
            // v1.0.8 fixes the bear-bonus patches to target cabin.storage. This
            // migration handles the LEGACY stuck items from prior versions:
            // walk manufacturingStorage, transfer Meat/Hide/Tallow to
            // base.storage. Runs once per cabin per session — items moved are
            // immediately visible to logistics on the next CheckWorkAvailability
            // tick.
            DrainStuckBearBonusItems();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] HunterCabinEnhancement attached to '{gameObject.name}' " +
                $"(key={GetBuildingKey()}, path={_path})");

            // Trapper meat-stuck diagnostic. Gated by DiagnosticsEnabled (default
            // OFF — verbose log dump for investigating community-reported issue
            // where Trapper Lodges accumulate meat past the 200 cap and become
            // invisible to nearby smokehouses / wagons. The probe samples both
            // storage pools and unreserved counts so we can see whether meat is:
            //   (A) sitting in base.storage but reserved by stuck tasks
            //   (B) routed to manufacturingStorage instead of base.storage
            //   (C) properly in base.storage but the cabin failed to register
            //       in the HasItemMeat work bucket
            if (WardenOfTheWildsMod.DiagnosticsEnabled.Value)
                StartCoroutine(TrapperMeatDiagnosticLoop());
        }

        /// <summary>
        /// Walks the hunter building's ManufactureDefinitions. For any recipe
        /// whose source items include ItemIron (or whose produced item is
        /// ItemAnimalTrap), sets those source-item quantities to 0 — making
        /// traps free to produce/maintain.
        /// </summary>
        /// <summary>
        /// Zeros out the ItemIron cost on the "MakingAnimalTraps" recipe so
        /// Trap Master doesn't drain the iron economy. Field names confirmed
        /// via RECIPE-DIAG dump: ManufactureDefinition.sourceItems holds a
        /// list of SourceItemDefinition. SourceItemDefinition has a BASE
        /// class ItemDefinition with field `itemName` (string) and `_item`
        /// (Item), and the quantity lives on SourceItemDefinition itself as
        /// `_numSourceItemsNeeded` (int).
        /// </summary>
        private void ZeroTrapRecipeCosts()
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;

                var manuField = FindBackingField(building.GetType(), "manufactureDefinitions");
                if (manuField == null) return;

                var manuList = manuField.GetValue(building) as System.Collections.IList;
                if (manuList == null || manuList.Count == 0) return;

                foreach (var manuDef in manuList)
                {
                    if (manuDef == null) continue;

                    var sourceItemsField = manuDef.GetType().GetField("sourceItems", AllInstance);
                    var sourceList = sourceItemsField?.GetValue(manuDef) as System.Collections.IList;
                    if (sourceList == null) continue;

                    foreach (var srcDef in sourceList)
                    {
                        if (srcDef == null) continue;

                        // itemName lives on the ItemDefinition base class — walk up
                        string itemName = GetInheritedString(srcDef, "itemName");
                        if (itemName != "ItemIron") continue;

                        // _numSourceItemsNeeded is declared on SourceItemDefinition
                        var qtyField = GetInheritedField(srcDef.GetType(), "_numSourceItemsNeeded");
                        if (qtyField == null) continue;

                        int oldQty = (int)qtyField.GetValue(srcDef);
                        if (oldQty == 0) continue;
                        qtyField.SetValue(srcDef, 0);

                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' zeroed trap recipe cost: " +
                            $"ItemIron {oldQty} → 0");
                    }
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] ZeroTrapRecipeCosts failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks the hunter building's ManufactureDefinitions and bumps the
        /// `capacity` field on each producedItems entry whose item is one of
        /// Meat / Hide / Tallow (or their carcass-specific variants). Same
        /// pattern as SmokehouseEnhancement.ApplyStorageCaps — capacity lives
        /// on ItemDefinition (base class for ProducedItemDefinition), so we
        /// walk inheritance to find it.
        ///
        /// Why: vanilla cap is 100 per output. With Trapper Lodge running 3
        /// traps at ~10-day interval, Meat output fills to 100 and butchering
        /// stalls if laborers can't haul fast enough → carcass queue backs up
        /// (reported by Smokey, May 2026).
        ///
        /// Scope: applies to every recipe that outputs Meat/Hide/Tallow, not
        /// just Trapper. Hunting Lodges benefit too. Other recipes (e.g. trap
        /// crafting → ItemAnimalTrap) are untouched.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> HunterOutputItems
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "ItemMeat", "ItemHide", "ItemTallow" };

        private void BumpHunterOutputCaps()
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;

                int target = System.Math.Max(1, WardenOfTheWildsMod.HunterCabinOutputStorageCap.Value);

                var manuField = FindBackingField(building.GetType(), "manufactureDefinitions");
                var manuList = manuField?.GetValue(building) as System.Collections.IList;
                if (manuList == null || manuList.Count == 0) return;

                int bumped = 0;
                foreach (var manuDef in manuList)
                {
                    if (manuDef == null) continue;

                    var prodField = manuDef.GetType().GetField("producedItems", AllInstance);
                    var prodList = prodField?.GetValue(manuDef) as System.Collections.IList;
                    if (prodList == null) continue;

                    foreach (var prod in prodList)
                    {
                        if (prod == null) continue;
                        string itemName = GetInheritedString(prod, "itemName");
                        if (!HunterOutputItems.Contains(itemName)) continue;

                        var capField = GetInheritedField(prod.GetType(), "capacity");
                        if (capField == null || capField.FieldType != typeof(int)) continue;

                        int current = (int)capField.GetValue(prod);
                        if (current == target) continue;

                        capField.SetValue(prod, target);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] Hunter output cap ({itemName}): {current} → {target}");
                        bumped++;
                    }
                }

                if (bumped == 0)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        "[WotW] BumpHunterOutputCaps: no recipes matched (already at target?).");
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] BumpHunterOutputCaps failed: {ex.Message}");
            }
        }

        /// <summary>Walks inheritance chain to find a string field by name.</summary>
        private static string GetInheritedString(object obj, string fieldName)
        {
            if (obj == null) return "";
            var f = GetInheritedField(obj.GetType(), fieldName);
            if (f == null) return "";
            try { return f.GetValue(obj) as string ?? ""; }
            catch { return ""; }
        }

        /// <summary>Walks inheritance chain to find a field by name.</summary>
        private static FieldInfo? GetInheritedField(System.Type type, string fieldName)
        {
            while (type != null && type != typeof(object))
            {
                var f = type.GetField(fieldName, AllInstance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                type = type.BaseType;
            }
            return null;
        }

        // NOTE: DumpRecipeDataCandidates + DumpManufactureDefinition were
        // one-shot diagnostics used to reverse-engineer the recipe field
        // layout. Kept for future reference / easy re-enable if the schema
        // ever changes; they're no longer called by ZeroTrapRecipeCosts.
        private void DumpRecipeDataCandidates(Building building)
        {
            try
            {
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW][RECIPE-DIAG] building='{building.gameObject.name}' type={building.GetType().Name}");

                // Dump building-level fields that look recipe-ish
                WardenOfTheWildsMod.Log.Msg($"[WotW][RECIPE-DIAG] --- building fields ---");
                DumpFieldsMatching(building, new[] {
                    "recipe", "produce", "material", "required", "source",
                    "ingredient", "cost", "input", "output", "resource", "manu"
                });

                // BuildingData
                var bdField = FindBackingField(building.GetType(), "buildingData")
                            ?? building.GetType().GetField("buildingData", AllInstance);
                var bd = bdField?.GetValue(building);
                if (bd != null)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW][RECIPE-DIAG] --- BuildingData fields (type={bd.GetType().Name}) ---");
                    DumpFieldsMatching(bd, new[] {
                        "recipe", "produce", "material", "required", "source",
                        "ingredient", "cost", "input", "output", "resource", "manu"
                    });
                }

                // ResourcesRecord (via resourceRecord backing field)
                var rrField = FindBackingField(building.GetType(), "resourceRecord");
                var rr = rrField?.GetValue(building);
                if (rr != null)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW][RECIPE-DIAG] --- ResourcesRecord fields (type={rr.GetType().Name}) ---");
                    DumpFieldsMatching(rr, new[] {
                        "recipe", "produce", "material", "required", "source",
                        "ingredient", "cost", "input", "output"
                    });
                }
                else
                {
                    WardenOfTheWildsMod.Log.Msg(
                        "[WotW][RECIPE-DIAG] ResourcesRecord is null on this building.");
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW][RECIPE-DIAG] failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps one ManufactureDefinition: its states (identifies the recipe),
        /// and every source-item entry with its item.name and quantity. This
        /// pinpoints which recipe holds the iron cost AND gives us the exact
        /// field names on SourceItemDefinition.
        /// </summary>
        private void DumpManufactureDefinition(object md, int idx)
        {
            try
            {
                var mdType = md.GetType();

                // Recipe identity — gatheringState + manufacturingState
                string gs = GetFieldStr(md, mdType, "gatheringState");
                string ms = GetFieldStr(md, mdType, "manufacturingState");
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW][RECIPE-DIAG] Recipe[{idx}] gathering={gs} manufacturing={ms}");

                // sourceItems list
                var siField = mdType.GetField("sourceItems", AllInstance);
                if (siField == null) { WardenOfTheWildsMod.Log.Warning($"[WotW][RECIPE-DIAG]   no sourceItems field"); return; }
                var siList = siField.GetValue(md) as System.Collections.IList;
                if (siList == null) { WardenOfTheWildsMod.Log.Msg($"[WotW][RECIPE-DIAG]   sourceItems null"); return; }

                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW][RECIPE-DIAG]   sourceItems count = {siList.Count}");

                for (int j = 0; j < siList.Count; j++)
                {
                    var src = siList[j];
                    if (src == null) continue;
                    var srcType = src.GetType();

                    // Dump every DeclaredOnly field on this SourceItemDefinition
                    // so we see the exact field names (item, quantity, etc.)
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW][RECIPE-DIAG]   src[{j}] type={srcType.Name}");
                    var walkType = srcType;
                    while (walkType != null && walkType != typeof(object)
                        && walkType != typeof(UnityEngine.Object))
                    {
                        foreach (var f in walkType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                        {
                            try
                            {
                                var val = f.GetValue(src);
                                string valStr;
                                if (val == null) valStr = "null";
                                else if (val is UnityEngine.Object uo) valStr = $"{uo.name} [{uo.GetType().Name}]";
                                else valStr = val.ToString() ?? "<?>";
                                WardenOfTheWildsMod.Log.Msg(
                                    $"[WotW][RECIPE-DIAG]     [{walkType.Name}] {f.Name} ({f.FieldType.Name}) = {valStr}");
                            }
                            catch { }
                        }
                        walkType = walkType.BaseType;
                    }
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW][RECIPE-DIAG] DumpManufactureDefinition failed: {ex.Message}");
            }
        }

        private string GetFieldStr(object obj, System.Type type, string name)
        {
            var f = type.GetField(name, AllInstance);
            if (f == null)
            {
                var baseT = type.BaseType;
                while (baseT != null && f == null)
                {
                    f = baseT.GetField(name, AllInstance);
                    baseT = baseT.BaseType;
                }
            }
            if (f == null) return "<missing>";
            try
            {
                var v = f.GetValue(obj);
                return v?.ToString() ?? "null";
            }
            catch { return "<error>"; }
        }

        private void DumpFieldsMatching(object obj, string[] keywords)
        {
            if (obj == null) return;
            var type = obj.GetType();
            int printed = 0;
            while (type != null && type != typeof(UnityEngine.Object) && type != typeof(object))
            {
                foreach (var f in type.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                {
                    string n = f.Name.ToLowerInvariant();
                    bool match = false;
                    foreach (var kw in keywords)
                    {
                        if (n.Contains(kw.ToLowerInvariant())) { match = true; break; }
                    }
                    if (!match) continue;

                    try
                    {
                        var val = f.GetValue(obj);
                        string valStr;
                        if (val == null) valStr = "null";
                        else if (val is System.Collections.IList list) valStr = $"[{list.Count} items]";
                        else if (val is System.Collections.IDictionary dict) valStr = $"{{{dict.Count} entries}}";
                        else valStr = val.ToString() ?? "<?>";
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW][RECIPE-DIAG]   [{type.Name}] {f.Name} ({f.FieldType.Name}) = {valStr}");
                        printed++;
                        if (printed > 30) return;
                    }
                    catch { }
                }
                type = type.BaseType;
            }
        }

        private void RestoreSavedPath()
        {
            if (SavedPaths.TryGetValue(GetBuildingKey(), out HunterT2Path saved))
                _path = saved;
        }

        // ── Path application ──────────────────────────────────────────────────
        private void HandlePathChanged()
        {
            ApplyPath();
            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] Hunter '{gameObject.name}' path → {_path}");

            PostPathChangeNotification();

            // Fire the static event so the UI (HunterModeButtonPatches) can
            // refresh button highlights without polling.
            try { OnPathChanged?.Invoke(this); } catch { }
        }

        /// <summary>
        /// Fires whenever any hunter cabin's path changes via the Path setter.
        /// UI code subscribes to this to refresh its display without polling.
        /// </summary>
        public static event System.Action<HunterCabinEnhancement>? OnPathChanged;

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
                        "Pelts and furs are the focus now.",
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

            // ── Binary path selection ────────────────────────────────────────
            // T2 has two modes chosen via the mode buttons (HunterModeButtonPatches):
            //   HuntingLodge ("Big Game Hunter") — no traps, bow hunter, expanded
            //                                      radius, combat tuning.
            //   TrapperLodge ("Trap Master")     — traps at max, pelt focus,
            //                                      slower trap interval.
            // The old "Vanilla" middle path is gone — if _path is still set to
            // Vanilla from a legacy save, we treat it as HuntingLodge.
            //
            // Worker slots are PATH-DEPENDENT:
            //   HuntingLodge (T2) → 2 workers — parallel hunting + butchering
            //   TrapperLodge (T2) → 1 worker  — single trapper running lines
            //                                   (matches Trap Master flavour;
            //                                    second slot was wasted because
            //                                    the trap auto-cycle doesn't
            //                                    benefit from a 2nd worker)
            //   T1 / unspecialised → 1 worker (vanilla)
            //
            // Trap slider: handled by the mode switch (see SetTrapsEnabled /
            // SetTrapCount). For TrapperLodge we push to max, for HuntingLodge
            // we disable (0). Worker slots are set FIRST so the trap-count
            // logic works against the final worker count.

            // Normalise legacy Vanilla → HuntingLodge
            HunterT2Path effectivePath = _path == HunterT2Path.Vanilla
                ? HunterT2Path.HuntingLodge
                : _path;

            if (isT2)
            {
                int workerTarget = effectivePath == HunterT2Path.TrapperLodge ? 1 : 2;
                SetWorkerSlots(workerTarget);
            }
            else
            {
                SetWorkerSlots(1);
            }

            switch (effectivePath)
            {
                case HunterT2Path.TrapperLodge:
                    SetWorkRadius(1.0f);
                    SetWorkerSpeed(1.0f);
                    if (isT2)
                    {
                        SetTrapsEnabled(true);  // enable + restore count in one shot
                        SetTrapCountToMax();    // push slider to maxDeployedTraps
                        SetTrapSpawnInterval(CalculateTrapperPeltMult());
                    }
                    // Trap Master still has a work area (where traps are placed).
                    // Show the ring at current radius (1.0× vanilla) so the player
                    // can see + move it, same as Big Game Hunter.
                    UpdateWorkAreaCircle(GetCurrentWorkRadius());
                    break;

                case HunterT2Path.HuntingLodge:
                default:
                    SetWorkRadius(WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value);
                    SetWorkerSpeed(WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value);
                    if (isT2)
                    {
                        SetTrapsEnabled(false);      // explicit: no traps
                        SetTrapSpawnInterval(1.0f);  // baseline (traps off)
                        // Immediately reclaim any deployed traps back into the
                        // lodge's storage. Without this, the existing world
                        // traps stay orphaned — hunters from OTHER lodges can
                        // scan for "gatherable" traps and cross the map to
                        // collect them. CollectTrapsToStorage() is the vanilla
                        // method that cleans this up in one shot.
                        CollectExistingTraps();
                    }
                    UpdateWorkAreaCircle(GetCurrentWorkRadius());
                    LogKitingParameters();
                    break;
            }
        }

        /// <summary>
        /// Calls HunterBuilding.CollectTrapsToStorage() via reflection so any
        /// traps currently deployed in the world are immediately pulled back
        /// into this lodge's inventory. Call on mode switch to BGH to prevent
        /// orphaned world-traps from being cross-collected by other lodges'
        /// hunters scanning for nearby gatherable traps.
        /// </summary>
        private void CollectExistingTraps()
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;
                var method = building.GetType().GetMethod(
                    "CollectTrapsToStorage",
                    AllInstance,
                    null, System.Type.EmptyTypes, null);
                if (method != null)
                {
                    method.Invoke(building, null);
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' CollectTrapsToStorage() — " +
                        "reclaimed deployed traps on BGH switch");
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] CollectExistingTraps failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets userDefinedMaxDeployedTraps to the building's maxDeployedTraps
        /// (the slider's upper bound), so TrapperLodge runs with all traps
        /// deployed. Used when the slider is no longer the path selector.
        /// </summary>
        private void SetTrapCountToMax()
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;
                var hunter = building as HunterBuilding;
                if (hunter == null) return;

                int max = hunter.maxDeployedTraps;
                if (max <= 0) return;

                var setter = building.GetType().GetMethod(
                    "set_userDefinedMaxDeployedTraps", AllInstance);
                setter?.Invoke(building, new object[] { max });
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' userDefinedMaxDeployedTraps → {max} (max)");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetTrapCountToMax: {ex.Message}");
            }
        }

        // ── Trapper environment bonuses ───────────────────────────────────────
        // Pelt mult inputs:
        //   • TrapperLodgePeltMult / TrapMasterSpeedMult / TrapperWaterBonus — prefs (cheap, re-read every call)
        //   • Water tile count — expensive grid scan (~250 GetIsWater calls @ radius 60).
        //     Only changes on relocate or terrain edit — cache and skip when stable.
        //
        // Caching invariant: water-tile count is valid for (position, radius).
        // Vanilla's UpdateTrappingPoints postfix can fire many times per day-tick
        // across all lodges; previously each invocation re-scanned the terrain,
        // producing a visible freeze on maps with many hunter buildings.
        private bool  _trapperNearWater = false;
        private int   _cachedWaterTiles = 0;
        private float _cachedPeltMult   = 1.0f;
        private bool    _waterTilesScanned = false;
        private Vector3 _scannedAtPos      = Vector3.zero;
        private float   _scannedAtRadius   = 0f;
        private float   _lastLoggedMult    = float.NaN;
        private bool    _lastLoggedNearWater = false;

        /// <summary>
        /// Calculates the effective interval multiplier for TrapperLodge.
        /// Combines: base pelt mult × Trap Master speed mult × water bonus (if threshold met).
        /// Result is passed to SetTrapSpawnInterval as the divisor.
        /// </summary>
        private float CalculateTrapperPeltMult()
        {
            float mult = WardenOfTheWildsMod.TrapperLodgePeltMult.Value;

            // Trap Master speed boost — traps tick faster baseline
            mult *= WardenOfTheWildsMod.TrapMasterSpeedMult.Value;

            // Water tile counting — only re-scan if position or radius changed.
            // Position change covers relocate; radius change covers path switch.
            float radius = GetHuntingRadius();
            if (!_waterTilesScanned ||
                (transform.position - _scannedAtPos).sqrMagnitude > 0.25f ||
                Mathf.Abs(radius - _scannedAtRadius) > 0.5f)
            {
                _cachedWaterTiles = CountWaterTiles(transform.position, radius);
                _scannedAtPos = transform.position;
                _scannedAtRadius = radius;
                _waterTilesScanned = true;
            }

            _trapperNearWater = _cachedWaterTiles >=
                WardenOfTheWildsMod.TrapperWaterTileThreshold.Value;

            if (_trapperNearWater)
                mult *= WardenOfTheWildsMod.TrapperWaterBonus.Value;

            _cachedPeltMult = mult; // Cache for OnGUI — never recompute on the render thread

            // Log only on actual change — vanilla's UpdateTrappingPoints postfix
            // calls this frequently across every Trapper lodge.
            if (float.IsNaN(_lastLoggedMult)
                || Mathf.Abs(mult - _lastLoggedMult) > 0.01f
                || _trapperNearWater != _lastLoggedNearWater)
            {
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] Trapper '{gameObject.name}' effective mult: {mult:F2} " +
                    $"(pelt={WardenOfTheWildsMod.TrapperLodgePeltMult.Value:F1}, " +
                    $"speed={WardenOfTheWildsMod.TrapMasterSpeedMult.Value:F2}, " +
                    $"waterTiles={_cachedWaterTiles}/{WardenOfTheWildsMod.TrapperWaterTileThreshold.Value}, " +
                    $"waterBonus={_trapperNearWater})");
                _lastLoggedMult = mult;
                _lastLoggedNearWater = _trapperNearWater;
            }

            return mult;
        }

        /// <summary>
        /// Forces the water-tile cache to recompute on next CalculateTrapperPeltMult.
        /// Call from external systems that mutate the terrain inside this lodge's
        /// hunting radius (e.g. river carving, terrain editor) to keep the bonus
        /// gate accurate without waiting for a relocate or path switch.
        /// </summary>
        public void InvalidateWaterTileCache()
        {
            _waterTilesScanned = false;
        }

        /// <summary>
        /// Gets the cabin's hunting radius via reflection (used for water tile sampling).
        /// </summary>
        private float GetHuntingRadius()
        {
            try
            {
                var building = GetComponent<HunterBuilding>();
                if (building != null)
                {
                    var field = building.GetType().GetField("_huntingRadius", AllInstance);
                    if (field != null) return (float)field.GetValue(building);
                }
            }
            catch { }
            return 60f; // fallback
        }

        /// <summary>
        /// Counts water tiles within a radius by sampling the terrain on a grid.
        /// Uses TerrainManagerBase.GetIsWater(Vector2, width, height, gridSize, out bool).
        /// Hot — caller must cache the result. CalculateTrapperPeltMult only invokes
        /// this when the lodge moves or its hunting radius changes.
        /// </summary>
        private static int CountWaterTiles(Vector3 center, float radius)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm == null || gm.terrainManager == null) return 0;

                int waterCount = 0;
                float step = 8f; // 8u grid = ~4 tiles per step, good enough for counting

                for (float x = -radius; x <= radius; x += step)
                {
                    for (float z = -radius; z <= radius; z += step)
                    {
                        // Skip corners outside the circular radius
                        if (x * x + z * z > radius * radius) continue;

                        Vector2 samplePos = new Vector2(center.x + x, center.z + z);
                        bool isOcean;
                        if (gm.terrainManager.GetIsWater(samplePos, step, step, step, out isOcean))
                            waterCount++;
                    }
                }

                return waterCount;
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] CountWaterTiles: {ex.Message}");
            }
            return 0;
        }

        // ── Worker slot adjustment (Manifest Delivery pattern) ───────────────
        //
        // Save/load gotcha: vanilla serialises userDefinedMaxWorkers, then on
        // load clamps it to vanilla's (1-slot) maxWorkers BEFORE our mod runs.
        // We raise maxWorkers here, but if we only clamp userDefinedMaxWorkers
        // DOWN (never up), the 2nd slot stays off forever on reloaded saves.
        //
        // Fix: sync userDefinedMaxWorkers to targetMax in BOTH directions,
        // then call AttemptToAddMaxWorkers() so the game hires a villager
        // into the new slot immediately (mirrors what clicking the + button
        // does — same path we used in MD).
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

                // Sync userDefinedMaxWorkers in both directions
                if (building.userDefinedMaxWorkers != targetMax)
                {
                    int before = building.userDefinedMaxWorkers;
                    building.userDefinedMaxWorkers = targetMax;
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' userDefinedMaxWorkers: " +
                        $"{before} → {targetMax}");
                }

                // Trigger the hiring path (equivalent to clicking the + button).
                // Without this, the slot widens but stays empty until the player
                // manually clicks + — exactly the MD wagon shop bug.
                if (building.userDefinedMaxWorkers > 0)
                {
                    int currentWorkers = building.workersRO?.Count ?? 0;
                    if (currentWorkers < building.userDefinedMaxWorkers)
                    {
                        building.AttemptToAddMaxWorkers();
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' AttemptToAddMaxWorkers " +
                            $"({currentWorkers} → target {building.userDefinedMaxWorkers})");
                    }
                }
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
        // HuntingLodge hunters get a per-villager speed bump so they can kite
        // dangerous game. We set `villager.curOccupationalSpeedBonus` on each
        // hunter assigned to this shack.
        //
        // IMPORTANT: this used to mutate CombatManager._hunterMoveSpeedBonus, a
        // GLOBAL singleton used by vanilla's VillagerOccupationHunter.Init to set
        // the baseline `curOccupationalSpeedBonus` on every hunter. Multiple
        // shacks with different multipliers compounded the global value
        // (0.2 → 0.24 → 0.29 → 0.34 → ...), and all non-BGH hunters inherited
        // the boost too. Now we write per-villager.
        //
        // Stacking: `curOccupationalSpeedBonus` is ADDED in the final speed
        // calculation alongside `techOffroadSpeedBonus` (which Trailblazing
        // owns), so tech-tree Trailblazing still stacks on top of our BGH boost.
        private void SetWorkerSpeed(float multiplier)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var cm = gm?.combatManager;
                if (cm == null) return;

                // Vanilla baseline that VillagerOccupationHunter.Init assigns to
                // every hunter (default 0.2). BGH multiplies that; non-BGH paths
                // reset to baseline.
                float baseline = cm.hunterMoveSpeedBonus;
                float target   = baseline * (multiplier > 0f ? multiplier : 1f);

                var myBuilding = GetComponent<HunterBuilding>();
                if (myBuilding == null) return;

                int applied = 0;
                foreach (var villager in UnityEngine.Object.FindObjectsOfType<Villager>())
                {
                    if (villager == null) continue;
                    // Only villagers housed at THIS shack, with hunter occupation.
                    if (!(villager.residence is HunterBuilding hb) || hb != myBuilding)
                        continue;
                    if (!(villager.occupation is VillagerOccupationHunter))
                        continue;
                    villager.curOccupationalSpeedBonus = target;
                    applied++;
                }

                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' curOccupationalSpeedBonus → {target:F3} " +
                    $"(baseline {baseline:F3} × {multiplier:F2}) on {applied} hunter(s)");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetWorkerSpeed failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Public entry called when a worker is assigned to this shack (via the
        /// HunterBuilding.OnResidentAdded Harmony patch) — ensures newly-assigned
        /// hunters immediately inherit the BGH speed bonus without needing a
        /// path re-selection.
        /// </summary>
        public void RefreshSpeedBonusOnNewResident()
        {
            float mult = _path == HunterT2Path.HuntingLodge
                ? WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value
                : 1.0f;
            SetWorkerSpeed(mult);
        }

        /// <summary>
        /// Re-applies the speed bonus to a single villager. Called from the
        /// Harmony postfix on VillagerOccupationHunter.Init so that whenever
        /// vanilla resets the bonus to its baseline (0.2), we immediately put
        /// the BGH multiplier back. Without this hook, anything that triggers
        /// occupation re-init clobbers the BGH bonus mid-game.
        /// </summary>
        public void ApplySpeedBonusToVillager(Villager v)
        {
            if (v == null) return;
            if (!(v.residence is HunterBuilding hb)) return;
            if (hb != GetComponent<HunterBuilding>()) return;

            var gm = UnitySingleton<GameManager>.Instance;
            var cm = gm?.combatManager;
            if (cm == null) return;
            float baseline = cm.hunterMoveSpeedBonus;
            float mult = _path == HunterT2Path.HuntingLodge
                ? WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value
                : 1.0f;
            v.curOccupationalSpeedBonus = baseline * mult;
        }

        // ── Kiting parameter log ──────────────────────────────────────────────
        // Called on HuntingLodge path selection. Logs the computed kiting
        // parameters for each dangerous animal at current hunter stats so the
        // optimal retreat distances are visible in the MelonLoader log.
        private void LogKitingParameters()
        {
            // Read the current vanilla hunter baseline for kiting-distance log.
            // Prior code cached this from a per-shack global stomp; new speed
            // system is per-villager so we read live from CombatManager.
            float baselineBonus = 0.2f;
            try
            {
                var cm = UnitySingleton<GameManager>.Instance?.combatManager;
                if (cm != null) baselineBonus = cm.hunterMoveSpeedBonus;
            }
            catch { }
            float speed = WardenOfTheWilds.Systems.AnimalBehaviorSystem.KitingCalculator.FallbackHunterSpeed
                        * (1f + baselineBonus * WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value);

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

        // Last interval we wrote. Lets the post-UpdateTrappingPoints re-apply
        // detect that vanilla overwrote our value and restore the division.
        private int _lastWrittenInterval = -1;

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
                _lastWrittenInterval = newInterval;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' trappingCarcassSpawnInterval: " +
                    $"{_baselineSpawnInterval}d → {newInterval}d (×{multiplier:F1})");
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetTrapSpawnInterval failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from the Harmony postfix on HunterBuilding.UpdateTrappingPoints.
        /// Vanilla recalculates trappingCarcassSpawnInterval from biome/water/overlap
        /// on any work-area change, which clobbers our Trapper-path divider. We
        /// detect the overwrite (current != last-written) and re-divide the
        /// fresh vanilla value by the current pelt multiplier.
        /// </summary>
        public void ReapplyTrapSpawnIntervalAfterVanillaUpdate()
        {
            if (_path != HunterT2Path.TrapperLodge) return;
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;
                var type = building.GetType();

                var getter = type.GetMethod("get_trappingCarcassSpawnInterval", AllInstance);
                var setter = type.GetMethod("set_trappingCarcassSpawnInterval", AllInstance);
                if (getter == null || setter == null) return;

                object? raw = getter.Invoke(building, null);
                int currentInterval = raw is int i ? i : 0;
                if (currentInterval <= 0) return;

                // Vanilla didn't change anything — nothing to do.
                if (currentInterval == _lastWrittenInterval) return;

                float mult = CalculateTrapperPeltMult();
                if (mult <= 1f) return;

                int newInterval = (int)System.Math.Max(1,
                    System.Math.Round(currentInterval / (double)mult));
                if (newInterval == currentInterval) return;

                setter.Invoke(building, new object[] { newInterval });
                _lastWrittenInterval = newInterval;
                // Refresh cached baseline so any subsequent SetTrapSpawnInterval
                // computes from vanilla's new value, not the stale original.
                _baselineSpawnInterval = currentInterval;
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] ReapplyTrapSpawnInterval failed: {ex.Message}");
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

        private void Update()
        {
            // Cache the SelectableComponent ref — GetComponent on every frame
            // across many shacks adds up. Re-used across Update AND OnGUI.
            if (_cachedSelectable == null)
                _cachedSelectable = GetComponent<SelectableComponent>();
            bool selected = _cachedSelectable != null && _cachedSelectable.IsSelected;

            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                // Show the work-area ring on selection regardless of mode —
                // both Big Game Hunter and Trap Master have a meaningful work
                // area. Previously this was HuntingLodge-only, which caused
                // stale visibility state on mode switches.
                if (_workArea != null)
                    _workArea.SetEnabled(selected);
            }

            // Mode switching is now handled by HunterModeButtonPatches (Big Game
            // Hunter / Trap Master buttons on the building info window).
            // No per-frame slider poll, no P hotkey. If you need keyboard control
            // later, wire it as an event-based shortcut, not a per-frame Input check.
        }

        // OnGUI fires 4+ times per frame per MonoBehaviour. Per-frame
        // GetComponent<>() and Camera.main calls were adding up to thousands
        // of calls/sec across all hunter shacks. Cache everything.
        private SelectableComponent _cachedSelectable;
        private Building _cachedBuilding;
        private static Camera _cachedMainCamera;

        private void OnGUI()
        {
            // Fast path: resolve-and-cache selectable once, then gate everything else.
            if (_cachedSelectable == null)
                _cachedSelectable = GetComponent<SelectableComponent>();
            if (_cachedSelectable == null || !_cachedSelectable.IsSelected) return;

            // Cache main camera statically — it's the same across all shacks
            // and Camera.main's lookup is slow (iterates all Cameras).
            if (_cachedMainCamera == null)
                _cachedMainCamera = Camera.main;
            if (_cachedMainCamera == null) return;

            Vector3 screenPos = _cachedMainCamera.WorldToScreenPoint(
                transform.position + Vector3.up * 7f);
            if (screenPos.z <= 0) return;

            float y = Screen.height - screenPos.y;
            if (_cachedBuilding == null)
                _cachedBuilding = GetComponent<Building>();
            bool isT2 = _cachedBuilding != null && _cachedBuilding.tier >= 2;

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
                    $"Specialises: Small game pelts\n" +
                    $"{(_trapperNearWater ? "Water bonus" : "")}\n" +
                    $"[{keyName}] cycle path",
                HunterT2Path.HuntingLodge =>
                    $"Radius x{WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value:F1}  " +
                    $"2 workers  |  Traps: Off\n" +
                    $"Kiting: {(WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value ? "Active" : "Off")}\n" +
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

        // ── Migration: drain stuck bear-bonus items from manufacturingStorage ──
        //
        // For users coming from v1.0.0-1.0.7 with cabins that have accumulated
        // bear-bonus meat/hide/tallow in manufacturingStorage. Walks the input
        // pool, removes any of those three items, transfers to base.storage so
        // vanilla logistics can see them.
        //
        // Carcasses (the legitimate manufacturingStorage residents) are left
        // alone — the worker still butchers them via the normal flow.
        //
        // Runs once per cabin per session (the InitializeDelayed coroutine is
        // single-shot via _initialized). Idempotent on subsequent reloads:
        // if no stuck items exist, the method exits with a single "0 drained"
        // log line and is otherwise free.
        private void DrainStuckBearBonusItems()
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;

                var storage     = building.storage;
                var manuStorage = building.manufacturingStorage;
                if (storage == null || manuStorage == null) return;

                var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
                if (wbm == null) return;

                uint drainedMeat   = TransferAll(manuStorage, storage, wbm.itemMeat);
                uint drainedHide   = TransferAll(manuStorage, storage, wbm.itemHide);
                uint drainedTallow = TransferAll(manuStorage, storage, wbm.itemTallow);

                if (drainedMeat + drainedHide + drainedTallow > 0)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] '{gameObject.name}' migrated stuck bear-bonus items " +
                        $"from manufacturingStorage → storage: " +
                        $"+{drainedMeat} meat, +{drainedHide} hide, +{drainedTallow} tallow. " +
                        "These were invisible to logistics in v1.0.0-1.0.7; now hauled normally.");
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] DrainStuckBearBonusItems on '{gameObject.name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Removes ALL unreserved instances of `item` from `from` and adds them
        /// to `to`. Returns the number transferred. Carcasses and other items
        /// are untouched. Uses ReservableItemStorage.RemoveUnreservedItemsClamped
        /// (same API used by vanilla's heavy-tool transfer path in Building.cs).
        /// </summary>
        private static uint TransferAll(
            ReservableItemStorage from, ReservableItemStorage to, Item item)
        {
            if (item == null) return 0;
            uint count = from.GetItemCount(item);
            if (count == 0) return 0;

            var bundle = from.RemoveUnreservedItemsClamped(item, count, null);
            if (bundle == null || bundle.numberOfItems == 0) return 0;

            uint added = to.AddItems(bundle);
            return added;
        }

        // ── Trapper meat-stuck diagnostic ────────────────────────────────────
        //
        // Runs every 30 seconds while DiagnosticsEnabled = true. Logs the
        // cabin's storage state for every hunter cabin (so we can compare
        // Trapper Lodge against Hunting Lodge / unspecialised). Output looks
        // like:
        //
        // [WotW] [TrapperDiag] 'HunterShack_tier2_01A(Clone) #3' path=TrapperLodge
        //   base.storage:           Meat=N (unres=N)  Hide=N  Tallow=N
        //   manufacturingStorage:   Meat=N  Hide=N  Tallow=N  Carcass=N  Small=N
        //   GetItemCountFromAllStorages(Meat) = N
        //
        // Signals to look for in the captured log:
        //   • base.storage Meat HIGH but unres=0 → reservation leak (path A)
        //   • base.storage Meat = 0 but manufacturing Meat HIGH → routing bug (B)
        //   • base.storage Meat HIGH and unres MATCHES → cabin should be visible
        //     (then bucket-registration is the problem — path C)
        private IEnumerator TrapperMeatDiagnosticLoop()
        {
            // Wait one frame so Building.Start has run and storages are wired up.
            yield return null;

            var building = GetComponent<Building>();
            var hunterBld = GetComponent<HunterBuilding>();
            if (building == null) yield break;

            // Resolve item refs once
            var gm  = UnitySingleton<GameManager>.Instance;
            var wbm = gm?.workBucketManager;
            if (wbm == null) yield break;

            var itemMeat        = wbm.itemMeat;
            var itemHide        = wbm.itemHide;
            var itemTallow      = wbm.itemTallow;
            var itemCarcass     = wbm.itemCarcass;
            var itemSmallCarc   = wbm.itemSmallCarcass;
            var itemBoarCarc    = wbm.itemBoarCarcass;
            var itemWolfCarc    = wbm.itemWolfCarcass;

            var wait = new WaitForSeconds(30f);
            while (this != null && gameObject != null)
            {
                yield return wait;
                if (!WardenOfTheWildsMod.DiagnosticsEnabled.Value) continue;

                try
                {
                    var storage     = building.storage;
                    var manuStorage = building.manufacturingStorage;

                    // base.storage counts
                    uint sMeat   = storage?.GetItemCount(itemMeat) ?? 0;
                    uint sHide   = storage?.GetItemCount(itemHide) ?? 0;
                    uint sTallow = storage?.GetItemCount(itemTallow) ?? 0;
                    uint sUnresMeat = storage?.GetNumberOfUnreservedItems(itemMeat) ?? 0;
                    uint sUnresHide = storage?.GetNumberOfUnreservedItems(itemHide) ?? 0;

                    // manufacturingStorage counts
                    uint mMeat   = manuStorage?.GetItemCount(itemMeat)        ?? 0;
                    uint mHide   = manuStorage?.GetItemCount(itemHide)        ?? 0;
                    uint mTallow = manuStorage?.GetItemCount(itemTallow)      ?? 0;
                    uint mCarc   = manuStorage?.GetItemCount(itemCarcass)     ?? 0;
                    uint mSmall  = manuStorage?.GetItemCount(itemSmallCarc)   ?? 0;
                    uint mBoar   = manuStorage?.GetItemCount(itemBoarCarc)    ?? 0;
                    uint mWolf   = manuStorage?.GetItemCount(itemWolfCarc)    ?? 0;

                    // Combined (matches UI display)
                    uint allMeat   = building.GetItemCountFromAllStorages(itemMeat);
                    uint allHide   = building.GetItemCountFromAllStorages(itemHide);
                    uint allTallow = building.GetItemCountFromAllStorages(itemTallow);

                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] [TrapperDiag] '{gameObject.name}' path={_path}\n" +
                        $"  base.storage:         Meat={sMeat} (unres={sUnresMeat})  " +
                        $"Hide={sHide} (unres={sUnresHide})  Tallow={sTallow}\n" +
                        $"  manufacturingStorage: Meat={mMeat}  Hide={mHide}  Tallow={mTallow}  " +
                        $"Carcass={mCarc}  Small={mSmall}  Boar={mBoar}  Wolf={mWolf}\n" +
                        $"  GetItemCountFromAllStorages: Meat={allMeat}  Hide={allHide}  Tallow={allTallow}");
                }
                catch (System.Exception ex)
                {
                    WardenOfTheWildsMod.Log.Warning($"[WotW] TrapperDiag '{gameObject.name}': {ex.Message}");
                }
            }
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
