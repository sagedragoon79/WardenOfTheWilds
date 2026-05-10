using MelonLoader;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  SmokehouseEnhancement
//  MonoBehaviour attached to every SmokeHouse at scene load.
//
//  Heritage: ported (simplified) from Stalk & Smoke v0.1.0, the predecessor
//  mod that became WotW. The original S&S version had source-pinning and
//  search-entry interception; this restored version focuses on the three
//  features users most consistently want:
//
//    1. Visible work-radius circle  — advisory placement aid.
//       (Vanilla SmokeHouse has no WorkArea by default; we draw one to help
//       the player see which Hunter Cabins / Fishing Shacks are within
//       comfortable supply distance.)
//
//    2. Configurable max workers    — vanilla = 1, default override = 2.
//       Two parallel smokers cuts processing time roughly in half.
//
//    3. Increased storage capacity  — both raw input (incoming meat/fish)
//       and smoked output (outgoing). Vanilla caps stall production when a
//       hunter brings in a big haul or when storehouses are full of smoked
//       meat awaiting wagon pickup. Larger caps = smoother throughput.
//
//  All three features are individually toggleable through MelonPreferences
//  (SmokehouseRadiusEnabled, SmokehouseMaxWorkers, *StorageCap entries).
//
//  NO source-radius enforcement is applied — the radius is purely visual.
//  Vanilla's pickup logic is left alone. If the user later wants strict
//  enforcement (workers refuse to walk to far sources), we'd add a separate
//  patch on the SmokeHouse.CheckWorkAvailability search entry.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Components
{
    public class SmokehouseEnhancement : MonoBehaviour
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private bool      _initialized = false;
        private WorkArea? _workArea;
        private bool      _lastSelected = false;

        // ── Unity lifecycle ───────────────────────────────────────────────────
        private void Start()
        {
            StartCoroutine(InitializeDelayed());
        }

        private IEnumerator InitializeDelayed()
        {
            // One-frame delay so the SmokeHouse Building has finished its own
            // Awake/Start before we touch maxWorkers / storage caps.
            yield return null;
            if (_initialized) yield break;
            _initialized = true;

            if (!WardenOfTheWildsMod.SmokehouseOverhaulEnabled.Value)
            {
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] SmokehouseEnhancement on '{gameObject.name}': overhaul disabled, " +
                    "vanilla behaviour retained.");
                yield break;
            }

            ApplyMaxWorkers();
            ApplyStorageCaps();
            UpdateWorkAreaCircle();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] SmokehouseEnhancement on '{gameObject.name}' " +
                $"(workers={WardenOfTheWildsMod.SmokehouseMaxWorkers.Value}, " +
                $"radius={(WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value ? WardenOfTheWildsMod.SmokehouseWorkRadius.Value : 0f):F0}u, " +
                $"rawCap={WardenOfTheWildsMod.SmokehouseRawMeatStorageCap.Value}, " +
                $"smokedCap={WardenOfTheWildsMod.SmokehouseSmokedMeatStorageCap.Value})");

            // RADIUS ENFORCEMENT: prune approach was abandoned after diagnostic
            // confirmed `storagesToStealFrom` is the smokehouse's own internal
            // storages, not external sources. The real source-finding pathway
            // (laborer logistics search entries / StockingSmokeHouse state) is
            // a future investigation item.
            //
            // The WorkOrderDiagnosticLoop below dumped the data needed to
            // figure all that out. It's now gated by the master diagnostics
            // toggle so it stays quiet for normal users — flip the pref if
            // you need to investigate a Crate patch.
            if (WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value
             && WardenOfTheWildsMod.SmokehouseRadiusEnforce.Value
             && WardenOfTheWildsMod.DiagnosticsEnabled.Value)
            {
                StartCoroutine(WorkOrderDiagnosticLoop());
            }
        }

        private void Update()
        {
            // Hot-reload work area on selection so the circle appears/hides
            // with the building selection state.
            var sel = GetComponent<SelectableComponent>();
            bool selected = sel != null && sel.IsSelected;
            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                _workArea?.SetEnabled(selected && WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value);
            }
        }

        // ── Worker slot override ──────────────────────────────────────────────
        private void ApplyMaxWorkers()
        {
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;

                int target = Mathf.Max(1, WardenOfTheWildsMod.SmokehouseMaxWorkers.Value);

                // Same backing-field pattern HunterCabinEnhancement /
                // FishingShackEnhancement use: maxWorkers is an auto-property
                // with the compiler-generated <maxWorkers>k__BackingField.
                var maxField = FindBackingField(building.GetType(), "maxWorkers");
                if (maxField != null)
                {
                    int current = (int)maxField.GetValue(building);
                    if (current != target)
                    {
                        maxField.SetValue(building, target);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' maxWorkers: {current} → {target}");
                    }
                }

                if (building.userDefinedMaxWorkers < target)
                    building.userDefinedMaxWorkers = target;

                // Trigger hiring path so the new slot fills immediately rather
                // than waiting for the next vanilla worker-search tick.
                int currentWorkers = building.workersRO?.Count ?? 0;
                if (currentWorkers < target)
                    building.AttemptToAddMaxWorkers();
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SmokehouseEnhancement.ApplyMaxWorkers: {ex.Message}");
            }
        }

        // ── Storage capacity override ─────────────────────────────────────────
        // The vanilla SmokeHouse class is a sealed implementation we don't
        // have decompiled field names for. We try a list of plausible field
        // names per category (input/output) and override whichever exists.
        // Logging the discovered field name on first attach lets us refine
        // the candidate list over time.
        private static readonly string[] InputCapCandidates =
        {
            "rawMeatStorageCapacity",      "rawStorageCapacity",
            "inputStorageCapacity",        "incomingStorageCapacity",
            "rawMaterialStorageCapacity",  "ingredientStorageCapacity",
            "storageCapacity",  // generic fallback
        };

        private static readonly string[] OutputCapCandidates =
        {
            "smokedMeatStorageCapacity",   "smokedStorageCapacity",
            "outputStorageCapacity",       "finishedStorageCapacity",
            "productionStorageCapacity",   "productStorageCapacity",
        };

        private void ApplyStorageCaps()
        {
            // CONFIRMED PATH (April 2026 diagnostic dump):
            //
            // Cap lives on ItemDefinition.capacity (Int32), where ItemDefinition
            // is the base class for SourceItemDefinition (raw input entries on
            // sourceItems[]) and ProducedItemDefinition (smoked output entries
            // on producedItems[]). Both are inside ManufactureDefinition, which
            // is a ScriptableObject shared globally across all smokehouses.
            //
            // Hierarchy:
            //   Building.manufactureDefinitions: List<ManufactureDefinition>
            //     .sourceItems:   List<SourceItemDefinition : ItemDefinition>
            //     .producedItems: List<ProducedItemDefinition : ItemDefinition>
            //   ItemDefinition.capacity (Int32)   ← target (was 100, want 200)
            //   ItemDefinition.itemName (String)  ← match key (e.g. "ItemSmokedMeat")
            //
            // Because the SO is shared, we only need to write each cap ONCE
            // per process — gated by the static dictionary below.
            try
            {
                var building = GetComponent<Building>();
                if (building == null) return;

                int rawTarget    = Mathf.Max(1, WardenOfTheWildsMod.SmokehouseRawMeatStorageCap.Value);
                int smokedTarget = Mathf.Max(1, WardenOfTheWildsMod.SmokehouseSmokedMeatStorageCap.Value);

                var manuField = FindField(building.GetType(), "manufactureDefinitions")
                             ?? FindBackingField(building.GetType(), "manufactureDefinitions");
                if (!(manuField?.GetValue(building) is System.Collections.IList defs)) return;

                foreach (var def in defs)
                {
                    if (def == null) continue;
                    Type defType = def.GetType();
                    string defName = (def is UnityEngine.Object uo) ? uo.name : defType.Name;

                    // Sources (raw meat / fish input) — bump to rawTarget
                    var srcField = defType.GetField("sourceItems", AllInstance);
                    ApplyCapOnList(srcField?.GetValue(def) as System.Collections.IList, rawTarget,
                        defName, "source");

                    // Produced (smoked meat / fish output) — bump to smokedTarget
                    var prodField = defType.GetField("producedItems", AllInstance);
                    ApplyCapOnList(prodField?.GetValue(def) as System.Collections.IList, smokedTarget,
                        defName, "produced");
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SmokehouseEnhancement.ApplyStorageCaps: {ex.Message}");
            }
        }

        // Tracks which (recipeName, side, itemName) tuples have already had
        // their cap written, so we don't spam the log when multiple smokehouses
        // attach in sequence. Static — same SO is shared across all smokehouses.
        private static readonly System.Collections.Generic.HashSet<string> _capApplied
            = new System.Collections.Generic.HashSet<string>();

        private void ApplyCapOnList(
            System.Collections.IList? list, int target, string defName, string side)
        {
            if (list == null) return;
            foreach (var entry in list)
            {
                if (entry == null) continue;
                Type et = entry.GetType();

                // ItemDefinition is a base class — walk up to find capacity + itemName.
                FieldInfo? capField  = FindField(et, "capacity");
                FieldInfo? nameField = FindField(et, "itemName");
                if (capField == null || capField.FieldType != typeof(int)) continue;

                string itemName = (nameField?.GetValue(entry) as string) ?? "?";
                int current = (int)capField.GetValue(entry);
                if (current == target) continue;

                // Skip non-target side items (e.g. firewood is a sourceItem
                // but we shouldn't bump firewood capacity — leave non-meat /
                // non-fish source items alone).
                if (side == "source" && itemName != "ItemMeat" && itemName != "ItemFish")
                    continue;

                string key = $"{defName}|{side}|{itemName}";
                if (!_capApplied.Add(key)) continue; // already done this session

                capField.SetValue(entry, target);
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] {defName}.{side}({itemName}).capacity: {current} → {target}");
            }
        }

        /// <summary>
        /// Reads a ReservableItemStorage from a SmokeHouse property
        /// (`storage` / `manufacturingStorage`), then sets its capacity
        /// field. ReservableItemStorage capacity field name is unknown
        /// — we try a candidate list and fall back to dumping all int/uint
        /// fields if none match, so we can refine for future builds.
        /// </summary>
        private bool TryApplyReservableCapacity(
            object building, Type buildingType, string propName, int target, string label)
        {
            try
            {
                // Resolve the property
                var prop = buildingType.GetProperty(propName, AllInstance);
                if (prop == null)
                {
                    // Sometimes the field is exposed directly, not via property
                    var f = FindField(buildingType, propName);
                    if (f == null)
                    {
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] '{gameObject.name}' {label}: property/field '{propName}' not found.");
                        return false;
                    }
                    var direct = f.GetValue(building);
                    return ApplyCapacityOnStorageInstance(direct, target, label, propName);
                }

                var storage = prop.GetValue(building, null);
                return ApplyCapacityOnStorageInstance(storage, target, label, propName);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SmokehouseEnhancement.TryApplyReservableCapacity({propName}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Walks the candidate field names on a ReservableItemStorage
        /// instance and sets capacity. On first miss, dumps all int/uint
        /// fields once per process so we can update CapacityCandidates.
        /// </summary>
        private static readonly string[] CapacityCandidates =
        {
            "capacity",         "_capacity",
            "maxCapacity",      "_maxCapacity",
            "maxItems",         "_maxItems",
            "totalCapacity",    "_totalCapacity",
            "storageCapacity",  "_storageCapacity",
            "size",             "_size",
        };

        private static bool _storageFieldsDumped = false;
        private static bool _capFailureLogged   = false;

        private bool ApplyCapacityOnStorageInstance(
            object? storage, int target, string label, string propName)
        {
            if (storage == null)
            {
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' {label}: '{propName}' resolves to null " +
                    "(building hasn't been initialised yet?).");
                return false;
            }

            Type t = storage.GetType();

            foreach (var name in CapacityCandidates)
            {
                var f = FindField(t, name);
                if (f == null) continue;

                try
                {
                    if (f.FieldType == typeof(int))
                    {
                        int current = (int)f.GetValue(storage);
                        if (current != target)
                        {
                            f.SetValue(storage, target);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' {label} ({propName}.{name}): " +
                                $"{current} → {target}");
                        }
                        return true;
                    }
                    if (f.FieldType == typeof(uint))
                    {
                        uint current = (uint)f.GetValue(storage);
                        uint t2 = (uint)target;
                        if (current != t2)
                        {
                            f.SetValue(storage, t2);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' {label} ({propName}.{name}): " +
                                $"{current} → {t2}");
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        $"[WotW] {propName}.{name} set: {ex.Message}");
                }
            }

            if (!_storageFieldsDumped)
            {
                _storageFieldsDumped = true;
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] '{gameObject.name}' {label}: no capacity field on " +
                    $"{t.FullName}. Dumping all int/uint fields once per session:");
                Type? cur = t;
                while (cur != null && cur != typeof(object))
                {
                    foreach (var f in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType != typeof(int) && f.FieldType != typeof(uint)) continue;
                        object? v = null; try { v = f.GetValue(storage); } catch { }
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW]   {cur.Name}.{f.Name} ({f.FieldType.Name}) = {v}");
                    }
                    cur = cur.BaseType;
                }
            }
            return false;
        }

        private bool TryApplyIntOrUintField(
            object instance, Type startType, string[] candidates, int target, string label)
        {
            foreach (var name in candidates)
            {
                var f = FindField(startType, name);
                if (f == null) continue;

                try
                {
                    if (f.FieldType == typeof(int))
                    {
                        int current = (int)f.GetValue(instance);
                        if (current != target)
                        {
                            f.SetValue(instance, target);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' {label} ({name}): {current} → {target}");
                        }
                        return true;
                    }
                    if (f.FieldType == typeof(uint))
                    {
                        uint current = (uint)f.GetValue(instance);
                        uint t2      = (uint)target;
                        if (current != t2)
                        {
                            f.SetValue(instance, t2);
                            WardenOfTheWildsMod.Log.Msg(
                                $"[WotW] '{gameObject.name}' {label} ({name}): {current} → {t2}");
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        $"[WotW] SmokehouseEnhancement set {name}: {ex.Message}");
                }
            }
            return false;
        }

        private void DumpStorageLikeFields(Type t)
        {
            Type? cur = t;
            while (cur != null && cur != typeof(MonoBehaviour))
            {
                foreach (var f in cur.GetFields(AllInstance))
                {
                    string n = f.Name.ToLowerInvariant();
                    if (!(n.Contains("storage") || n.Contains("capacity") || n.Contains("cap"))) continue;
                    if (f.FieldType != typeof(int) && f.FieldType != typeof(uint)) continue;
                    object? v = null;
                    try { v = f.GetValue(GetComponent<Building>()); } catch { }
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW]   {cur.Name}.{f.Name} ({f.FieldType.Name}) = {v}");
                }
                cur = cur.BaseType;
            }
        }

        /// <summary>
        /// Broader dump than DumpStorageLikeFields — every int/uint field on
        /// the building's full inheritance chain, regardless of name. The
        /// 100/100 cap MUST exist as a numeric value somewhere — by exhausting
        /// the field set we'll spot any candidate reading "100" that we missed
        /// with name filters.
        /// </summary>
        private void DumpAllNumericBuildingFields(Type t)
        {
            var building = GetComponent<Building>();
            if (building == null) return;
            Type? cur = t;
            while (cur != null && cur != typeof(MonoBehaviour))
            {
                foreach (var f in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    if (ft != typeof(int) && ft != typeof(uint)
                     && ft != typeof(short) && ft != typeof(ushort)
                     && ft != typeof(byte) && ft != typeof(sbyte)
                     && ft != typeof(long) && ft != typeof(ulong)) continue;
                    object? v = null;
                    try { v = f.GetValue(building); } catch { }
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW]   {cur.Name}.{f.Name} ({ft.Name}) = {v}");
                }
                cur = cur.BaseType;
            }
        }

        // ── Work area visual circle ───────────────────────────────────────────
        private void UpdateWorkAreaCircle()
        {
            try
            {
                if (!WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value)
                {
                    _workArea?.SetEnabled(false);
                    return;
                }

                float radius = Mathf.Max(5f, WardenOfTheWildsMod.SmokehouseWorkRadius.Value);

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
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SmokehouseEnhancement.UpdateWorkAreaCircle: {ex.Message}");
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
            catch { /* visual nicety only */ }
        }

        // ── Radius enforcement: storagesToStealFrom pruning ───────────────────
        //
        // The SmokeDiag dump confirmed SmokeHouse exposes:
        //   List<...> get_storagesToStealFrom (0 args)
        //   void     set_storagesToStealFrom (1 args)
        // This is the list of HunterCabin / FishingShack storages this building
        // is allowed to pull raw meat / raw fish from. Vanilla rebuilds it
        // periodically based on global proximity; we re-prune to OUR radius
        // every few seconds so out-of-radius entries never persist long enough
        // for a worker task to lock onto them.
        //
        // Approach: every 5 seconds, walk the list and remove any entry whose
        // owning Component / Building is farther than the configured radius
        // from this Smokehouse's transform.position. The list type is unknown
        // but it's IList-shaped so we can iterate + remove via reflection.
        //
        // Logged on first prune so we can confirm the field is the right one
        // and see how many sources we're filtering out.
        private int  _diagTick = 0;
        private bool _workOrderShapeDumped = false;
        private bool _manufactureDefDeepDumped = false;
        private bool _cachedSourcesEverPopulated = false;
        private bool _cachedSourcesDumpedRich = false;

        private IEnumerator WorkOrderDiagnosticLoop()
        {
            // Bumped from 5 to 30 ticks (~150 seconds of monitoring). The
            // smokehouse needs time to actually request raw materials; the
            // first 5 ticks captured an idle-on-stock state. Now we wait
            // long enough to catch cachedSourceItems populated.

            // v1.0.11 — Per-smokehouse random initial offset to spread diag
            // ticks evenly across the 5s cadence so multiple smokehouses
            // don't all log + run reflection-heavy diagnostics on the same
            // frame.
            yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 5f));

            var wait = new WaitForSeconds(5f);
            while (this != null && gameObject != null)
            {
                yield return wait;
                if (_diagTick >= 30) yield break;
                _diagTick++;
                try
                {
                    DiagWorkOrders();
                    DiagCachedSources();
                    DiagManufactureDefDeep();   // one-shot: every field on the recipe SO
                }
                catch (Exception ex)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        $"[WotW] WorkOrderDiagnosticLoop on '{gameObject.name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// One-shot per smokehouse: dump every field on the FIRST manufacture
        /// definition (e.g. ManufactureSmokedMeat). The earlier numeric-only
        /// dump only surfaced `_workUnitsForProduction` + `workUnitModifier`.
        /// The cap field is non-numeric (Item reference, recipe-output ref,
        /// or similar nested SO) — exhaustive dump will reveal the path.
        /// </summary>
        private void DiagManufactureDefDeep()
        {
            if (_manufactureDefDeepDumped) return;
            var building = GetComponent<Building>();
            if (building == null) return;
            var t = building.GetType();
            var manuField = FindField(t, "manufactureDefinitions")
                         ?? FindBackingField(t, "manufactureDefinitions");
            var raw = manuField?.GetValue(building);
            if (!(raw is System.Collections.IList list) || list.Count == 0) return;
            _manufactureDefDeepDumped = true;

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] [Diag] '{gameObject.name}' ManufactureDefinition deep dump:");
            int idx = 0;
            foreach (var entry in list)
            {
                if (entry == null) { idx++; continue; }
                Type et = entry.GetType();
                string entryName = entry is UnityEngine.Object uo ? $"'{uo.name}'" : "(non-Object)";
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]   [{idx}] {et.Name} {entryName}");
                Type? cur = et;
                while (cur != null && cur != typeof(object) && cur != typeof(UnityEngine.Object) && cur != typeof(ScriptableObject))
                {
                    foreach (var f in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        object? v = null;
                        try { v = f.GetValue(entry); } catch { }
                        string vs = StringifyShort(v);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] [Diag]     {cur.Name}.{f.Name} ({f.FieldType.Name}) = {vs}");
                    }
                    cur = cur.BaseType;
                }
                // Inline expand sourceItems[0] and producedItems[0] — the cap
                // field (100) lives on one of these entries.
                DumpListEntryShape(entry, et, "sourceItems");
                DumpListEntryShape(entry, et, "producedItems");
                idx++;
            }
        }

        /// <summary>
        /// Walks an internal List<> on a ManufactureDefinition (e.g.
        /// `sourceItems` or `producedItems`) and dumps every field on the
        /// first entry. The 100/100 cap value should appear here as one of
        /// the entry's numeric fields (likely `maxStockpile` /
        /// `producedQuantityCap` / similar on producedItems).
        /// </summary>
        private void DumpListEntryShape(object owner, Type ownerType, string listFieldName)
        {
            try
            {
                var f = ownerType.GetField(listFieldName, AllInstance);
                if (f == null) return;
                if (!(f.GetValue(owner) is System.Collections.IList list)) return;
                if (list.Count == 0) return;
                var first = list[0];
                if (first == null) return;
                Type ft = first.GetType();
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]     ↳ {listFieldName}[0] ({ft.Name}) field shape:");
                Type? cur = ft;
                while (cur != null && cur != typeof(object))
                {
                    foreach (var fld in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        object? v = null;
                        try { v = fld.GetValue(first); } catch { }
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] [Diag]         {cur.Name}.{fld.Name} ({fld.FieldType.Name}) = {StringifyShort(v)}");
                    }
                    cur = cur.BaseType;
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] DumpListEntryShape({listFieldName}): {ex.Message}");
            }
        }

        private static string StringifyShort(object? v)
        {
            try
            {
                if (v == null) return "(null)";
                if (v is GameObject go) return $"GameObject:'{go.name}'";
                if (v is Component cmp)  return $"{cmp.GetType().Name}@'{cmp.gameObject?.name ?? "?"}'";
                if (v is UnityEngine.Object uo) return $"{uo.GetType().Name}:'{uo.name}'";
                if (v is System.Collections.ICollection col) return $"<{col.GetType().Name}, count={col.Count}>";
                string s = v.ToString() ?? "(null)";
                if (s.Length > 100) s = s.Substring(0, 100) + "...";
                return s;
            }
            catch { return "(threw)"; }
        }

        private void DiagWorkOrders()
        {
            var building = GetComponent<Building>();
            if (building == null) return;
            var t = building.GetType();

            var f = FindField(t, "workOrders");
            var raw = f?.GetValue(building);
            if (!(raw is System.Collections.IList list))
            {
                if (_diagTick == 1)
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] [Diag] '{gameObject.name}' workOrders: not IList " +
                        $"(field present={f != null}).");
                return;
            }

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] [Diag] '{gameObject.name}' tick #{_diagTick}: " +
                $"workOrders count={list.Count}");

            // First tick: dump the field shape of one entry so we know what
            // ManufactureWorkOrder carries (item id, quantity, source ref, etc).
            if (!_workOrderShapeDumped && list.Count > 0 && list[0] != null)
            {
                _workOrderShapeDumped = true;
                var entry = list[0];
                Type et = entry.GetType();
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]   ManufactureWorkOrder shape ({et.FullName}):");
                Type? cur = et;
                while (cur != null && cur != typeof(object))
                {
                    foreach (var fld in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        object? v = null;
                        try { v = fld.GetValue(entry); } catch { }
                        string vs;
                        try
                        {
                            if (v == null) vs = "(null)";
                            else if (v is GameObject go) vs = $"GameObject:'{go.name}'";
                            else if (v is Component cmp) vs = $"{cmp.GetType().Name}@'{cmp.gameObject?.name ?? "?"}'";
                            else vs = v.ToString() ?? "(null)";
                            if (vs.Length > 100) vs = vs.Substring(0, 100) + "...";
                        }
                        catch { vs = "(threw)"; }
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] [Diag]     {cur.Name}.{fld.Name} ({fld.FieldType.Name}) = {vs}");
                    }
                    cur = cur.BaseType;
                }
            }

            // Per-tick summary of all entries
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry == null)
                {
                    WardenOfTheWildsMod.Log.Msg($"[WotW] [Diag]   [{i}] null");
                    continue;
                }
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]   [{i}] {entry.GetType().Name}");
            }
        }

        private void DiagCachedSources()
        {
            var building = GetComponent<Building>();
            if (building == null) return;
            var t = building.GetType();

            var fItems = FindField(t, "cachedSourceItems");
            var fQtys  = FindField(t, "cachedSourceItemQtys");
            var items = fItems?.GetValue(building) as System.Collections.IList;
            var qtys  = fQtys?.GetValue(building)  as System.Collections.IList;
            int icount = items?.Count ?? -1;
            int qcount = qtys?.Count  ?? -1;

            if (icount <= 0 && qcount <= 0) return;

            // First time we see populated entries: do a rich dump showing
            // every field per entry. After that, just summarize counts.
            if (!_cachedSourcesEverPopulated)
            {
                _cachedSourcesEverPopulated = true;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag] '{gameObject.name}' tick #{_diagTick}: " +
                    $"cachedSourceItems FIRST POPULATED (items={icount}, qtys={qcount})");
            }

            if (!_cachedSourcesDumpedRich && items != null && items.Count > 0 && items[0] != null)
            {
                _cachedSourcesDumpedRich = true;
                var first = items[0];
                Type et = first.GetType();
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]   cachedSourceItems[0] shape ({et.FullName}):");
                Type? cur = et;
                while (cur != null && cur != typeof(object))
                {
                    foreach (var fld in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        object? v = null;
                        try { v = fld.GetValue(first); } catch { }
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] [Diag]     {cur.Name}.{fld.Name} ({fld.FieldType.Name}) = {StringifyShort(v)}");
                    }
                    cur = cur.BaseType;
                }
            }

            // Per-tick summary
            int n = Mathf.Min(icount, 4);
            for (int i = 0; i < n; i++)
            {
                object? itm = items != null && i < items.Count ? items[i] : null;
                object? qty = qtys  != null && i < qtys.Count  ? qtys[i]  : null;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]   [{i}] item={StringifyShort(itm)} qty={qty}");
            }
        }

        // ── DEPRECATED diagnostic methods (kept compiled out for reference) ──
        //
        // The original `storagesToStealFrom` pruning approach was abandoned
        // when the diagnostic dump revealed the list is the smokehouse's OWN
        // internal storages (input + output), not external sources. The
        // workOrders / cachedSourceItems diagnostic above is the new path.
        //
        // Methods below are kept as stubs (returning early) in case we want
        // to revive any of the dump logic. The `manufactureDefinitions[]`
        // dump already ran; results were `_workUnitsForProduction` + a
        // workUnitModifier, no capacity field.
#if WOTW_LEGACY_PRUNE_DIAG
        private void DiagDumpListAndManufactureOnce()
        {
            if (_diagListsDumped && _diagManufactureDumped) return;

            var building = GetComponent<Building>();
            if (building == null) return;
            Type t = building.GetType();

            // (c) — list-typed properties / fields on the building chain
            if (!_diagListsDumped)
            {
                _diagListsDumped = true;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag] '{gameObject.name}' List<>-typed members on building chain:");
                Type? cur = t;
                while (cur != null && cur != typeof(MonoBehaviour))
                {
                    foreach (var p in cur.GetProperties(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        if (!p.PropertyType.IsGenericType) continue;
                        if (p.PropertyType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)) continue;
                        var arg = p.PropertyType.GetGenericArguments()[0];
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] [Diag]   {cur.Name}.{p.Name} : List<{arg.Name}>");
                    }
                    foreach (var f in cur.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        if (!f.FieldType.IsGenericType) continue;
                        if (f.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)) continue;
                        var arg = f.FieldType.GetGenericArguments()[0];
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] [Diag]   {cur.Name}.{f.Name} (field) : List<{arg.Name}>");
                    }
                    cur = cur.BaseType;
                }
            }

            // (d) — manufactureDefinitions per-entry numeric fields
            if (!_diagManufactureDumped)
            {
                _diagManufactureDumped = true;
                var manuField = FindField(t, "manufactureDefinitions")
                             ?? FindBackingField(t, "manufactureDefinitions");
                var raw = manuField?.GetValue(building);
                if (raw is System.Collections.IList list && list.Count > 0)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] [Diag] '{gameObject.name}' manufactureDefinitions ({list.Count} entries):");
                    int idx = 0;
                    foreach (var entry in list)
                    {
                        if (entry == null) { idx++; continue; }
                        Type et = entry.GetType();
                        WardenOfTheWildsMod.Log.Msg($"[WotW] [Diag]   [{idx}] {et.Name}");
                        Type? ec = et;
                        while (ec != null && ec != typeof(object))
                        {
                            foreach (var f in ec.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                            {
                                Type ft = f.FieldType;
                                if (ft != typeof(int) && ft != typeof(uint)
                                 && ft != typeof(short) && ft != typeof(ushort)
                                 && ft != typeof(float)) continue;
                                object? v = null;
                                try { v = f.GetValue(entry); } catch { }
                                WardenOfTheWildsMod.Log.Msg(
                                    $"[WotW] [Diag]     {ec.Name}.{f.Name} ({ft.Name}) = {v}");
                            }
                            ec = ec.BaseType;
                        }
                        idx++;
                    }
                }
                else
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] [Diag] '{gameObject.name}' manufactureDefinitions: empty/null.");
                }
            }
        }

        private void DiagPruneVerbose()
        {
            if (_diagPruneCounter >= 5) return;
            _diagPruneCounter++;

            var smoke = GetComponent<Building>();
            if (smoke == null) return;
            var listProp = smoke.GetType().GetProperty("storagesToStealFrom", AllInstance);
            if (listProp == null) return;
            var raw = listProp.GetValue(smoke, null);
            if (!(raw is System.Collections.IList list))
            {
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag] '{gameObject.name}' tick #{_diagPruneCounter}: " +
                    $"storagesToStealFrom not IList (raw={raw?.GetType().Name ?? "null"}).");
                return;
            }

            float radius = Mathf.Max(5f, WardenOfTheWildsMod.SmokehouseWorkRadius.Value);
            Vector3 myPos = transform.position;

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] [Diag] '{gameObject.name}' tick #{_diagPruneCounter}: " +
                $"storagesToStealFrom count={list.Count} (radius={radius:F0}u)");

            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry == null)
                {
                    WardenOfTheWildsMod.Log.Msg($"[WotW] [Diag]   [{i}] null entry");
                    continue;
                }
                Type et = entry.GetType();
                Vector3? pos = TryGetSourcePosition(entry);
                string posStr = pos == null
                    ? "POS?"
                    : $"({pos.Value.x:F0},{pos.Value.z:F0}) " +
                      $"d={Vector3.Distance(myPos, pos.Value):F0}u " +
                      $"{(Vector3.Distance(myPos, pos.Value) > radius ? "OUT" : "IN")}";
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] [Diag]   [{i}] {et.Name} {posStr}");
            }
        }

        private void PruneOnce()
        {
            if (!WardenOfTheWildsMod.SmokehouseOverhaulEnabled.Value) return;
            if (!WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value)   return;
            if (!WardenOfTheWildsMod.SmokehouseRadiusEnforce.Value)   return;

            var smoke = GetComponent<Building>();
            if (smoke == null) return;

            float radius = Mathf.Max(5f, WardenOfTheWildsMod.SmokehouseWorkRadius.Value);
            float radiusSqr = radius * radius;
            Vector3 myPos = transform.position;

            // Property is on EnterableBuilding (parent of SmokeHouse).
            // Resolve via the actual instance type — works regardless of
            // whether the property was declared on the base or derived class.
            var listProp = smoke.GetType().GetProperty("storagesToStealFrom",
                AllInstance);
            if (listProp == null) return;

            var raw = listProp.GetValue(smoke, null);
            if (!(raw is System.Collections.IList list)) return;
            if (list.Count == 0) return;

            int countBefore = list.Count;
            int removed = 0;

            // Iterate backwards because we're removing items by index.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                if (entry == null) continue;

                Vector3? sourcePos = TryGetSourcePosition(entry);
                if (sourcePos == null) continue; // unknown shape — leave alone

                if ((sourcePos.Value - myPos).sqrMagnitude > radiusSqr)
                {
                    list.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0 && !_firstPruneLogged)
            {
                _firstPruneLogged = true;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] '{gameObject.name}' radius prune: " +
                    $"removed {removed}/{countBefore} out-of-radius source(s) " +
                    $"(radius={radius:F0}u). Subsequent prunes silent.");
            }
        }

        /// <summary>
        /// Best-effort extraction of a world-space position from a
        /// storagesToStealFrom entry. The list type is unknown — entries
        /// might be the raw storage component, a wrapper, or a Building.
        /// We try common shapes and return null if we can't resolve a
        /// position (caller leaves the entry alone in that case).
        /// </summary>
        private static Vector3? TryGetSourcePosition(object entry)
        {
            try
            {
                // Direct shapes ─ Component / GameObject / Transform
                if (entry is Component comp) return comp.transform.position;
                if (entry is GameObject go)  return go.transform.position;
                if (entry is Transform trf)  return trf.position;

                // Wrapper shape ─ object with a `building` / `owner` / `storage` field
                Type t = entry.GetType();
                foreach (var fname in new[] {
                    "building", "_building", "owner", "_owner",
                    "storage",  "_storage",  "container",
                    "source",   "sourceBuilding"
                })
                {
                    var f = t.GetField(fname, AllInstance);
                    if (f == null) continue;
                    var v = f.GetValue(entry);
                    if (v is Component c) return c.transform.position;
                    if (v is GameObject g) return g.transform.position;
                }

                // Property-based wrapper
                foreach (var pname in new[] {
                    "building", "owner", "storage", "source", "sourceBuilding"
                })
                {
                    var p = t.GetProperty(pname, AllInstance);
                    if (p == null) continue;
                    var v = p.GetValue(entry, null);
                    if (v is Component c) return c.transform.position;
                    if (v is GameObject g) return g.transform.position;
                }
            }
            catch { /* fall through to null */ }
            return null;
        }
#endif // WOTW_LEGACY_PRUNE_DIAG

        // ── Reflection helpers (matching HunterCabinEnhancement) ──────────────
        private static FieldInfo? FindBackingField(Type startType, string propertyName)
        {
            string backingName = $"<{propertyName}>k__BackingField";
            Type? t = startType;
            while (t != null)
            {
                var f = t.GetField(backingName, AllInstance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static FieldInfo? FindField(Type startType, string fieldName)
        {
            Type? t = startType;
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
