# Warden of the Wilds — Adversarial Performance Review (2026-06-19)

_Generated via an 8-dimension multi-agent review (33 agents); every finding independently verified against the call site + game decompile before inclusion._

10 confirmed findings collapse to **7 distinct issues** after dedup. The honest headline: **there are no Critical issues, and only one finding rises to High.** The mod's combat hot paths are already disciplined (cheap early-outs, `Count == 0` guards, mostly-cached reflection). The real costs are (a) one genuinely per-frame uncached reflection call on hunting villagers, and (b) a cluster of dead/redundant work in the combat-only kite loop that is cheap in absolute terms but sloppy and trivially fixable.

---

## Deduplication map

| Distinct issue | Merged source findings |
|---|---|
| **A.** Smokehouse `Update()` — uncached `GetComponent` + no feature-flag gate | findings #1, #2, #8, #9 (all the same `SmokehouseEnhancement.Update()` at L101-112) |
| **B.** `IsHunterUnderEquipped` uncached `GetMethod`+`Invoke` per frame per hunting villager | finding #3 |
| **C.** `GetEffectiveReloadSeconds` dead uncached reflection hierarchy walk | findings #5, #10 (same method, `AnimalBehaviorSystem.cs:534-571`) |
| **D.** `UpdateActiveKites`/`UpdateActiveTaunts` run N× per tick + `FindHunterByKey` nested scan | finding #4 |
| **E.** `UpdateActiveKites` per-tick `new List<int>()` allocation | finding #7 |
| **F.** `GetTargetHpFraction` uncached `GetProperty` per attack-decision tick | finding #6 |
| **G.** `DogLeashEnforcementLoop` per-cabin coroutine, uncached `GetProperty` per worker | finding #8 (dog leash) |
| **H.** `eventLabel` string interpolated per hunter hit, used only by rate-limited log | finding #10 (eventLabel) |

(D and E are the same method `UpdateActiveKites` but distinct root causes — redundant N× invocation vs. per-call allocation — so kept separate with a shared fix note.)

---

## CRITICAL
None. No per-frame-per-building work with meaningful per-call cost, and no scene-wide scans on a tick. The historical `FindObjectsOfType`-every-tick stutter is gone and did not reappear anywhere.

---

## HIGH

### B. `IsHunterUnderEquipped` — uncached `GetMethod` + reflective `Invoke` every frame, per hunting villager
**File:** `Patches/HunterEquipmentGatePatches.cs:258-268` (call site `HunterCombatPatches.cs:2726`)
**Frequency (verified):** Once **per frame** per actively-hunting villager. `HuntSubTask` never sets a positive `timeBetweenUpdates`, so `TaskProcessorComponent.Update`'s `timeBetweenUpdates <= 0f` gate is always true and `IsSubTaskValidToContinue` fires every frame. This call sits **before** the 15s proactive rate-limit, so that limit does not protect it. `HunterRequiresBowForWork` defaults to `true` (`Plugin.cs:1007`), so the bow branch is always taken.
**Cost × multiplier:** `typeof(ItemStorage).GetMethod("GetItemCount", new[]{ _rangedWeaponItems.GetType() })` re-walks the type's method table every call, then `Invoke` allocates a fresh `object[]` arg array and boxes the `uint` return. At 60fps × N hunting villagers (6-12 late game) = hundreds of method-table walks + GC allocations/sec, sustained the entire time hunters are out hunting (not just in combat).
**This is the one finding where the per-frame cadence, the building/villager multiplier, and the per-call allocation all stack.** It is the clear top priority.
**Fix:** Resolve the `GetItemCount(List<Item>)` `MethodInfo` once inside the existing `if (!_itemRefsResolved)` block (alongside the already-cached `_arrowItem`/`_rangedWeaponItems`). Best: bind a typed delegate once via `Delegate.CreateDelegate` (`Func<ItemStorage, List<Item>, uint>`) — eliminates the `GetMethod` walk, the `object[]` alloc, and the boxing entirely. Both the target type and arg type are session-constant; single-threaded main-thread path, so a cached arg buffer is also safe if you keep `Invoke`.

---

## MEDIUM

### F. `GetTargetHpFraction` — uncached `GetProperty` chain per attack-decision tick
**File:** `Patches/HunterCombatPatches.cs:1514-1538`
**Frequency (verified):** Per task-update tick (dozens/sec per hunter) for every **T1 hunter in active melee combat** where the target supports both ranged and melee. Driven through `AttackSubTask.OnArrivedUpdate` → `UpdateAttacking` (returns `Running`, re-driven every tick) → `isMeleeAttack` getter → `OnIsMeleeAttack` delegate → mod postfix. Author's own throttle comment (L1349-1350) independently confirms "dozens per second."
**Cost × multiplier:** Up to 5 `Type.GetProperty` member-table walks (`lifePercentage`/`healthPercentage`/`lifeFraction`/`life`/`maxLife`) + a `PropertyType` check + `Convert.ToSingle` per call, uncached, scaling with concurrent T1 hunters in a fight. Mono's `GetProperty` is meaningfully costlier than CoreCLR. Combat-only (not steady state), which is why it's Medium not High.
**Fix:** Memoize a `static Dictionary<Type,(PropertyInfo perc, PropertyInfo life, PropertyInfo maxLife, bool percIsFloat)>` keyed by target `Type` (target type genuinely varies: Wolf/Bear/Boar/Deer/raider). This is the file's own idiom — see `GetHunterHealthFraction` at L1933-1951 (`_healthPropSearchDone` + cached `_villagerHealthProp`). **Cache the negative result too** (store entries even when props are null) so missing-property target types don't re-run the full chain every tick.

### C. `GetEffectiveReloadSeconds` — dead uncached reflection hierarchy walk (combat path)
**File:** `Systems/AnimalBehaviorSystem.cs:534-571`
**Frequency (verified):** Combat-only, bounded by active-kite count (author note: typically 0-2). Called from `ApplyKiteStep` (per active kite per HuntSubTask tick via `UpdateActiveKites`) and twice per shot from `ApplyPostShotRetreat` (L708, L1145 retreat paths). Zero cost when no kite active (`_kiteEndTime.Count == 0` early-out).
**Cost:** Walks the hunter type hierarchy (Villager → Character → CEMonoBehaviour, breaks at MonoBehaviour = 3 levels) × 8 candidate `GetField` lookups = ~24 reflective lookups per call. **I verified directly (L528-532) that none of the 8 candidate names exist on `Villager`/`Character`** — the real crossbow state lives on `CombatComponent.rangedWeaponData.useCrossbowAnims`. So the scan **always** falls through and returns `BowReloadSeconds.Value`. It is pure dead weight producing zero value.
**Fix:** **Option (a) — delete the reflection loop and return `WardenOfTheWildsMod.BowReloadSeconds.Value` directly.** This is behavior-preserving (the scan provably never matches today) and removes ~24 `GetField` calls per call for free. If genuine crossbow-aware reload is wanted later, resolve `CombatComponent.rangedWeaponData.useCrossbowAnims` once and cache it — do **not** revive the dead candidate-name scan (caching it would just cache a permanent null, i.e. a roundabout option (a)).

### D. `UpdateActiveKites` / `UpdateActiveTaunts` invoked N× per tick + `FindHunterByKey` nested scan
**File:** `Patches/HunterCombatPatches.cs:2687-2691`; `UpdateActiveKites` at 2371-2404; `FindHunterByKey` at 2407
**Frequency (verified):** ~1 Hz per hunting villager (`HuntSubTask`/`WanderRandomlySubTask` inherit `SubTask`'s default `timeBetweenUpdates = 1f` — not per-frame, correcting the mod's own "0.2s" comment). Both calls are unconditional at the top of the postfix, **before any early-out**, so global maintenance runs N times per tick (N = hunting villagers) doing identical global work N-1 times redundantly.
**Cost:** Both have correct `Count == 0` guards (free when idle). During combat, per active kite, `FindHunterByKey` does an un-indexed nested `O(buildings × workers)` scan of `_buildingWorkers` to reverse-resolve a hunter from a hash, plus `ApplyKiteStep` + `EngageHuntersDog`. Worst case N × K × scan per second — but N (hunters in a live fight) and K (active kites, 0-2) are both small, so absolute impact is low; flagged Medium for the redundant multiplier + the un-indexed scan being avoidable.
**Fix:** (1) Frame-gate both maintenance calls: `if (_kiteTickFrame == Time.frameCount) return; _kiteTickFrame = Time.frameCount;` — collapses the N same-frame invocations to one; work is global and idempotent, zero behavior change. (2) Eliminate `FindHunterByKey` entirely by storing the hunter `Component` directly in a parallel `Dictionary<int,Component>` populated in `ApplyPostShotRetreat`, removing the only reason this path walks `_buildingWorkers`.

---

## LOW

### A. Smokehouse `Update()` — uncached `GetComponent` + no feature-flag gate *(4 findings merged)*
**File:** `Components/SmokehouseEnhancement.cs:101-112` (the `GetComponent` at L105)
**Frequency (verified):** Once per frame per smokehouse, unconditionally — `Update()` has **no early-out at all**. With 6-12 smokehouses late game, ~360-720 `GetComponent<SelectableComponent>()` calls/sec. There is no `_cachedSelectable` field here (only `_lastSelected` at L48); the `GetComponent` at L517 is in setup-only `UpdateWorkAreaCircle`, not hot.
**Why Low:** `GetComponent<T>` is a single GameObject's component-list walk (microseconds), not a scene scan. Real, building-count-multiplied waste, but small absolute cost.
**Inconsistency with codebase:** Both siblings cache this exact pattern — `HunterCabinEnhancement:1583-1584` (with the literal comment "GetComponent on every frame across many shacks adds up") and `FishingShackEnhancement:1247-1248`. `FishingShack` also gates on `!FishingOverhaulEnabled.Value` first. Smokehouse missed both.
**Fix:** Add `private SelectableComponent? _cachedSelectable;` and resolve once (`if (_cachedSelectable == null) _cachedSelectable = GetComponent<SelectableComponent>();`). **Bigger win:** also add `if (!WardenOfTheWildsMod.SmokehouseOverhaulEnabled.Value || _workArea == null) return;` at the top — `_workArea` is only ever assigned in `UpdateWorkAreaCircle` (which only runs when overhaul+radius are on), so a null `_workArea` means `SetEnabled` is a guaranteed no-op. This skips the per-frame work entirely when the feature is off. (Unity's overloaded `==` handles destroyed-object null-checks correctly.)

### G. `DogLeashEnforcementLoop` — per-cabin coroutine, uncached `GetProperty` per worker
**File:** `Components/HunterCabinEnhancement.cs:169, 644-709`
**Frequency (verified):** One coroutine per cabin, ticking every **2 wall-seconds**. The reflection body runs **only for Pets-DLC owners** with leash on (default true) — for non-DLC users (likely the majority) it's two cheap bool checks every 2s, an idle no-op. The `PetsDlcActive` gate is itself cheap (TTL-cached bool). For DLC owners: ~12-36 `GetProperty("defenders")`-by-name calls every 2s (cabins have 1-3 workers each).
**Why Low:** 2s cadence, not per-frame; small worker counts; fully gated behind DLC ownership. Tens of microseconds per tick, not a frame-budget concern. The expensive `RecallDogToCabin` reflection (L745) only fires when a dog is actually out of bounds — a rare conditional, not guaranteed per-tick.
**Fix:** Cache the `VillagerOccupationHunter` `Type` and the `defenders` `PropertyInfo` in static fields (resolve once on first success). Replace the `occType.Name == "VillagerOccupationHunter"` string compare with cached `Type` reference equality (`occType == _hunterOccType`) — otherwise the string compare still runs per worker per tick. Optional: consolidate the N per-cabin 2s coroutines into one static loop iterating leash-enabled cabins, but the Type/PropertyInfo caching captures most of the benefit given the cadence.

### E. `UpdateActiveKites` — per-tick `new List<int>()` during combat
**File:** `Patches/HunterCombatPatches.cs:2375`
**Frequency (verified):** ~1 Hz per hunting villager, **only when ≥1 kite is active** (`Count == 0` early-out at L2373). Confirmed the alloc by reading the line directly.
**Why Low (downgraded from High):** ~1 small empty `List<int>` per call (~40-byte header; backing array not allocated until first `.Add`, which only happens on kite expiry). A few tiny Gen0 allocs/sec during combat windows only — an order of magnitude smaller than the historical `FindObjectsOfType` stutter.
**Inconsistency:** The sibling `HunterDogCombatSystem.UpdateActiveTaunts` (called one line later, L2691) was deliberately rewritten to use a static `_expiredScratch` list ("allocates nothing per tick", L70). `UpdateActiveKites` was simply missed.
**Fix:** `private static readonly List<int> _kiteExpiredScratch = new();` then `_kiteExpiredScratch.Clear();` at the top, mirroring `_expiredScratch`. Single call site, main-thread, non-reentrant — safe. (Fold into the D fix; same method.)

### H. `eventLabel` interpolated per hunter hit, used only by a rate-limited log
**File:** `Patches/HunterCombatPatches.cs:1000-1001` (consumed at L1180)
**Frequency (verified):** Per combat-event — `OnAttackedPostfix` on `Villager.OnAttacked`, fired from `CombatComponent.ReactToBeingAttacked` on every damage/dodge/block. Bursty in fights, but combat-only, never idle. Allocated only after the L989/L990/L998 early-outs, i.e. only for a qualifying hunter hit from a dangerous animal.
**Why Low:** A ~15-char `$"hit ({damageAmount:F1} dmg)"` built and discarded on the common no-retreat path (`RecordEngagementAndCheckRetreat` returns at L1121/L1126 before touching `eventLabel`; it's used only in the L1180 `MelonLogger.Msg`, gated on `shouldRetreat` + `KiteRateLimit`). One tiny string per qualifying hit. Flagged for completeness, not a stutter source.
**Fix:** Pass a constant label (`"hit"`) and move the `damageAmount:F1` formatting into the rate-limited log itself (`damageAmount` is already a parameter, in scope at L1180). Best folded into any other edit to this method. Check the aggro call site that also passes an `eventLabel` for the same pattern.

---

## Cleared / not an issue (coverage confirmation)

- **Scene-wide scans on a tick** — none. All `FindObjectsOfType`/registry scans run at map load or are guarded by `Count == 0`. The documented day-tick `WaitForSeconds`+`FindObjectsOfType` stutter is fully migrated to event-driven and did not recur.
- **`FishingShackEnhancement.Update` / `HunterCabinEnhancement.Update`** — already correct: both cache `_cachedSelectable` and `FishingShack` early-outs on its feature flag. They are the *reference pattern* Smokehouse should copy.
- **Most reflection in `HunterCombatPatches`** — already cached behind "search done" flags (`_villagerHealthProp`/`_healthValueProp`, `_ammoMissingField`, `_forceRetreatField`+`_forceRetreatFieldDone`, `_huntSubFieldSearchDone`). The gaps (B, C, F) are the exceptions, and they're flagged above.
- **`UpdateActiveTaunts`** — already allocation-free (static `_expiredScratch`); its only issue is the shared N× redundant invocation (D), fixed by the frame-gate.
- **One-time setup reflection** (`Building.Awake`/`Load`/`OnSceneWasInitialized`, `InitializeDelayed`, `UpdateWorkAreaCircle`) — runs once per building at attach; not a perf concern even uncached. Correctly excluded.
- **`RecallDogToCabin` reflection** (`HunterCabinEnhancement.cs:745`) — uncached but only fires when a dog is actually out of bounds (rare conditional), not guaranteed per-tick. Not worth a fix on its own.

---

## Recommended order of work
1. **B** (only High — per-frame uncached reflection + alloc on hunting villagers). Biggest real win.
2. **C** (delete dead scan — zero-risk, free).
3. **F** (Type-keyed PropertyInfo cache — combat tick reflection).
4. **D + E together** (same method: frame-gate + drop `FindHunterByKey` + static scratch list).
5. **A** (Smokehouse — copy the sibling pattern; cheap and removes a codebase inconsistency).
6. **G, H** (tidiness/GC polish; fold into adjacent edits).

Relevant files:
- `C:\Users\saged\source\repos\WardenOfTheWilds\Patches\HunterEquipmentGatePatches.cs`
- `C:\Users\saged\source\repos\WardenOfTheWilds\Patches\HunterCombatPatches.cs`
- `C:\Users\saged\source\repos\WardenOfTheWilds\Systems\AnimalBehaviorSystem.cs`
- `C:\Users\saged\source\repos\WardenOfTheWilds\Components\SmokehouseEnhancement.cs`
- `C:\Users\saged\source\repos\WardenOfTheWilds\Components\HunterCabinEnhancement.cs`