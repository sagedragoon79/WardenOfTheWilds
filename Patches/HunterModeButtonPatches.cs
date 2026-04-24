using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WardenOfTheWilds.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  HunterModeButtonPatches
//
//  Injects "Big Game Hunter" and "Trap Master" mode buttons into the T2 hunter
//  building info window. Mirrors the FishingModeSliderPatches pattern.
//
//  Only active for T2 hunter buildings (tier >= 2). T1 shows nothing.
//
//  Clicking a button calls HunterCabinEnhancement.Path setter, which invokes
//  ApplyPath → worker count, radius, speed, traps, etc.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    internal static class HunterModeButtonPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const string RowName = "WotW_HunterModeButtons";

        // Match the fishing slider palette for a consistent look
        private static readonly Color GoldBright = new Color(0.95f, 0.82f, 0.35f, 1f);
        private static readonly Color GoldMuted  = new Color(0.55f, 0.45f, 0.22f, 1f);
        private static readonly Color BgActive   = new Color(0.18f, 0.26f, 0.12f, 0.95f);
        private static readonly Color BgInactive = new Color(0.10f, 0.09f, 0.07f, 0.90f);

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
                        "[WotW] HunterModeButton: UIBuildingInfoWindow_New not found.");
                    return;
                }

                var setTargetData = windowType.GetMethod("SetTargetData", AllInstance);
                if (setTargetData == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] HunterModeButton: SetTargetData not found.");
                    return;
                }

                var postfix = typeof(HunterModeButtonPatches).GetMethod(
                    nameof(SetTargetDataPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(setTargetData, postfix: new HarmonyMethod(postfix));

                WardenOfTheWildsMod.Log.Msg(
                    "[WotW] HunterModeButton: patched UIBuildingInfoWindow_New.SetTargetData");
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] HunterModeButton register failed: {ex.Message}");
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
                    RemoveAllButtonRows();
                    return;
                }

                var enhancement = building.GetComponent<HunterCabinEnhancement>();
                if (enhancement == null)
                {
                    RemoveAllButtonRows();
                    return;
                }

                // T1 gets no buttons — only T2 has the binary choice
                if (building.tier < 2)
                {
                    RemoveAllButtonRows();
                    return;
                }

                InjectButtons(comp, enhancement);
                SyncTrapSliderUI(comp, enhancement);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] HunterModeButton postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Hides the vanilla trap-count slider on T2 hunter windows. Mode is
        /// now button-driven (Big Game Hunter / Trap Master), so the slider
        /// is vestigial and potentially confusing. We walk the window's UI
        /// tree and SetActive(false) on every Slider's parent container.
        /// T2 hunter windows only have one slider (the trap slider), so this
        /// is safe.
        /// </summary>
        private static void HideTrapSlider(Component window)
        {
            try
            {
                // Slider is defined in UnityEngine.UI — resolve by name so we
                // don't need a hard compile-time reference if that assembly
                // isn't linked.
                var sliderType = Type.GetType("UnityEngine.UI.Slider, UnityEngine.UI");
                if (sliderType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        sliderType = asm.GetType("UnityEngine.UI.Slider");
                        if (sliderType != null) break;
                    }
                }
                if (sliderType == null) return;

                var sliders = window.GetComponentsInChildren(sliderType, includeInactive: true);
                if (sliders == null) return;

                foreach (var s in sliders)
                {
                    var sComp = s as Component;
                    if (sComp == null) continue;
                    // Hide the IMMEDIATE parent — usually the slider sits in a
                    // container that also holds its icon + value label. If we
                    // disable the slider GO itself, the icon/label remain
                    // visible orphaned.
                    var parent = sComp.transform.parent;
                    var target = parent != null ? parent.gameObject : sComp.gameObject;
                    if (target.activeSelf)
                    {
                        target.SetActive(false);
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW] HunterModeButton: hid trap slider container '{target.name}'");
                    }
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] HideTrapSlider failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Identifies the trap-count slider (by maxValue matching the building's
        /// maxDeployedTraps) and both (a) sets its value to the mode's target,
        /// so mode-switch updates the UI immediately, and (b) registers an
        /// onValueChanged listener that snaps it back if the player drags it.
        /// </summary>
        private static void SyncTrapSliderUI(Component window, HunterCabinEnhancement enh)
        {
            try
            {
                var hunterBuilding = enh.GetComponent<HunterBuilding>();
                if (hunterBuilding == null) return;
                int maxDeployed = hunterBuilding.maxDeployedTraps;
                if (maxDeployed <= 0) return;

                var sliderType = Type.GetType("UnityEngine.UI.Slider, UnityEngine.UI");
                if (sliderType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        sliderType = asm.GetType("UnityEngine.UI.Slider");
                        if (sliderType != null) break;
                    }
                }
                if (sliderType == null) return;

                var maxValueProp   = sliderType.GetProperty("maxValue");
                var valueProp      = sliderType.GetProperty("value");
                var onValueChanged = sliderType.GetProperty("onValueChanged");

                var sliders = window.GetComponentsInChildren(sliderType, includeInactive: true);
                if (sliders == null) return;

                // Normalise legacy Vanilla → HuntingLodge
                var effective = enh.Path == HunterT2Path.Vanilla
                    ? HunterT2Path.HuntingLodge
                    : enh.Path;
                int targetValue = effective == HunterT2Path.TrapperLodge ? maxDeployed : 0;

                foreach (var s in sliders)
                {
                    if (s == null) continue;
                    float max;
                    try { max = (float)maxValueProp.GetValue(s); }
                    catch { continue; }

                    // Identify the trap slider by its max matching the hunter's
                    // maxDeployedTraps (unique among sliders on this window).
                    if (Mathf.Abs(max - maxDeployed) > 0.01f) continue;

                    // Set slider value directly (UI state)
                    try { valueProp.SetValue(s, (float)targetValue); }
                    catch { }

                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] Trap slider UI synced to {targetValue} on '{window.gameObject.name}'");

                    // Hook onValueChanged so drags snap back visually
                    if (onValueChanged != null)
                    {
                        try
                        {
                            var ev = onValueChanged.GetValue(s);
                            if (ev != null)
                            {
                                // ev is UnityEvent<float> — use reflection to call
                                // AddListener(UnityAction<float>)
                                var addMethod = ev.GetType().GetMethod("AddListener");
                                if (addMethod != null)
                                {
                                    // Build a UnityAction<float> that clamps the slider
                                    var capturedSlider = s;
                                    var capturedMax = maxDeployed;
                                    var capturedEnh = enh;
                                    var capturedValProp = valueProp;

                                    UnityEngine.Events.UnityAction<float> listener = (float v) =>
                                    {
                                        try
                                        {
                                            var effPath = capturedEnh.Path == HunterT2Path.Vanilla
                                                ? HunterT2Path.HuntingLodge
                                                : capturedEnh.Path;
                                            int clampTarget = effPath == HunterT2Path.TrapperLodge
                                                ? capturedMax : 0;
                                            if (Mathf.Abs(v - clampTarget) > 0.01f)
                                            {
                                                capturedValProp.SetValue(capturedSlider,
                                                    (float)clampTarget);
                                            }
                                        }
                                        catch { }
                                    };

                                    addMethod.Invoke(ev, new object[] { listener });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            WardenOfTheWildsMod.Log.Warning(
                                $"[WotW] SyncTrapSliderUI: listener attach failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] SyncTrapSliderUI failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Destroys every hunter mode button row in the scene, regardless of
        /// which window it's attached to. Safe against UI pooling.
        /// </summary>
        private static void RemoveAllButtonRows()
        {
            var subs = UnityEngine.Object.FindObjectsOfType<HunterButtonSubscriber>();
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i] != null)
                    UnityEngine.Object.Destroy(subs[i].gameObject);
            }
        }

        private static void RemoveRow(Transform root)
        {
            var existing = root.Find(RowName);
            if (existing != null)
                UnityEngine.Object.Destroy(existing.gameObject);
        }

        private static void InjectButtons(Component window, HunterCabinEnhancement enh)
        {
            RemoveRow(window.transform);

            TMP_FontAsset gameFont = null;
            float gameFontSize = 14f;
            var existingText = window.GetComponentInChildren<TextMeshProUGUI>(true);
            if (existingText != null)
            {
                gameFont = existingText.font;
                gameFontSize = existingText.fontSize;
            }

            var row = new GameObject(RowName);
            row.transform.SetParent(window.transform, false);
            var rowRT = row.AddComponent<RectTransform>();
            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.ignoreLayout = true;
            // Positioned lower to fill the void where the vanilla trap
            // slider used to live. Wider than the fishing slider so the
            // button labels ("Big Game Hunter", "Trap Master") fit on one line.
            rowRT.anchorMin = new Vector2(0.5f, 1f);
            rowRT.anchorMax = new Vector2(0.5f, 1f);
            rowRT.pivot = new Vector2(0.5f, 1f);
            rowRT.anchoredPosition = new Vector2(10f, -255f);
            rowRT.sizeDelta = new Vector2(490f, 48f);

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.padding = new RectOffset(4, 4, 2, 2);

            // Normalise legacy Vanilla → BigGameHunter for button highlight purposes
            var effectivePath = enh.Path == HunterT2Path.Vanilla
                ? HunterT2Path.HuntingLodge
                : enh.Path;

            CreateButton(row.transform, "Big Game Hunter",
                HunterT2Path.HuntingLodge, enh, effectivePath, gameFont, gameFontSize);
            CreateButton(row.transform, "Trap Master",
                HunterT2Path.TrapperLodge, enh, effectivePath, gameFont, gameFontSize);

            // Event-driven refresh: rebuild when path changes on this shack
            var sub = row.AddComponent<HunterButtonSubscriber>();
            sub.Setup(window, enh);

            WardenOfTheWildsMod.Log.Msg(
                $"[WotW] HunterModeButton: injected for '{window.gameObject.name}' " +
                $"(path={effectivePath})");
        }

        private static void CreateButton(Transform parent, string label,
            HunterT2Path mode, HunterCabinEnhancement enh,
            HunterT2Path currentPath, TMP_FontAsset font, float fontSize)
        {
            bool isActive = currentPath == mode;

            var btnObj = new GameObject($"WotW_HunterBtn_{label.Replace(' ', '_')}");
            btnObj.transform.SetParent(parent, false);

            var borderImg = btnObj.AddComponent<Image>();
            borderImg.color = isActive ? GoldBright : GoldMuted;
            borderImg.raycastTarget = true;

            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredHeight = 44f;
            le.preferredWidth = 225f;
            le.minWidth = 225f;

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
            tmp.text = label;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = isActive ? GoldBright : GoldMuted;
            tmp.raycastTarget = false;
            tmp.outlineWidth = 0.15f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);

            var trigger = btnObj.AddComponent<EventTrigger>();
            var capturedEnh = enh;
            var capturedMode = mode;
            var capturedBtn = btnObj;

            var clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            clickEntry.callback.AddListener((data) =>
            {
                capturedEnh.Path = capturedMode;
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] Hunter mode button clicked: {capturedMode} on " +
                    $"{capturedEnh.gameObject.name}");
            });
            trigger.triggers.Add(clickEntry);

            var enterEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enterEntry.callback.AddListener((data) =>
                ShowTooltip(capturedBtn.transform, GetTooltipText(capturedMode), font));
            trigger.triggers.Add(enterEntry);

            var exitEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exitEntry.callback.AddListener((data) => HideTooltip());
            trigger.triggers.Add(exitEntry);
        }

        /// <summary>Subscribes to HunterCabinEnhancement path-change events
        /// and rebuilds the buttons so highlights update on click.</summary>
        private class HunterButtonSubscriber : MonoBehaviour
        {
            private Component _window;
            private HunterCabinEnhancement _enh;
            private Action<HunterCabinEnhancement> _pathHandler;

            public void Setup(Component window, HunterCabinEnhancement enh)
            {
                _window = window;
                _enh = enh;
                _pathHandler = OnPathChanged;
                HunterCabinEnhancement.OnPathChanged += _pathHandler;
            }

            private void OnPathChanged(HunterCabinEnhancement changed)
            {
                if (changed != _enh) return;
                if (!IsWindowStillShowingOurShack())
                {
                    UnityEngine.Object.Destroy(gameObject);
                    return;
                }
                InjectButtons(_window, _enh);
                SyncTrapSliderUI(_window, _enh);
            }

            private bool IsWindowStillShowingOurShack()
            {
                if (_window == null || _enh == null) return false;
                try
                {
                    var bf = _window.GetType().GetField("building", AllInstance);
                    var currentBuilding = bf?.GetValue(_window) as Building;
                    if (currentBuilding == null) return false;
                    return currentBuilding.GetComponent<HunterCabinEnhancement>() == _enh;
                }
                catch { return false; }
            }

            private void OnDestroy()
            {
                if (_pathHandler != null)
                    HunterCabinEnhancement.OnPathChanged -= _pathHandler;
                _pathHandler = null;
                _window = null;
                _enh = null;
            }
        }

        // ── Tooltip (reuses fishing slider pattern) ──────────────────────────
        private static GameObject _tooltipObj;
        private static TextMeshProUGUI _tooltipText;

        private static string GetTooltipText(HunterT2Path mode)
        {
            switch (mode)
            {
                case HunterT2Path.HuntingLodge:
                    return "Big Game Hunter\n" +
                           $"Radius: x{WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value:F1}\n" +
                           $"Speed: x{WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value:F2}\n" +
                           "<i>Bow hunter. No traps. Active pursuit, expanded range.</i>";
                case HunterT2Path.TrapperLodge:
                    return "Trap Master\n" +
                           $"Pelt multiplier: x{WardenOfTheWildsMod.TrapperLodgePeltMult.Value:F1}\n" +
                           $"Speed bonus: x{WardenOfTheWildsMod.TrapMasterSpeedMult.Value:F2}\n" +
                           "<i>Trap specialist. Max trapline, passive pelt income.</i>";
                default:
                    return mode.ToString();
            }
        }

        private static void ShowTooltip(Transform anchor, string text, TMP_FontAsset font)
        {
            HideTooltip();
            if (anchor == null) return;

            _tooltipObj = new GameObject("WotW_HunterButtonTooltip");
            _tooltipObj.transform.SetParent(anchor, false);
            _tooltipObj.transform.SetAsLastSibling();

            var tipCanvas = _tooltipObj.AddComponent<Canvas>();
            tipCanvas.overrideSorting = true;
            tipCanvas.sortingOrder = 32000;

            var tipRT = _tooltipObj.GetComponent<RectTransform>();
            if (tipRT == null) tipRT = _tooltipObj.AddComponent<RectTransform>();
            tipRT.anchorMin = new Vector2(0.5f, 1f);
            tipRT.anchorMax = new Vector2(0.5f, 1f);
            tipRT.pivot = new Vector2(0.5f, 0f);
            tipRT.anchoredPosition = new Vector2(0f, 6f);
            tipRT.sizeDelta = new Vector2(360f, 150f);

            var border = _tooltipObj.AddComponent<Image>();
            border.color = new Color(0.55f, 0.45f, 0.22f, 1f);
            border.raycastTarget = false;

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

            var headerObj = new GameObject("Header");
            headerObj.transform.SetParent(contentObj.transform, false);
            var headerTmp = headerObj.AddComponent<TextMeshProUGUI>();
            if (font != null) headerTmp.font = font;
            headerTmp.fontSize = 22f;
            headerTmp.fontStyle = FontStyles.Bold;
            headerTmp.color = new Color(1f, 0.92f, 0.60f, 1f);
            headerTmp.alignment = TextAlignmentOptions.TopLeft;
            headerTmp.raycastTarget = false;
            headerTmp.enableAutoSizing = false;
            headerTmp.outlineWidth = 0.2f;
            headerTmp.outlineColor = new Color32(0, 0, 0, 230);
            headerTmp.text = headerLine;
            headerTmp.enableWordWrapping = false;

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
