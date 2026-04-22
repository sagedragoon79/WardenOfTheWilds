using MelonLoader;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using WardenOfTheWilds.Systems;

// ─────────────────────────────────────────────────────────────────────────────
//  SmokehouseEnhancement
//  MonoBehaviour attached to every Smokehouse at scene load.
//
//  Features:
//    Work Area Radius
//      Workers only collect raw meat/fish from sources within this radius.
//      Radius is visible as a WorkArea circle when the building is selected.
//      Eliminates the infamous "worker walks across the entire map" problem.
//
//    Source Pinning
//      Player can pin specific Hunter Cabins / Fishing Shacks as exclusive
//      sources for this Smokehouse. Pinned sources override radius entirely.
//      Stored as world-space positions (position-keyed for save/load safety).
//
//  Processing ratios (Meat vs. Fish) are controlled by the vanilla production
//  sliders built into the Smokehouse UI. We don't duplicate that here.
//
//  Companion synergy (Tended Wilds):
//      When herbs or mushrooms are available in nearby ForagerShack storage
//      and this Smokehouse's herb-cure toggle is ON, the next smoking cycle
//      produces herb-cured variants with a food variety bonus tag.
//      (TODO: Hook into ForagerShack inventory check via TendedWildsCompat)
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Components
{
    public class SmokehouseEnhancement : MonoBehaviour
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Persistence ───────────────────────────────────────────────────────
        // Source pins persisted per building position — list of pinned source positions
        private static readonly Dictionary<int, List<Vector3>> SavedPins   = new Dictionary<int, List<Vector3>>();
        // Work radius override persisted per building
        private static readonly Dictionary<int, float>         SavedRadii  = new Dictionary<int, float>();

        private int GetBuildingKey()
        {
            var pos = transform.position;
            return Mathf.RoundToInt(pos.x * 1000f + pos.z);
        }

        public static void OnMapLoaded()
        {
            SavedPins.Clear();
            SavedRadii.Clear();
        }

        // ── State ─────────────────────────────────────────────────────────────
        private List<Vector3> _sourcePins  = new List<Vector3>();
        private float         _workRadius;
        private bool          _initialized = false;
        private WorkArea?     _workArea;

        public float WorkRadius
        {
            get => _workRadius;
            set
            {
                _workRadius = Mathf.Max(5f, value);
                SavedRadii[GetBuildingKey()] = _workRadius;
                UpdateWorkAreaCircle();
            }
        }

        /// <summary>
        /// True when at least one source pin is set.
        /// When true, radius is ignored and only pinned sources are used.
        /// </summary>
        public bool HasPins => _sourcePins.Count > 0;

        public IReadOnlyList<Vector3> SourcePins => _sourcePins;

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

            int key = GetBuildingKey();

            // Restore saved pins
            if (SavedPins.TryGetValue(key, out List<Vector3> savedPins))
                _sourcePins = new List<Vector3>(savedPins);

            // Restore saved radius (or use config default)
            _workRadius = SavedRadii.TryGetValue(key, out float savedRadius)
                ? savedRadius
                : WardenOfTheWildsMod.SmokehouseDefaultRadius.Value;

            UpdateWorkAreaCircle();

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] SmokehouseEnhancement on '{gameObject.name}' " +
                $"(radius={_workRadius:F1}u, pins={_sourcePins.Count}, key={key})");
        }

        // ── Source pin management ─────────────────────────────────────────────
        /// <summary>
        /// Adds a source pin for a specific Hunter Cabin or Fishing Shack.
        /// Pass the building's world position.
        /// </summary>
        public void AddPin(Vector3 sourcePosition)
        {
            // Deduplicate within 1u tolerance
            foreach (var pin in _sourcePins)
            {
                if (Vector3.Distance(pin, sourcePosition) < 1f) return;
            }
            _sourcePins.Add(sourcePosition);
            SavedPins[GetBuildingKey()] = new List<Vector3>(_sourcePins);
            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] Smokehouse '{gameObject.name}': pinned source at {sourcePosition}.");
        }

        /// <summary>Removes a source pin by proximity match.</summary>
        public void RemovePin(Vector3 sourcePosition)
        {
            _sourcePins.RemoveAll(p => Vector3.Distance(p, sourcePosition) < 1f);
            SavedPins[GetBuildingKey()] = new List<Vector3>(_sourcePins);
        }

        public void ClearPins()
        {
            _sourcePins.Clear();
            SavedPins.Remove(GetBuildingKey());
        }

        // ── Radius check helper (used by SmokehousePatches) ───────────────────
        /// <summary>
        /// Returns true if the given world position is an approved source for
        /// this Smokehouse. Respects both pins and radius modes.
        /// </summary>
        public bool IsApprovedSource(Vector3 sourcePosition)
        {
            if (HasPins)
            {
                // Pin mode: only explicitly pinned buildings are approved
                foreach (var pin in _sourcePins)
                {
                    if (Vector3.Distance(pin, sourcePosition) < 2f) return true;
                }
                return false;
            }
            // Radius mode
            float distSqr = (sourcePosition - transform.position).sqrMagnitude;
            return distSqr <= _workRadius * _workRadius;
        }

        // ── Herb-cure synergy (Tended Wilds companion) ────────────────────────
        // TODO: Wire up once TendedWildsCompat.GetHerbStockNear() is implemented.
        // This method will check if sufficient herbs/mushrooms are available within
        // radius from ForagerShack cultivated stocks, and if so flag the next
        // smoking cycle to produce herb-cured variants.
        public bool ShouldHerbCure()
        {
            if (!WardenOfTheWildsMod.TendedWildsActive) return false;
            return TendedWildsCompat.GetHerbStockNear(transform.position, _workRadius) > 0;
        }

        // ── Work area visual circle (Manifest Delivery pattern) ───────────────
        private void UpdateWorkAreaCircle()
        {
            try
            {
                if (_workRadius <= 0f)
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

                _workArea.Init(transform.position, _workRadius);
                RegenerateSelectionCircleEdges(_workArea);

                var sel = GetComponent<SelectableComponent>();
                _workArea.SetEnabled(sel != null && sel.IsSelected);
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning($"[WotW] SmokehouseEnhancement.UpdateWorkAreaCircle: {ex.Message}");
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
                _workArea?.SetEnabled(selected && !HasPins); // Hide circle when using pins
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

            GUI.color = new Color(0.85f, 0.85f, 0.85f, 1f);

            string pinInfo = HasPins
                ? $"Pinned sources: {_sourcePins.Count}"
                : $"Work radius: {_workRadius:F0}u";

            string herbInfo = WardenOfTheWildsMod.TendedWildsActive
                ? $"\nHerb-cure: {(ShouldHerbCure() ? "Available" : "No herbs nearby")}"
                : "";

            GUI.Label(
                new Rect(screenPos.x - 120, y - 45, 240, 45),
                $"[WotW] Smokehouse\n{pinInfo}" +
                herbInfo);

            GUI.color = Color.white;
        }
    }
}
