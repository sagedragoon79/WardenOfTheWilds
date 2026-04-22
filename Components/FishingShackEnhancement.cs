using MelonLoader;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  FishingShackEnhancement
//  MonoBehaviour attached to every FishingShack at scene load.
//
//  Features:
//    Tier 2 — Fishing Dock
//      • +50% base fish output (configurable multiplier)
//      • 2 worker slots
//      • Work area circle (like other buildings — fishing shack oddly has none)
//      • Fish Oil byproduct: each fishing cycle has a chance to produce Fish Oil
//        Fish Oil use cases:
//          → Trade good (base case)
//          → Fertilizer bonus at ForagerShack (Tended Wilds companion)
//          → Lamp fuel (potential future building)
//
//    Low-Stock Notification
//      Monitors local fish stock and fires a warning log / screen notification
//      when fish in the assigned pond drops below a threshold.
//      (TODO: Hook into fish spawn/count system once decompile reveals the type)
//
//  Building rename pattern (Tended Wilds):
//      T1 = "Fisherman's Shack"  (vanilla)
//      T2 = "Fishing Dock"       (patched via Building.SetBuildingDataRecordName postfix)
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Components
{
    public class FishingShackEnhancement : MonoBehaviour
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Persistence ───────────────────────────────────────────────────────
        private static readonly Dictionary<int, float> SavedFishOilAccumulator =
            new Dictionary<int, float>();

        private int GetBuildingKey()
        {
            var pos = transform.position;
            return Mathf.RoundToInt(pos.x * 1000f + pos.z);
        }

        public static void OnMapLoaded()
        {
            SavedFishOilAccumulator.Clear();
        }

        // ── State ─────────────────────────────────────────────────────────────
        private bool  _initialized = false;
        private float _fishOilAccumulator = 0f;  // Partial fish oil credit between cycles
        private WorkArea? _workArea;

        // Fish oil drops out at roughly 1 unit per N fishing cycles (configurable)
        // This accumulator lets us give fractional yields cleanly.
        private const float FishOilPerCycle = 0.33f; // ~1 oil per 3 cycles at Dock

        // ── Unity lifecycle ───────────────────────────────────────────────────
        private void Start()
        {
            StartCoroutine(InitializeDelayed());
        }

        private IEnumerator InitializeDelayed()
        {
            yield return null;
            if (_initialized) yield break;
            _initialized = true;

            if (SavedFishOilAccumulator.TryGetValue(GetBuildingKey(), out float saved))
                _fishOilAccumulator = saved;

            var building = GetComponent<Building>();
            if (building != null && building.tier >= 2)
                ApplyDockUpgrade();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] FishingShackEnhancement on '{gameObject.name}' " +
                $"(tier={building?.tier}, oilAccum={_fishOilAccumulator:F2})");
        }

        // ── Dock upgrade application ──────────────────────────────────────────
        /// <summary>
        /// Applies Tier 2 Fishing Dock stat changes.
        /// Called both on first attach (if already T2) and from the tier-up patch.
        /// </summary>
        public void ApplyDockUpgrade()
        {
            if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

            SetWorkerSlots(2);
            UpdateWorkAreaCircle();

            // Output multiplier applied via FishingShackPatches.OnFishingCycleComplete postfix
            // (storing it here for the patch to read)
            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] '{gameObject.name}' upgraded to Fishing Dock " +
                $"(×{WardenOfTheWildsMod.FishingDockOutputMult.Value:F1} output, 2 workers)");
        }

        // ── Fish Oil production ───────────────────────────────────────────────
        /// <summary>
        /// Called by FishingShackPatches after each successful fish yield.
        /// Accumulates fractional fish oil and returns whole units to produce.
        /// </summary>
        public int ConsumeAccumulatedFishOil()
        {
            var building = GetComponent<Building>();
            if (building == null || building.tier < 2) return 0;

            _fishOilAccumulator += FishOilPerCycle;
            int wholeUnits = Mathf.FloorToInt(_fishOilAccumulator);
            if (wholeUnits > 0)
            {
                _fishOilAccumulator -= wholeUnits;
                SavedFishOilAccumulator[GetBuildingKey()] = _fishOilAccumulator;
            }
            return wholeUnits;
        }

        // TODO: Produce actual Fish Oil items into the building's output storage.
        // This requires:
        //   1. Confirmed ItemID for Fish Oil (new item — may need to reuse an
        //      existing unused slot or hook into item creation).
        //   2. The method to add items to a building's output storage.
        //   From Manifest Delivery: LogisticsRequester.activeMoveOutRequests pattern
        //   From Tended Wilds: ForagerShack storage is ReservableItemStorage
        //
        // Options (to decide after decompile):
        //   A) Produce as Tallow (existing item) with a modifier flag — simplest
        //   B) Reuse an obscure existing ItemID (candles? lamp oil?)
        //   C) Inject into the ItemID enum via reflection and Harmony — complex

        // ── Worker slot adjustment (same helper as HunterCabinEnhancement) ────
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
                WardenOfTheWildsMod.Log.Warning($"[WotW] SetWorkerSlots: {ex.Message}");
            }
        }

        // ── Work area visual circle ───────────────────────────────────────────
        private void UpdateWorkAreaCircle()
        {
            // Fishing shacks have a work radius in vanilla but no visible circle.
            // At T2 we make it visible, matching the ForagerShack UX.
            try
            {
                // TODO: Read actual work radius from FishingShack field once decompile confirms name
                float radius = 40f; // Placeholder — typical fishing shack range

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
                WardenOfTheWildsMod.Log.Warning($"[WotW] FishingShack.UpdateWorkAreaCircle: {ex.Message}");
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
            var sel = GetComponent<SelectableComponent>();
            bool selected = sel != null && sel.IsSelected;

            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                var building = GetComponent<Building>();
                if (_workArea != null && building?.tier >= 2)
                    _workArea.SetEnabled(selected);
            }
        }

        private void OnGUI()
        {
            var sel = GetComponent<SelectableComponent>();
            if (sel == null || !sel.IsSelected) return;

            var building = GetComponent<Building>();
            if (building == null || building.tier < 2) return;

            Vector3 screenPos = Camera.main != null
                ? Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 6f)
                : Vector3.zero;
            if (screenPos.z <= 0) return;

            float y = Screen.height - screenPos.y;
            GUI.color = new Color(0.4f, 0.8f, 1f, 1f); // Blue

            string twInfo = WardenOfTheWildsMod.TendedWildsActive
                ? "\nFish Oil → fertilizer: Active"
                : "";

            GUI.Label(
                new Rect(screenPos.x - 110, y - 45, 220, 45),
                $"[WotW] Fishing Dock\n" +
                $"Output ×{WardenOfTheWildsMod.FishingDockOutputMult.Value:F1}  " +
                $"2 workers  |  Fish Oil: ON" +
                twInfo);

            GUI.color = Color.white;
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
    }
}
