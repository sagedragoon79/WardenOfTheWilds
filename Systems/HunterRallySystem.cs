using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Hunter select-all hotkey.
    ///
    /// Pressing the configured combo (default Ctrl+K) adds every hunter on the
    /// map to vanilla's multi-selection (InputManager.selectedObjs). Once
    /// selected, vanilla's civilian click-to-move handles movement — right-click
    /// terrain to move, right-click an enemy to attack.
    ///
    /// Prior versions tried to add a rally-to-cursor and return-home hotkey via
    /// Villager.OnCommandedToMove, but that method only routes to movement for
    /// Soldier-occupation villagers; for civilians it just sets
    /// stationaryIdlePosition with no actual navmesh command. Those two
    /// features were silently non-functional and have been removed.
    /// </summary>
    public static class HunterRallySystem
    {
        private static KeyCode _selectAllKey = KeyCode.K;
        private static KeyCode _selectAllModifier = KeyCode.LeftControl;
        private static bool _keysResolved = false;

        private static float _lastKeyResolve = 0f;
        private const float KeyResolveInterval = 5f;

        public static void Tick()
        {
            ResolveKeysIfStale();

            if (IsComboDown(_selectAllKey, _selectAllModifier))
                SelectAllHunters();
        }

        private static void ResolveKeysIfStale()
        {
            if (_keysResolved && Time.time - _lastKeyResolve < KeyResolveInterval)
                return;
            _keysResolved = true;
            _lastKeyResolve = Time.time;

            ParseBinding(WardenOfTheWildsMod.HunterSelectAllKeyName.Value,
                KeyCode.K, KeyCode.LeftControl,
                out _selectAllKey, out _selectAllModifier);
        }

        /// <summary>
        /// Parses strings like "Ctrl+K", "Alt+R", "K" into a key + optional
        /// modifier. Falls back to (fallbackKey, fallbackMod) if parsing fails.
        /// </summary>
        private static void ParseBinding(
            string raw, KeyCode fallbackKey, KeyCode fallbackMod,
            out KeyCode key, out KeyCode modifier)
        {
            key = fallbackKey;
            modifier = fallbackMod;
            if (string.IsNullOrWhiteSpace(raw)) return;

            string s = raw.Trim();
            if (s.Contains("+"))
            {
                var parts = s.Split('+');
                if (parts.Length == 2)
                {
                    modifier = ResolveModifier(parts[0].Trim());
                    key = ResolveSingleKey(parts[1].Trim(), fallbackKey);
                    return;
                }
            }

            modifier = KeyCode.None;
            key = ResolveSingleKey(s, fallbackKey);
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

            // Accept either left OR right variant of a modifier.
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

        /// <summary>
        /// Adds every hunter to vanilla's multi-selection (InputManager.selectedObjs).
        /// The player can then right-click terrain to move them, or right-click
        /// an enemy to attack. Vanilla's civilian click-to-move handles the rest.
        /// </summary>
        private static void SelectAllHunters()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            var im = gm?.inputManager;
            if (im == null) return;

            int selected = 0;
            foreach (var hunter in EnumerateHunters())
            {
                var selectable = hunter.GetComponent<SelectableComponent>() as ISelectable
                    ?? hunter as ISelectable;
                if (selectable == null) continue;

                im.SelectSelectable(selectable);
                selected++;
            }

            if (selected > 0)
                MelonLogger.Msg($"[WotW] Select-all: {selected} hunter(s) selected.");
        }

        private static IEnumerable<Villager> EnumerateHunters()
        {
            foreach (var villager in UnityEngine.Object.FindObjectsOfType<Villager>())
            {
                if (villager == null) continue;
                if (villager.occupation is VillagerOccupationHunter)
                    yield return villager;
            }
        }
    }
}
