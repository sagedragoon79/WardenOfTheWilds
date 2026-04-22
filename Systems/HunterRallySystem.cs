using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Hotkey-driven rally and recall system for hunters.
    ///
    /// Vanilla civilian villagers can't be right-clicked to move, nor added
    /// to soldier control groups (SoldierControls hard-gates on
    /// VillagerOccupationSoldier). This system sidesteps that entirely by
    /// force-setting NavMeshAgent destinations on all hunters.
    ///
    /// Features:
    ///   RallyKey (default G) → hunters move to current cursor world position.
    ///   ReturnHomeKey (default J) → hunters walk back to their assigned cabin.
    ///
    /// Once hunters arrive at a rally point, our proactive-engage system
    /// kicks in — they'll fight any raider/wolf in range without further
    /// player input. So the workflow is: hotkey → fight → hotkey return →
    /// back to hunting.
    /// </summary>
    public static class HunterRallySystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Resolved keycodes — parsed from config strings once per map load
        private static KeyCode _rallyKey = KeyCode.K;
        private static KeyCode _rallyModifier = KeyCode.None;
        private static KeyCode _returnHomeKey = KeyCode.F10;
        private static KeyCode _returnHomeModifier = KeyCode.None;
        private static KeyCode _selectAllKey = KeyCode.K;
        private static KeyCode _selectAllModifier = KeyCode.LeftControl;
        private static bool _keysResolved = false;

        // Throttle re-parse so config edits mid-session don't require reload
        private static float _lastKeyResolve = 0f;
        private const float KeyResolveInterval = 5f;

        // One-shot log so we can confirm OnUpdate is actually firing
        private static bool _firstTickLogged = false;

        /// <summary>Called from Plugin.OnUpdate every frame.</summary>
        public static void Tick()
        {
            ResolveKeysIfStale();

            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                MelonLogger.Msg(
                    $"[WotW] HunterRallySystem tick alive. " +
                    $"selectAll={FormatCombo(_selectAllKey, _selectAllModifier)}, " +
                    $"rally={FormatCombo(_rallyKey, _rallyModifier)}, " +
                    $"returnHome={FormatCombo(_returnHomeKey, _returnHomeModifier)}");
            }

            // Select-all takes precedence — most specific combo (modifier+key)
            // is tested first so a Ctrl+K press doesn't also trigger bare-K Rally.
            if (IsComboDown(_selectAllKey, _selectAllModifier))
            {
                MelonLogger.Msg(
                    $"[WotW] Select-all combo ({FormatCombo(_selectAllKey, _selectAllModifier)}) pressed.");
                SelectAllHunters();
            }
            else if (IsComboDown(_rallyKey, _rallyModifier))
            {
                MelonLogger.Msg(
                    $"[WotW] Rally combo ({FormatCombo(_rallyKey, _rallyModifier)}) pressed.");
                RallyToCursor();
            }
            else if (IsComboDown(_returnHomeKey, _returnHomeModifier))
            {
                MelonLogger.Msg(
                    $"[WotW] Return-home combo ({FormatCombo(_returnHomeKey, _returnHomeModifier)}) pressed.");
                ReturnAllHome();
            }
        }

        private static void ResolveKeysIfStale()
        {
            if (_keysResolved && Time.time - _lastKeyResolve < KeyResolveInterval)
                return;
            _keysResolved = true;
            _lastKeyResolve = Time.time;

            ParseBinding(WardenOfTheWildsMod.HunterRallyKeyName.Value,
                KeyCode.K, out _rallyKey, out _rallyModifier);
            ParseBinding(WardenOfTheWildsMod.HunterReturnHomeKeyName.Value,
                KeyCode.F10, out _returnHomeKey, out _returnHomeModifier);
            ParseBinding(WardenOfTheWildsMod.HunterSelectAllKeyName.Value,
                KeyCode.K, out _selectAllKey, out _selectAllModifier);
        }

        /// <summary>
        /// Parses strings like "R", "9", "Keypad9", "Alt+R", "Ctrl+H",
        /// "LeftAlt+F6" into a key + optional modifier. Returns fallback for
        /// the key with no modifier if parsing fails.
        /// </summary>
        private static void ParseBinding(
            string raw, KeyCode fallback,
            out KeyCode key, out KeyCode modifier)
        {
            key = fallback;
            modifier = KeyCode.None;
            if (string.IsNullOrWhiteSpace(raw)) return;

            string s = raw.Trim();
            // Split on '+' for modifier combos
            if (s.Contains("+"))
            {
                var parts = s.Split('+');
                if (parts.Length == 2)
                {
                    modifier = ResolveModifier(parts[0].Trim());
                    key = ResolveSingleKey(parts[1].Trim(), fallback);
                    return;
                }
            }

            key = ResolveSingleKey(s, fallback);
        }

        private static KeyCode ResolveSingleKey(string s, KeyCode fallback)
        {
            if (s.Length == 1 && char.IsDigit(s[0]))
                s = "Alpha" + s;
            return Enum.TryParse(s, ignoreCase: true, out KeyCode k) ? k : fallback;
        }

        private static KeyCode ResolveModifier(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "alt":
                case "leftalt":   return KeyCode.LeftAlt;
                case "rightalt":  return KeyCode.RightAlt;
                case "ctrl":
                case "control":
                case "leftctrl":
                case "leftcontrol":  return KeyCode.LeftControl;
                case "rightctrl":
                case "rightcontrol": return KeyCode.RightControl;
                case "shift":
                case "leftshift":  return KeyCode.LeftShift;
                case "rightshift": return KeyCode.RightShift;
                default:
                    return Enum.TryParse(s, ignoreCase: true, out KeyCode k) ? k : KeyCode.None;
            }
        }

        private static bool IsComboDown(KeyCode key, KeyCode modifier)
        {
            if (!Input.GetKeyDown(key)) return false;
            if (modifier == KeyCode.None) return true;

            // Accept either left OR right variant of a modifier so the combo
            // works regardless of which physical modifier the user presses.
            // (Some keyboards send RightControl for common shortcut presses.)
            switch (modifier)
            {
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                    return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                default:
                    return Input.GetKey(modifier);
            }
        }

        private static string FormatCombo(KeyCode key, KeyCode modifier)
        {
            return modifier == KeyCode.None ? key.ToString() : $"{modifier}+{key}";
        }

        /// <summary>
        /// Ray-casts from the mouse cursor to the terrain and moves all hunters
        /// to that point. Silently no-ops if the cursor isn't over the world
        /// (e.g., hovering over UI).
        /// </summary>
        private static void RallyToCursor()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            // Cast against the Terrain layer preferentially, fall back to any hit
            int terrainMask = 1 << LayerMask.NameToLayer("Terrain");
            if (!Physics.Raycast(ray, out RaycastHit hit, 5000f, terrainMask))
            {
                if (!Physics.Raycast(ray, out hit, 5000f)) return;
            }

            Vector3 dest = hit.point;
            int moved = MoveAllHunters(dest, "rally");
            if (moved > 0)
                MelonLogger.Msg(
                    $"[WotW] Rally: {moved} hunter(s) → ({dest.x:F0}, {dest.z:F0})");
        }

        /// <summary>
        /// Sends each hunter back to their assigned cabin. Hunters without an
        /// assigned cabin (unemployed) are skipped.
        /// </summary>
        /// <summary>
        /// Adds every hunter to vanilla's multi-selection (InputManager.selectedObjs).
        /// The player can then right-click terrain to move them, or use our
        /// Rally/ReturnHome hotkeys on top. Vanilla's civilian click-to-move
        /// works once selected — no extra plumbing needed.
        /// </summary>
        private static void SelectAllHunters()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            var im = gm?.inputManager;
            if (im == null)
            {
                MelonLogger.Warning("[WotW] SelectAll: InputManager unavailable.");
                return;
            }

            int selected = 0;
            foreach (var hunter in EnumerateHunters())
            {
                var selectable = hunter.GetComponent<SelectableComponent>() as ISelectable
                    ?? hunter as ISelectable;
                if (selectable == null) continue;

                im.SelectSelectable(selectable);
                selected++;
            }

            MelonLogger.Msg($"[WotW] Select-all: {selected} hunter(s) added to selection.");
        }

        private static void ReturnAllHome()
        {
            int moved = 0;
            foreach (var hunter in EnumerateHunters())
            {
                var cabin = FindCabinForHunter(hunter);
                if (cabin == null) continue;

                // attackMove=false: hunter prefers to reach cabin, not engage
                CommandVillagerTo(hunter, cabin.transform.position, attackMove: false);
                moved++;
            }
            if (moved > 0)
                MelonLogger.Msg($"[WotW] Return home: {moved} hunter(s) recalled.");
        }

        private static int MoveAllHunters(Vector3 dest, string reason)
        {
            int moved = 0;
            foreach (var hunter in EnumerateHunters())
            {
                // attackMove=true: hunter engages enemies along the path + at dest
                CommandVillagerTo(hunter, dest, attackMove: true);
                moved++;
            }
            return moved;
        }

        /// <summary>
        /// Iterates every villager whose residence is a HunterBuilding.
        /// Uses FindObjectsOfType once — rally is a user-triggered hotkey,
        /// not a per-frame hot path, so the cost is fine.
        /// </summary>
        private static IEnumerable<Villager> EnumerateHunters()
        {
            foreach (var villager in UnityEngine.Object.FindObjectsOfType<Villager>())
            {
                if (villager == null) continue;
                if (villager.occupation is VillagerOccupationHunter)
                    yield return villager;
            }
        }

        /// <summary>
        /// Finds the hunter's assigned cabin via the residence property.
        /// Falls back to any HunterBuilding GameObject named after the villager's
        /// assignment, if available.
        /// </summary>
        private static Component FindCabinForHunter(Villager hunter)
        {
            try
            {
                if (hunter.residence is HunterBuilding hb) return hb;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Commands a villager to move to a destination via vanilla's
        /// OnCommandedToMove — the same path soldiers use when right-clicked.
        /// Unlike raw NavMeshAgent.SetDestination (which vanilla's task
        /// scheduler overrides every frame), this properly cancels the
        /// current task and routes movement through the combat/task system.
        ///
        /// attackMove=true → hunter engages enemies along the way (for rally)
        /// attackMove=false → straight move, prefer not to engage (for return-home)
        /// </summary>
        private static void CommandVillagerTo(
            Villager villager, Vector3 destination, bool attackMove)
        {
            try
            {
                villager.OnCommandedToMove(destination, attackMove);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] CommandVillagerTo: {ex.Message}");
            }
        }
    }
}
