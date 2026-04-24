using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WardenOfTheWilds.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  FishingModeSliderPatches
//  Injects a binary mode selector into the FishingShack info window.
//
//  Layout:
//
//      Angler  [●═══════════]  Creeler        (Angler selected)
//      Angler  [═══════════●]  Creeler        (Creeler selected)
//
//  - Clickable "Angler" label on the left, "Creeler" on the right.
//  - Track bar in between with a draggable-looking handle. Click either side
//    or the handle's target zone to toggle modes.
//  - Pre-tech: Creeler label is dimmed and clicks show the research notice
//    via enhancement.SetMode() (which handles the lock + notification).
//
//  Pattern cribbed from ManifestDelivery.ModeButtonPatches — UIBuildingInfoWindow_New
//  SetTargetData postfix, EventTrigger-based clicks, matching gold palette.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    internal static class FishingModeSliderPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const string RowName = "WotW_FishingModeSlider";

        // ── Palette (matches MD gold theme for visual consistency) ──────────
        private static readonly Color GoldBright = new Color(0.95f, 0.82f, 0.35f, 1f);
        private static readonly Color GoldMuted  = new Color(0.55f, 0.45f, 0.22f, 1f);
        private static readonly Color BgActive   = new Color(0.18f, 0.26f, 0.12f, 0.95f);
        private static readonly Color BgInactive = new Color(0.10f, 0.09f, 0.07f, 0.90f);
        private static readonly Color LockedTint = new Color(0.40f, 0.20f, 0.15f, 0.95f);

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                var windowType = Type.GetType("UIBuildingInfoWindow_New, Assembly-CSharp");
                if (windowType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        windowType = asm.GetType("UIBuildingInfoWindow_New");
                        if (windowType != null) break;
                    }
                }
                if (windowType == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] FishingModeSlider: UIBuildingInfoWindow_New not found.");
                    return;
                }

                var setTargetData = windowType.GetMethod("SetTargetData", AllInstance);
                if (setTargetData == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] FishingModeSlider: SetTargetData not found.");
                    return;
                }

                var postfix = typeof(FishingModeSliderPatches).GetMethod(
                    nameof(SetTargetDataPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(setTargetData, postfix: new HarmonyMethod(postfix));

                WardenOfTheWildsMod.Log.Msg(
                    "[WotW] FishingModeSlider: patched UIBuildingInfoWindow_New.SetTargetData");
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] FishingModeSlider register failed: {ex.Message}");
            }
        }

        private static void SetTargetDataPostfix(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                var buildingField = __instance.GetType().GetField("building", AllInstance);
                var building = buildingField?.GetValue(__instance) as Building;
                if (building == null)
                {
                    // Non-building or cleared window — kill any stray sliders
                    RemoveAllSliderRows();
                    return;
                }

                var enhancement = building.GetComponent<FishingShackEnhancement>();
                if (enhancement == null)
                {
                    // Not a fishing shack. Cheap global sweep so no row can
                    // survive on a pooled/alternate window instance and show
                    // up next to some other building's UI.
                    RemoveAllSliderRows();
                    return;
                }

                InjectSlider(comp, enhancement);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] FishingModeSlider postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Destroys every slider row in the scene, regardless of which window
        /// it's attached to. Safer than Transform.Find(RowName) because the
        /// game may use multiple/pooled info-window instances.
        /// </summary>
        private static void RemoveAllSliderRows()
        {
            var subs = UnityEngine.Object.FindObjectsOfType<SliderEventSubscriber>();
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i] != null)
                    UnityEngine.Object.Destroy(subs[i].gameObject);
            }
            HideTooltip();
        }

        /// <summary>
        /// Still used by InjectSlider to replace a row on its own window.
        /// Cheap (single Transform.Find) and correct for the same-window case.
        /// </summary>
        private static void RemoveRow(Transform root)
        {
            var existing = root.Find(RowName);
            if (existing != null)
                UnityEngine.Object.Destroy(existing.gameObject);
            HideTooltip();
        }

        // ── Slider construction ─────────────────────────────────────────────
        private static void InjectSlider(Component window, FishingShackEnhancement enh)
        {
            RemoveRow(window.transform);

            // Pull a TMP font from existing window text for consistent styling
            TMP_FontAsset gameFont = null;
            float gameFontSize = 14f;
            var existingText = window.GetComponentInChildren<TextMeshProUGUI>(true);
            if (existingText != null)
            {
                gameFont = existingText.font;
                gameFontSize = existingText.fontSize;
            }

            // Row container — upper area, absolute positioned so vanilla layout unaffected.
            // Sits below the building image, just above the HP bar.
            var row = new GameObject(RowName);
            row.transform.SetParent(window.transform, false);
            var rowRT = row.AddComponent<RectTransform>();
            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.ignoreLayout = true;
            rowRT.anchorMin = new Vector2(0.5f, 1f);
            rowRT.anchorMax = new Vector2(0.5f, 1f);
            rowRT.pivot = new Vector2(0.5f, 1f);
            rowRT.anchoredPosition = new Vector2(10f, -255f);
            rowRT.sizeDelta = new Vector2(450f, 48f);

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.padding = new RectOffset(4, 4, 2, 2);

            // -- Angler button --
            CreateSideLabel(row.transform, "Angler", FishingShackMode.Angler,
                enh, gameFont, gameFontSize, isLocked: false);

            // -- Creeler button (possibly locked) --
            bool creelerLocked = !WardenOfTheWildsMod.SustainableFishingResearched;
            CreateSideLabel(row.transform, "Creeler", FishingShackMode.Creeler,
                enh, gameFont, gameFontSize, isLocked: creelerLocked);

            // Event-driven subscriber: listens for mode changes on this shack
            // AND for the static "Sustainable Fishing researched" event. When
            // either fires, the slider is rebuilt. No polling.
            var subscriber = row.AddComponent<SliderEventSubscriber>();
            subscriber.Setup(window, enh);

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] FishingModeSlider: injected for '{window.gameObject.name}' " +
                $"(mode={enh.Mode}, creelerLocked={creelerLocked})");
        }

        /// <summary>
        /// Zero-polling subscriber. Hooks the two events that can make the
        /// slider out of date (mode change + tech research), and rebuilds
        /// only when one of them fires. OnDestroy unhooks cleanly.
        /// </summary>
        private class SliderEventSubscriber : MonoBehaviour
        {
            private Component _window;
            private FishingShackEnhancement _enh;
            private System.Action<FishingShackEnhancement> _modeHandler;
            private System.Action _techHandler;

            public void Setup(Component window, FishingShackEnhancement enh)
            {
                _window = window;
                _enh = enh;

                _modeHandler = OnModeChanged;
                _techHandler = OnTechResearched;

                FishingShackEnhancement.OnModeChanged += _modeHandler;
                WardenOfTheWildsMod.OnSustainableFishingResearched += _techHandler;
            }

            private void OnModeChanged(FishingShackEnhancement changed)
            {
                if (changed != _enh) return;
                if (!IsWindowStillShowingOurShack())
                {
                    // Stale — the info window we were injected into has since
                    // been retargeted to a different building. Self-destruct
                    // so we don't paint a slider onto an unrelated UI panel.
                    UnityEngine.Object.Destroy(gameObject);
                    return;
                }
                Rebuild();
            }

            private void OnTechResearched()
            {
                if (!IsWindowStillShowingOurShack())
                {
                    UnityEngine.Object.Destroy(gameObject);
                    return;
                }
                FishingShackEnhancement.RefreshAllShackRadii();
                Rebuild();
            }

            /// <summary>
            /// Confirms the info window we're attached to is STILL displaying
            /// our FishingShackEnhancement. Prevents cross-window leakage when
            /// the game retargets the info panel to another building.
            /// </summary>
            private bool IsWindowStillShowingOurShack()
            {
                if (_window == null || _enh == null) return false;
                try
                {
                    var bf = _window.GetType().GetField("building", AllInstance);
                    var currentBuilding = bf?.GetValue(_window) as Building;
                    if (currentBuilding == null) return false;
                    return currentBuilding.GetComponent<FishingShackEnhancement>() == _enh;
                }
                catch { return false; }
            }

            private void Rebuild()
            {
                if (_window == null || _enh == null) return;
                InjectSlider(_window, _enh);
            }

            private void OnDestroy()
            {
                if (_modeHandler != null)
                    FishingShackEnhancement.OnModeChanged -= _modeHandler;
                if (_techHandler != null)
                    WardenOfTheWildsMod.OnSustainableFishingResearched -= _techHandler;
                _modeHandler = null;
                _techHandler = null;
                _window = null;
                _enh = null;
            }
        }

        private static void CreateSideLabel(Transform parent, string label,
            FishingShackMode mode, FishingShackEnhancement enh,
            TMP_FontAsset font, float fontSize, bool isLocked)
        {
            bool isActive = enh.Mode == mode;

            var btnObj = new GameObject($"WotW_Side_{label}");
            btnObj.transform.SetParent(parent, false);

            // Border
            var borderImg = btnObj.AddComponent<Image>();
            borderImg.color = isLocked ? LockedTint : (isActive ? GoldBright : GoldMuted);
            borderImg.raycastTarget = true;

            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredHeight = 44f;
            le.preferredWidth = 205f;
            le.minWidth = 205f;

            // Inner background
            var innerObj = new GameObject("Inner");
            innerObj.transform.SetParent(btnObj.transform, false);
            var innerRT = innerObj.AddComponent<RectTransform>();
            innerRT.anchorMin = Vector2.zero;
            innerRT.anchorMax = Vector2.one;
            innerRT.offsetMin = new Vector2(2f, 2f);
            innerRT.offsetMax = new Vector2(-2f, -2f);
            var innerImg = innerObj.AddComponent<Image>();
            innerImg.color = isActive ? BgActive : BgInactive;
            innerImg.raycastTarget = false;

            // Label
            var textObj = new GameObject("Label");
            textObj.transform.SetParent(innerObj.transform, false);
            var textRT = textObj.AddComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero;
            textRT.offsetMax = Vector2.zero;

            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = fontSize * 1.10f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.text = isLocked ? $"{label} (locked)" : label;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = isLocked ? new Color(0.7f, 0.35f, 0.25f, 1f)
                                 : (isActive ? GoldBright : GoldMuted);
            tmp.raycastTarget = false;
            tmp.outlineWidth = 0.15f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);

            // Click handler
            var trigger = btnObj.AddComponent<EventTrigger>();
            var capturedParent = parent;
            var capturedEnh = enh;
            var capturedMode = mode;
            var capturedBtn = btnObj;

            var clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            clickEntry.callback.AddListener((data) =>
            {
                // SetMode() handles the tech gate; we just ask for the mode.
                capturedEnh.SetMode(capturedMode);
                RefreshSlider(capturedParent, capturedEnh);
            });
            trigger.triggers.Add(clickEntry);

            // Hover tooltip
            var enterEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enterEntry.callback.AddListener((data) =>
                ShowTooltip(capturedBtn.transform, GetTooltipText(capturedMode, isLocked), font));
            trigger.triggers.Add(enterEntry);

            var exitEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exitEntry.callback.AddListener((data) => HideTooltip());
            trigger.triggers.Add(exitEntry);
        }

        /// <summary>
        /// Re-injects the mode buttons to reflect current state.
        /// </summary>
        private static void RefreshSlider(Transform rowParent, FishingShackEnhancement enh)
        {
            // rowParent IS the row; walk up if caller passed a child
            var root = rowParent;
            while (root != null && root.name != RowName)
                root = root.parent;
            if (root == null) return;

            var window = root.parent?.GetComponent<Component>();
            if (window == null) return;

            InjectSlider(window, enh);
        }

        // ── Tooltips ────────────────────────────────────────────────────────
        private static GameObject _tooltipObj;
        private static TextMeshProUGUI _tooltipText;

        private static string GetTooltipText(FishingShackMode mode, bool isLocked)
        {
            // The tooltip splits on the first newline: everything before the
            // first \n becomes the outlined header, the rest is body text.
            switch (mode)
            {
                case FishingShackMode.Angler:
                    return "Angler\n" +
                           $"Catch multiplier: x{WardenOfTheWildsMod.AnglerCatchMult.Value:F2}\n" +
                           $"Timer reduction: x{WardenOfTheWildsMod.AnglerTimerReduction.Value:F2}\n" +
                           "<i>Two rod fishers. Fast cycles, bigger hauls.</i>";
                case FishingShackMode.Creeler:
                    if (isLocked)
                        return "Creeler (locked)\n" +
                               "<color=#d08040>Research <b>Sustainable Fishing</b> to unlock.</color>\n" +
                               $"Traps: every {WardenOfTheWildsMod.CrabTrapSpawnDays.Value}d, " +
                               $"{WardenOfTheWildsMod.CrabTrapFishPerSpawn.Value} fish/slot\n" +
                               "<i>Willow creels work year-round, even under ice.</i>";
                    return "Creeler\n" +
                           $"Traps: every {WardenOfTheWildsMod.CrabTrapSpawnDays.Value}d, " +
                           $"{WardenOfTheWildsMod.CrabTrapFishPerSpawn.Value} fish/slot\n" +
                           "<i>Willow creels work year-round, even under ice.</i>";
                default:
                    return mode.ToString();
            }
        }

        private static void ShowTooltip(Transform anchor, string text, TMP_FontAsset font)
        {
            HideTooltip();
            if (anchor == null) return;

            // Parent directly to the hovered button so positioning is
            // independent of the info window's rect layout. Tooltip floats
            // above the button with 5px gap.
            _tooltipObj = new GameObject("WotW_FishingSliderTooltip");
            _tooltipObj.transform.SetParent(anchor, false);
            _tooltipObj.transform.SetAsLastSibling();

            // Dedicated Canvas with high sortingOrder so the tooltip draws
            // above the info window and any other overlay UI. A Canvas needs
            // a GraphicRaycaster on the *root* canvas to handle events, but
            // the tooltip is display-only so we skip that — Images/TMP still
            // render fine via the parent canvas's camera.
            var tipCanvas = _tooltipObj.AddComponent<Canvas>();
            tipCanvas.overrideSorting = true;
            tipCanvas.sortingOrder = 32000;

            var tipRT = _tooltipObj.GetComponent<RectTransform>();
            if (tipRT == null) tipRT = _tooltipObj.AddComponent<RectTransform>();
            // Anchor to the top-center of the button; pivot at the bottom
            // center of the tooltip so anchoredPosition.y lifts it upward.
            tipRT.anchorMin = new Vector2(0.5f, 1f);
            tipRT.anchorMax = new Vector2(0.5f, 1f);
            tipRT.pivot = new Vector2(0.5f, 0f);
            tipRT.anchoredPosition = new Vector2(0f, 6f);
            tipRT.sizeDelta = new Vector2(360f, 150f);

            // Outer gold-ish border frame
            var border = _tooltipObj.AddComponent<Image>();
            border.color = new Color(0.55f, 0.45f, 0.22f, 1f);
            border.raycastTarget = false;

            // Inner dark background
            var innerObj = new GameObject("Inner");
            innerObj.transform.SetParent(_tooltipObj.transform, false);
            var innerRT = innerObj.AddComponent<RectTransform>();
            innerRT.anchorMin = Vector2.zero;
            innerRT.anchorMax = Vector2.one;
            innerRT.offsetMin = new Vector2(2f, 2f);
            innerRT.offsetMax = new Vector2(-2f, -2f);
            var innerImg = innerObj.AddComponent<Image>();
            innerImg.color = new Color(0.06f, 0.05f, 0.04f, 0.96f);
            innerImg.raycastTarget = false;

            // Content area uses VerticalLayoutGroup so header + body stack
            // cleanly, each sized to its own preferred height.
            var contentObj = new GameObject("Content");
            contentObj.transform.SetParent(innerObj.transform, false);
            var contentRT = contentObj.AddComponent<RectTransform>();
            contentRT.anchorMin = Vector2.zero;
            contentRT.anchorMax = Vector2.one;
            contentRT.offsetMin = new Vector2(10f, 8f);
            contentRT.offsetMax = new Vector2(-10f, -8f);

            var vlg = contentObj.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;

            // Split the text on first newline so header gets its own TMP
            // with outline + larger font, body gets a separate TMP without.
            string headerLine, bodyLines;
            int nlIdx = text.IndexOf('\n');
            if (nlIdx >= 0)
            {
                headerLine = text.Substring(0, nlIdx);
                bodyLines = text.Substring(nlIdx + 1);
            }
            else
            {
                headerLine = text;
                bodyLines = string.Empty;
            }

            // Header — bigger, bold, outlined for emphasis
            var headerObj = new GameObject("Header");
            headerObj.transform.SetParent(contentObj.transform, false);
            var headerTmp = headerObj.AddComponent<TextMeshProUGUI>();
            if (font != null) headerTmp.font = font;
            headerTmp.fontSize = 22f;
            headerTmp.fontStyle = FontStyles.Bold;
            headerTmp.color = new Color(1f, 0.92f, 0.60f, 1f); // warm gold for title
            headerTmp.alignment = TextAlignmentOptions.TopLeft;
            headerTmp.raycastTarget = false;
            headerTmp.enableAutoSizing = false;
            headerTmp.outlineWidth = 0.2f;
            headerTmp.outlineColor = new Color32(0, 0, 0, 230);
            headerTmp.text = headerLine;
            headerTmp.enableWordWrapping = false;

            // Body — smaller, no outline, softer color
            if (!string.IsNullOrEmpty(bodyLines))
            {
                var bodyObj = new GameObject("Body");
                bodyObj.transform.SetParent(contentObj.transform, false);
                _tooltipText = bodyObj.AddComponent<TextMeshProUGUI>();
                if (font != null) _tooltipText.font = font;
                _tooltipText.fontSize = 16f;
                _tooltipText.color = new Color(0.95f, 0.92f, 0.78f, 1f);
                _tooltipText.alignment = TextAlignmentOptions.TopLeft;
                _tooltipText.raycastTarget = false;
                _tooltipText.enableAutoSizing = false;
                _tooltipText.outlineWidth = 0f;
                _tooltipText.text = bodyLines;
                _tooltipText.enableWordWrapping = true;
            }
        }

        private static void HideTooltip()
        {
            if (_tooltipObj != null)
            {
                UnityEngine.Object.Destroy(_tooltipObj);
                _tooltipObj = null;
                _tooltipText = null;
            }
        }
    }
}
