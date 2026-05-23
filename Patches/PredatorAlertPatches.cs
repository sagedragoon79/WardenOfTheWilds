using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Phase 1 of the predator-alert overhaul (DLC + v1.1.2).
    ///
    /// Vanilla raises ONE generic "Predators are attacking!" blurb
    /// (uiAssetMap.predatorCombatBlurb) for every aggressive animal that
    /// targets a villager/building/pet/livestock — same red urgency whether
    /// it's a village-wrecking bear or a fox eyeing a chicken, or even a boar
    /// that's fleeing. All five raise paths funnel through the
    /// <c>PredatorCombatBlurbContainer</c> constructor, which receives the
    /// offending animal as <c>enemyUnit</c>.
    ///
    /// We prefix that constructor and swap the generic blurb for a per-animal
    /// clone that:
    ///   • shows the animal's own sprite (foxIcon/wolfIcon/bearIcon/boarIcon)
    ///     instead of the default bear icon, and
    ///   • carries a severity-tinted background:
    ///       Fox            → yellow  (harmless to humans; "spotted")
    ///       Boar (fleeing) → yellow  (running away, not a threat)
    ///       Boar (attacking) → red
    ///       Wolf / Bear    → red     (always a real threat)
    ///
    /// We intentionally do NOT change the BlurbType (stays Combat_Predator) so
    /// vanilla's consolidation/relevancy logic is untouched — only the visual
    /// (icon + color) changes via the substituted BlurbDefinition.
    ///
    /// Soft-fail throughout: any reflection/type miss falls back to the
    /// vanilla blurb (no swap), never throws into the UI pipeline.
    ///
    /// Phase 2 (boar "fleeing" as its own labeled state) and Phase 3
    /// (groundhog crop-infestation callout via cropWildlifeBlurb) are separate.
    /// </summary>
    internal static class PredatorAlertPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Color AttackRed   = new Color(0.65f, 0.12f, 0.12f, 1f);
        private static readonly Color SpottedAmber = new Color(0.80f, 0.62f, 0.15f, 1f);

        // Per-(animalKind, severity) cloned BlurbDefinition cache.
        private static readonly Dictionary<string, UnityEngine.Object> _clones =
            new Dictionary<string, UnityEngine.Object>();

        private static bool _registered;

        // Cached reflection handles.
        private static FieldInfo _iconField;          // BlurbDefinition.smallCriticalIcon
        private static PropertyInfo _entriesProp;      // BlurbDefinition.entries
        private static FieldInfo _bgColorField;        // BlurbDefinitionEntry.backgroundColor
        private static FieldInfo _entryIconField;      // BlurbDefinitionEntry.icon (the MAIN icon)

        public static void Register(HarmonyLib.Harmony harmony)
        {
            if (_registered) return;
            try
            {
                Type containerType = AccessTools.TypeByName("PredatorCombatBlurbContainer");
                if (containerType == null)
                {
                    MelonLogger.Msg(
                        "[WotW] PredatorAlertPatches: PredatorCombatBlurbContainer not found " +
                        "(pre-DLC build?). Skipping.");
                    return;
                }

                // The container has one constructor (8 params). Patch it.
                var ctor = AccessTools.GetDeclaredConstructors(containerType)?.Find(c => c.GetParameters().Length == 8)
                           ?? AccessTools.GetDeclaredConstructors(containerType)?[0];
                if (ctor == null)
                {
                    MelonLogger.Warning(
                        "[WotW] PredatorAlertPatches: no constructor found on " +
                        "PredatorCombatBlurbContainer.");
                    return;
                }

                // Prefix classifies (clean access to ctor args) and stashes the
                // chosen clone; postfix applies it to the constructed instance.
                // We can't just rewrite the ref arg in the prefix because a
                // derived-ctor prefix runs AFTER the base(...) call has already
                // consumed the original blurbDefinition.
                harmony.Patch(ctor,
                    prefix:  new HarmonyMethod(typeof(PredatorAlertPatches), nameof(CtorPrefix)),
                    postfix: new HarmonyMethod(typeof(PredatorAlertPatches), nameof(CtorPostfix)));

                // The blurb's persistent border/pulse color is baked into the
                // UICriticalBlurb prefab (defaultColor = pulseImage.color at
                // Awake), NOT driven by the blurb definition. So we also
                // postfix UICriticalBlurb.Init to recolor the live widget per
                // severity when it's rendering one of our predator clones.
                Type widgetType = AccessTools.TypeByName("UICriticalBlurb");
                if (widgetType != null)
                {
                    var postfix = new HarmonyMethod(typeof(PredatorAlertPatches), nameof(InitPostfix));
                    foreach (var m in AccessTools.GetDeclaredMethods(widgetType))
                        if (m.Name == "Init")
                        {
                            try { harmony.Patch(m, postfix: postfix); }
                            catch (Exception ex)
                            {
                                MelonLogger.Warning(
                                    $"[WotW] PredatorAlertPatches: Init patch ({m}): {ex.Message}");
                            }
                        }
                }

                _registered = true;
                MelonLogger.Msg(
                    "[WotW] PredatorAlertPatches: patched PredatorCombatBlurbContainer ctor + " +
                    "UICriticalBlurb.Init (per-animal icon + severity color).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PredatorAlertPatches.Register: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on the container ctor. Reads the offending animal from
        /// <paramref name="enemyUnit"/> and rewrites <paramref name="_blurbDefinition"/>
        /// (a ref arg consumed by the base ctor) to a per-animal clone.
        /// Only the two params we touch are declared — Harmony matches by name.
        /// </summary>
        // Handoff from prefix → postfix (ctors aren't reentrant on one thread).
        [ThreadStatic] private static UnityEngine.Object _pendingClone;

        public static void CtorPrefix(object enemyUnit, object _blurbType, object _blurbDefinition)
        {
            _pendingClone = null;
            try
            {
                if (!WardenOfTheWildsMod.PredatorAlertsEnabled.Value) return;
                if (enemyUnit == null || _blurbDefinition == null) return;

                // enemyUnit is the animal's combat/damageable component; its
                // gameObject is the AggressiveAnimal.
                var enemyComp = enemyUnit as Component;
                GameObject animalGo = enemyComp != null ? enemyComp.gameObject : null;
                if (animalGo == null) return;

                string kind = ClassifyAnimal(animalGo);   // "Fox"/"Wolf"/"Bear"/"Boar" or null
                if (kind == null) return;                  // unknown — leave vanilla blurb

                // Vanilla distinguishes the alert via BlurbType:
                //   CombatTargetSighted_Predator → a villager merely SPOTTED it
                //   Combat_Predator              → it's ENGAGING your stuff
                //     (raised by AggressiveAnimalTargeted{Villager,Building,
                //      Pet,Livestock} — so a fox going after a chicken/dog
                //      lands here = a real threat).
                bool sighted = (_blurbType?.ToString() == "CombatTargetSighted_Predator");

                bool lowSeverity = DetermineLowSeverity(kind, animalGo, sighted);

                _pendingClone = GetOrBuildClone(_blurbDefinition, kind, lowSeverity);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PredatorAlertPatches.CtorPrefix: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs after the base ctor finished. Overwrites the constructed
        /// container's blurbDefinition (+ selectedBlurbEntry, which the base
        /// ctor derived from the ORIGINAL blurb) with our clone, so the UI
        /// renders our icon + color. This is the step the old ref-arg prefix
        /// couldn't do — the base ctor had already consumed the original.
        /// </summary>
        public static void CtorPostfix(object __instance)
        {
            try
            {
                var clone = _pendingClone;
                _pendingClone = null;
                if (clone == null || __instance == null) return;

                Type t = __instance.GetType();

                var bdBacking = GetInheritedField(t, "<blurbDefinition>k__BackingField");
                bdBacking?.SetValue(__instance, clone);

                // selectedBlurbEntry was derived from the original blurb during
                // base ctor; repoint it at our clone's entry so color/text are
                // consistent. Color is uniform across our clone's entries.
                var entriesProp = clone.GetType().GetProperty("entries", AllInstance);
                var entries = entriesProp?.GetValue(clone) as System.Collections.IList;
                if (entries != null && entries.Count > 0)
                {
                    var sbeBacking = GetInheritedField(t, "<selectedBlurbEntry>k__BackingField");
                    sbeBacking?.SetValue(__instance, entries[0]);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PredatorAlertPatches.CtorPostfix: {ex.Message}");
            }
        }

        // Cached UICriticalBlurb private members for the color override.
        private static FieldInfo _wPulseImage, _wOutlineImage, _wDefaultColor, _wNoAlphaColor, _wBlurbContainer;
        private static bool _widgetReflectionTried;

        /// <summary>
        /// Postfix on UICriticalBlurb.Init. If the widget is rendering one of
        /// our predator clones (name starts with the clone prefix), recolor its
        /// border/pulse to the severity color baked into the clone name
        /// (":amber" / ":red"). The persistent color isn't blurb-driven, so we
        /// set it on the live widget here.
        /// </summary>
        public static void InitPostfix(object __instance, object _blurbContainer)
        {
            try
            {
                if (!WardenOfTheWildsMod.PredatorAlertsEnabled.Value) return;
                if (__instance == null) return;

                // Resolve the blurbDefinition.name from the container.
                object container = _blurbContainer;
                if (container == null) return;
                object bd = ReadMember(container, "blurbDefinition");
                string bdName = (bd as UnityEngine.Object)?.name;
                if (string.IsNullOrEmpty(bdName) || !bdName.StartsWith("WotW_PredatorBlurb_"))
                    return;   // not ours — leave vanilla coloring

                Color tint = bdName.EndsWith(":amber") ? SpottedAmber : AttackRed;

                EnsureWidgetReflection(__instance.GetType());

                // Recolor outline (border) — the most visible persistent element.
                var outline = _wOutlineImage?.GetValue(__instance) as Component;
                SetGraphicColor(outline, tint);

                // Recolor pulse glow + its lerp endpoints so the animation
                // stays on-severity. defaultColor = full, noAlpha = transparent.
                var pulse = _wPulseImage?.GetValue(__instance) as Component;
                SetGraphicColor(pulse, tint);
                _wDefaultColor?.SetValue(__instance, tint);
                var transparent = new Color(tint.r, tint.g, tint.b, 0f);
                _wNoAlphaColor?.SetValue(__instance, transparent);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PredatorAlertPatches.InitPostfix: {ex.Message}");
            }
        }

        private static void EnsureWidgetReflection(Type widgetType)
        {
            if (_widgetReflectionTried) return;
            _widgetReflectionTried = true;
            _wPulseImage     = GetInheritedField(widgetType, "pulseImage");
            _wOutlineImage   = GetInheritedField(widgetType, "outlineImage");
            _wDefaultColor   = GetInheritedField(widgetType, "defaultColor");
            _wNoAlphaColor   = GetInheritedField(widgetType, "noAlphaColor");
            _wBlurbContainer = GetInheritedField(widgetType, "blurbContainer");
        }

        /// <summary>Sets a UnityEngine.UI.Graphic/Image component's color.</summary>
        private static void SetGraphicColor(Component graphic, Color c)
        {
            if (graphic == null) return;
            try
            {
                var colorProp = graphic.GetType().GetProperty("color",
                    BindingFlags.Instance | BindingFlags.Public);
                colorProp?.SetValue(graphic, c);
            }
            catch { }
        }

        /// <summary>Returns "Fox"/"Wolf"/"Bear"/"Boar" if the GameObject has one
        /// of those animal components, else null.</summary>
        private static string ClassifyAnimal(GameObject go)
        {
            foreach (string kind in new[] { "Fox", "Wolf", "Bear", "Boar" })
            {
                Type t = AccessTools.TypeByName(kind);
                if (t != null && go.GetComponent(t) != null) return kind;
            }
            return null;
        }

        /// <summary>
        /// Decides amber (low-severity) vs red per animal + engagement state:
        ///   Wolf / Bear → always RED (always a real threat, per design)
        ///   Fox         → AMBER if merely spotted, RED if engaging
        ///                 (Combat_Predator = fox targeting a chicken/dog/etc.)
        ///   Boar        → AMBER if spotted OR currently fleeing, else RED
        /// </summary>
        private static bool DetermineLowSeverity(string kind, GameObject animalGo, bool sighted)
        {
            // Wolves and bears: always red, sighted or not.
            if (kind == "Wolf" || kind == "Bear") return false;

            // Fox: amber when only spotted; red when actually engaging
            // (a fox attacking your chickens/dog raises Combat_Predator).
            if (kind == "Fox") return sighted;

            // Boar: amber when spotted, OR when it's fleeing (retreating) —
            // a fleeing boar isn't a threat (the main annoyance). Red only
            // when it's actively engaging and NOT fleeing.
            if (kind == "Boar")
            {
                if (sighted) return true;
                if (IsRetreating(animalGo, "Boar")) return true;  // fleeing → amber
                return false;                                     // engaging → red
            }

            return false;
        }

        /// <summary>Reads the animal's damageableComp.isRetreating flag.</summary>
        private static bool IsRetreating(GameObject animalGo, string kind)
        {
            try
            {
                Type t = AccessTools.TypeByName(kind);
                var animalComp = t != null ? animalGo.GetComponent(t) : null;
                object dc = ReadMember(animalComp, "damageableComp");
                object retreating = ReadMember(dc, "isRetreating");
                return retreating is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns a cached clone of the source blurb for (kind, severity),
        /// building it on first request: animal sprite as the critical icon,
        /// severity color on every entry's background.
        /// </summary>
        private static UnityEngine.Object GetOrBuildClone(
            object sourceBlurb, string kind, bool lowSeverity)
        {
            string key = kind + (lowSeverity ? ":amber" : ":red");
            if (_clones.TryGetValue(key, out var cached) && cached != null)
                return cached;

            try
            {
                EnsureReflection(sourceBlurb.GetType());

                // Clone the SO so we don't mutate the shared vanilla blurb.
                var src = sourceBlurb as UnityEngine.Object;
                if (src == null) return null;
                var clone = UnityEngine.Object.Instantiate(src);
                clone.name = $"WotW_PredatorBlurb_{key}";
                UnityEngine.Object.DontDestroyOnLoad(clone);

                // Icon → animal sprite from uiAssetMap.
                Sprite icon = GetAnimalIcon(kind);

                // (a) smallCriticalIcon drives the small "mini" icon on the
                //     critical-blurb stack (top-left).
                if (icon != null && _iconField != null)
                    _iconField.SetValue(clone, icon);

                // (b) The MAIN blurb image reads selectedBlurbEntry.icon, NOT
                //     blurbDefinition.criticalIcon. So we must also overwrite
                //     each entry's own `icon` field — that's the big icon the
                //     "Predators are attacking" panel actually shows. (This was
                //     the missing piece: setting only smallCriticalIcon left
                //     the big icon at the vanilla default.)
                Color tint = lowSeverity ? SpottedAmber : AttackRed;
                if (_entriesProp != null)
                {
                    var entries = _entriesProp.GetValue(clone) as System.Collections.IList;
                    if (entries != null)
                        foreach (var e in entries)
                        {
                            if (e == null) continue;
                            if (icon != null && _entryIconField != null)
                                _entryIconField.SetValue(e, icon);
                            if (_bgColorField != null)
                                _bgColorField.SetValue(e, tint);
                        }
                }

                _clones[key] = clone;
                MelonLogger.Msg(
                    $"[WotW] PredatorAlert: built blurb clone '{key}' " +
                    $"(icon={icon != null}, color={(lowSeverity ? "amber" : "red")}).");
                return clone;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PredatorAlert.GetOrBuildClone({key}): {ex.Message}");
                return null;
            }
        }

        private static void EnsureReflection(Type blurbType)
        {
            if (_iconField == null)
                _iconField = GetInheritedField(blurbType, "smallCriticalIcon");
            if (_entriesProp == null)
                _entriesProp = blurbType.GetProperty("entries", AllInstance);

            if (_bgColorField == null || _entryIconField == null)
            {
                Type entryType = AccessTools.TypeByName("BlurbDefinitionEntry");
                if (entryType != null)
                {
                    if (_bgColorField == null)
                        _bgColorField = GetInheritedField(entryType, "backgroundColor");
                    if (_entryIconField == null)
                        _entryIconField = GetInheritedField(entryType, "icon");
                }
            }
        }

        private static Sprite GetAnimalIcon(string kind)
        {
            try
            {
                Type gaType = AccessTools.TypeByName("GlobalAssets");
                var uiMapProp = gaType?.GetProperty("uiAssetMap",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object uiMap = uiMapProp != null
                    ? uiMapProp.GetValue(null, null)
                    : gaType?.GetField("uiAssetMap",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (uiMap == null) return null;

                string fieldName = kind.ToLowerInvariant() + "Icon"; // foxIcon, wolfIcon, ...
                var f = uiMap.GetType().GetField(fieldName, AllInstance);
                return f?.GetValue(uiMap) as Sprite;
            }
            catch { return null; }
        }

        private static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                Type t = obj.GetType();
                while (t != null)
                {
                    var p = t.GetProperty(name, AllInstance);
                    if (p != null && p.CanRead) return p.GetValue(obj);
                    var f = t.GetField(name, AllInstance);
                    if (f != null) return f.GetValue(obj);
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        private static FieldInfo GetInheritedField(Type startType, string fieldName)
        {
            Type t = startType;
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
