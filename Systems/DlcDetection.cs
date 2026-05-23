using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Soft DLC detection helper. Wraps the game's
    /// <c>DlcManagerSingleton.IsDlcOwned(DlcId.DLC_001)</c> with caching and
    /// graceful degradation:
    ///
    /// <list type="bullet">
    ///   <item>If the DLC system isn't present (theoretical, since FF ships
    ///         the code in base Assembly-CSharp), assume DLC NOT owned.</item>
    ///   <item>If the singleton hasn't initialized yet, return the cached
    ///         result or false. Mod patches that depend on DLC content should
    ///         re-query each combat tick / on demand — the cache rechecks
    ///         every few seconds without spamming Steam.</item>
    ///   <item>Reflection-based so this builds against pre-DLC and post-DLC
    ///         Assembly-CSharp identically. No hard reference to types that
    ///         might shift between game versions.</item>
    /// </list>
    ///
    /// The result is also gated by the user-facing pref
    /// <c>WardenOfTheWildsMod.PetsDlcFeaturesEnabled</c>, so a DLC owner can
    /// still opt out of WotW's DLC-specific patches without uninstalling.
    /// </summary>
    internal static class DlcDetection
    {
        // Steam appid for FF Pets DLC (DLC_001), per DlcManagerSingleton.
        // Hard-coded as a safety net — we read it from the enum at runtime,
        // but if reflection fails we fall back to this constant.
        public const uint PetsDlcSteamAppId = 4242820u;

        private static bool _resolvedOnce;
        private static bool _cachedOwned;
        private static float _cacheExpiry = -1f;
        private const float CacheTtlSeconds = 10f;

        private static Type _singletonType;
        private static PropertyInfo _instanceProp;
        private static MethodInfo   _isDlcOwnedMethod;
        private static object       _dlcIdEnumValue;     // boxed DlcId.DLC_001
        private static bool         _reflectionFailed;

        /// <summary>
        /// True if both: (a) the Pets DLC is owned per Steam, and (b) the
        /// user hasn't disabled WotW's DLC features via pref. False otherwise.
        ///
        /// Re-queries Steam at most once every <see cref="CacheTtlSeconds"/>;
        /// the pref check is free and not cached.
        ///
        /// Safe to call from any thread / lifecycle phase. Returns false
        /// before <c>OnInitializeMelon</c> finishes (pref is null).
        /// </summary>
        public static bool PetsDlcActive
        {
            get
            {
                try
                {
                    // Master pref kill-switch — defaults true, but a player
                    // who owns the DLC can opt out of WotW DLC features
                    // (e.g. while testing a vanilla-fidelity playthrough).
                    var pref = WardenOfTheWildsMod.PetsDlcFeaturesEnabled;
                    if (pref != null && !pref.Value) return false;

                    return IsPetsDlcOwned();
                }
                catch { return false; }
            }
        }

        // ── Pacifist (Disable Pests) detection ──────────────────────────────
        // FF v1.1.2 added a "Disable Pests" New Settlement option for a relaxed
        // ambient experience without the fox/groundhog gameplay loop. Internally
        // it's AnimalManager.pacifistFoxGroundhog (bool). When true, WotW must
        // NOT spawn wild foxes, roll fox/groundhog trap catches, retarget
        // hunters onto foxes, or auto-loot groundhog kills — doing so would
        // override the player's relaxed-mode choice. Cat/dog QoL features are
        // unaffected (pacifist mode is fox/groundhog-specific).
        private static PropertyInfo _pacifistProp;
        private static bool _pacifistResolveFailed;

        /// <summary>
        /// True only when DLC pest GAMEPLAY should run: DLC owned + WotW pref on
        /// + the settlement is NOT in "Disable Pests" (pacifist) mode. Gate all
        /// fox/groundhog gameplay features on this rather than PetsDlcActive.
        /// </summary>
        public static bool PestGameplayActive
        {
            get
            {
                if (!PetsDlcActive) return false;
                return !IsPacifistMode();
            }
        }

        /// <summary>
        /// Reads AnimalManager.pacifistFoxGroundhog via reflection. Returns
        /// false (pests active) if the property can't be resolved — so on an
        /// older build or resolution miss we default to the normal gameplay
        /// loop rather than silently disabling features.
        /// </summary>
        public static bool IsPacifistMode()
        {
            if (_pacifistResolveFailed) return false;
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var am = gm?.animalManager;
                if (am == null) return false;

                if (_pacifistProp == null)
                {
                    _pacifistProp = am.GetType().GetProperty("pacifistFoxGroundhog",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_pacifistProp == null)
                    {
                        // Fall back to a field if it's not a property on this build.
                        var f = am.GetType().GetField("_pacifistFoxGroundhog",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? am.GetType().GetField("pacifistFoxGroundhog",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) return f.GetValue(am) is bool fb && fb;

                        _pacifistResolveFailed = true;
                        MelonLogger.Msg(
                            "[WotW] DlcDetection: pacifistFoxGroundhog not found " +
                            "(pre-v1.1.2 build?) — treating pests as active.");
                        return false;
                    }
                }

                object val = _pacifistProp.GetValue(am);
                return val is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Raw Steam ownership check, ignoring the user pref. Useful for
        /// diagnostic logging where you want to distinguish "DLC not owned"
        /// from "DLC features disabled by user."
        /// </summary>
        public static bool IsPetsDlcOwned()
        {
            // Cache: re-check Steam at most every CacheTtlSeconds. Steam's
            // ownership state can shift if the player buys/refunds during
            // a session (rare), so we don't pin forever — but we don't
            // hammer the API on every patch invocation either.
            if (Time.unscaledTime < _cacheExpiry) return _cachedOwned;

            _cachedOwned   = QueryOwnership();
            _cacheExpiry   = Time.unscaledTime + CacheTtlSeconds;
            _resolvedOnce  = true;
            return _cachedOwned;
        }

        private static bool QueryOwnership()
        {
            if (_reflectionFailed) return false;

            try
            {
                EnsureReflectionResolved();
                if (_singletonType == null || _isDlcOwnedMethod == null) return false;

                object instance = _instanceProp != null
                    ? _instanceProp.GetValue(null, null)
                    : _singletonType.GetField("Instance",
                        BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

                if (instance == null) return false;

                object result = _isDlcOwnedMethod.Invoke(instance, new[] { _dlcIdEnumValue });
                return result is bool b && b;
            }
            catch (Exception ex)
            {
                if (!_reflectionFailed)
                {
                    _reflectionFailed = true;
                    MelonLogger.Warning(
                        $"[WotW] DlcDetection: reflection failed, treating DLC as " +
                        $"not owned. ({ex.GetType().Name}: {ex.Message})");
                }
                return false;
            }
        }

        private static void EnsureReflectionResolved()
        {
            if (_singletonType != null) return;

            // Locate DlcManagerSingleton in any loaded assembly. Game class
            // names don't tend to move but we don't want to crash if Crate
            // renames or relocates the type.
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType("DlcManagerSingleton");
                    if (t == null) continue;

                    _singletonType = t;
                    _instanceProp  = t.GetProperty("Instance",
                        BindingFlags.Static | BindingFlags.Public);

                    // IsDlcOwned(DlcId) — find by name + 1 arg
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "IsDlcOwned") continue;
                        if (m.GetParameters().Length != 1) continue;
                        _isDlcOwnedMethod = m;
                        break;
                    }

                    // Resolve the DlcId enum value for the Pets DLC. Crate
                    // names it DLC_001; we look it up by name to survive a
                    // future renaming as long as the constant key persists.
                    Type idType = t.GetNestedType("DlcId", BindingFlags.Public);
                    if (idType != null && idType.IsEnum)
                    {
                        // Prefer DLC_001 by name; fall back to first non-None.
                        string[] names = Enum.GetNames(idType);
                        string pick = null;
                        foreach (string n in names)
                            if (n.Equals("DLC_001", StringComparison.OrdinalIgnoreCase))
                            { pick = n; break; }

                        if (pick == null)
                            foreach (string n in names)
                                if (!n.Equals("None", StringComparison.OrdinalIgnoreCase))
                                { pick = n; break; }

                        if (pick != null)
                            _dlcIdEnumValue = Enum.Parse(idType, pick);
                    }
                    break;
                }
                catch { /* ill-behaved assembly — try the next */ }
            }
        }

        /// <summary>Diagnostic log. Call from OnSceneWasLoaded or similar.</summary>
        public static void LogStatus()
        {
            bool owned = IsPetsDlcOwned();
            bool pref  = WardenOfTheWildsMod.PetsDlcFeaturesEnabled?.Value ?? true;
            bool pacifist = IsPacifistMode();
            MelonLogger.Msg(
                $"[WotW] DLC detect — PetsDLC owned: {owned} | " +
                $"WotW features enabled: {pref} | " +
                $"Disable Pests (pacifist): {pacifist} | " +
                $"pest gameplay active: {(owned && pref && !pacifist)}");
        }
    }
}
