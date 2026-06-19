# WotW Session Handoff — Trap-cost zeroing silently disables trapping (vanilla divide-by-zero)

## ✅ RESOLVED 2026-06-19 (v1.0.20)
Fixed via **Option 3**: `ZeroTrapRecipeCosts` → `ReduceTrapRecipeCosts`, which floors the
ItemIron trap cost at **1** (`SafeTrapIronCost`) instead of 0 — a valid divisor that keeps a
discount (vanilla 2 → 1) while satisfying the divide / CommitToWorkOrder / request-capacity
invariants. Self-healing: repairs a recipe SO a prior buggy zeroing left at 0 this session.
Verified in-game: traps craft and deploy again. The original analysis below is retained for record.

---

## Headline

**`ZeroTrapRecipeCosts` breaks trap deployment on EVERY map.** Setting the trap recipe's
`ItemIron` count to `0` (while leaving the entry in the recipe) triggers an unguarded
integer **divide-by-zero in vanilla FF's manufacturing code**, so the cabin can never
craft an `ItemAnimalTrap`, so no trap is ever placed — Trapper/Hunting-Lodge mode produces
zero traps whenever the zeroing has applied. This was found while debugging an unrelated
Rivers Restored "no deer across the river" issue; the trap symptom was initially confounded
with a path-to-town gate, but it is a **standalone, all-maps WotW bug.**

## How it surfaced

A user (RR + WotW) saw trapper-mode cabins place **no traps** on a river-isolated region
despite the green placement chevron showing "good trapping here." Two faults were confounded:
1. **FF path-to-town gate** (vanilla): trap points must pass `DoesGeneralPathExistToTown(cell, WallsBlock)` — a river-cut region with no town path yields zero valid trap points. (This is the RR-river interaction, not WotW's fault.)
2. **WotW zero-cost bug** (this handoff): even on a good-path map, zeroing the iron cost prevents the trap *item* from ever being manufactured.

The tell: in a later game, traps deployed fine **before** the zeroing applied (cost still 2 iron),
and the user noted the zeroing "needs a reload" to take effect — consistent with WotW's
coroutine-deferred application.

## Root cause (confirmed via decompile)

Traps are a **two-stage item**:
1. The hunter building **manufactures** an `ItemAnimalTrap` from a `ManufactureDefinition`
   whose only `sourceItem` is `ItemIron`.
2. Deployment **seeks the pre-built `ItemAnimalTrap`** (not iron) and instantiates the prefab
   (`SetTrapSearchEntry.ProcessNewTask` → `StartSeekItemsTaskSearch(..., itemAnimalTrap, ...)`,
   decomp `ff_full_dlc.cs:209346`; placement at `199716/199719`).

The iron cost gates **stage 1 only**. WotW sets `_numSourceItemsNeeded = 0` on the `ItemIron`
`SourceItemDefinition` but **leaves the entry in the `sourceItems` list**
([Components/HunterCabinEnhancement.cs:329](../Components/HunterCabinEnhancement.cs) — `qtyField.SetValue(srcDef, 0)`).
That is the one state vanilla can't handle. Three mutually-reinforcing stage-1 failures, any one fatal:

- **PRIMARY — DivideByZeroException.** `Building.CanProduceManufactureDefinition` runs
  `int b = GetNumberOfUnreservedItems(sourceItem.item) / sourceItem.numSourceItemsNeeded;`
  with **no zero-guard** (`ff_full_dlc.cs:334152`). `numSourceItemsNeeded == 0` → throws.
  Reached on routine work-scanning: `GetBestManufactureDefToWorkOn (333760)` →
  `GetNextAlternatingManufactureDef (333835)` → `CanSeekItemsForDefinition (333829)` →
  `CanProduceManufactureDefinition (333838)`. No try/catch in that path. `HunterBuilding`
  (`Residence : Building`, `345135`) does **not** override these — it runs the buggy base method.
- **CommitToWorkOrder invariant.** Requires `num == manuDef.sourceItems.Count` where `num`
  counts non-empty detached bundles (`333982` / `333977`). A 0-qty reservation detaches an
  empty bundle → `num` stays 0 while `sourceItems.Count == 1` → no work order; logs
  `"CommitToWorkOrder sourceItemsRemoved: 0 does not equal the expected count of: 1"`.
- **HunterBuilding.GetSourceItemRequestCapacity** returns `Min(capacity, numSourceItemsNeeded * …) = Min(cap, 0) = 0`
  (`345486`) → cabin requests zero iron hauled for trap production.

Net: **no `ItemAnimalTrap` is ever crafted → the deploy seek finds an empty `HasItemAnimalTrap`
bucket → `StartSeekItemsTaskSearch` fails immediately (`382801-808`, `SearchFailed`) → no trap placed.**

## Why this matters / cost-benefit

Per decompile, traps are a **one-time, refundable, 48-month-lifespan** structure
(`numMonthsBeforeWornOut = 48`, `ff_full_dlc.cs:145146`; iron consumed once at
`SetTrapSubTask.OnArrivedEnd` `199716`; **fully refunded** on collect / move / over-cap via
`CollectTrapsToStorage` `346125-141`). So zeroing the 2-iron cost buys almost nothing in
normal play, while silently disabling trapping entirely. **Strongly consider dropping the
zeroing feature outright.**

## Fix options (pick one)

1. **Drop the feature** — stop zeroing the trap iron cost. Cleanest; the economy benefit is negligible.
2. **Remove the entry instead of zeroing** — in the loop at
   [HunterCabinEnhancement.cs:315-335](../Components/HunterCabinEnhancement.cs), call
   `sourceList.Remove(srcDef)` for the `ItemIron` entry rather than `SetValue(..., 0)`
   (collect to a temp list while iterating to avoid mutation-during-enumeration). Then
   `sourceItems.Count == 0` → divide loop never iterates, `CommitToWorkOrder` invariant is
   `0 == 0`, capacity never queried. **Verify the trap/recipe UI widgets tolerate an empty
   `sourceItems`** (decomp UI refs ~`239586/239687`) before shipping — a recipe with no
   inputs is an unusual state for the cost-display panels.
3. **Set the cost to 1, not 0** — keeps division valid and the all-bundles-detached invariant
   satisfiable. Not a true zero, but a trivial, working drain. Lowest-risk if (2)'s UI check is uncertain.

Apply the chosen fix in the same one-shot `ZeroTrapRecipeCosts` path, and re-verify after the
per-map `_trapRecipeZeroed` reset (`OnMapLoaded`, ~`HunterCabinEnhancement.cs:142-148`).
Note the recipe SO is **shared**, so the mutation persists for the session — a removed entry
stays removed until the SO is reloaded.

## Verification test

On a **good-path** map (vanilla lodge with default cost deploys traps — confirms path gate OK):
1. Baseline (zeroing disabled): cabin crafts `ItemAnimalTrap`, traps deploy.
2. Repro (zeroing enabled, force reload so `Latest.log` shows `zeroed trap recipe cost: ItemIron 2 → 0`):
   traps stop; watch `Latest.log` for `DivideByZeroException` or the
   `"sourceItemsRemoved: 0 does not equal the expected count of: 1"` line.
3. Post-fix: traps craft and deploy again with the zeroing active.

## Key file refs

- WotW: [Components/HunterCabinEnhancement.cs:307-335](../Components/HunterCabinEnhancement.cs) (`ZeroTrapRecipeCosts` loop; bug at `:329`), `:136/:142-148/:151-179` (static guard, OnMapLoaded reset, coroutine-deferred apply).
- FF decomp (`ff_full_dlc.cs`): `334152` (divide-by-zero), `333982/333977/333999` (commit invariant + log), `345486` (request capacity → 0), `209346/199716/199719` (deploy seeks/places `itemAnimalTrap`), `382801-808` (empty-bucket seek fail), `186490-186495` (`numSourceItemsNeeded` getter), `145146` (48-month lifespan), `346125-141` (refund on collect).

🤖 Investigation via multi-agent decomp workflow (3 workflows, adversarially verified). Generated with [Claude Code](https://claude.com/claude-code)
