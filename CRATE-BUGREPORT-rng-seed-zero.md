# Bug report: GAMEPLAY RNG can reach seed 0, after which every probability roll in the game succeeds

**Game:** Farthest Frontier v1.1.0 (Mono build)
**Severity:** Save-breaking. Silent — nothing is logged, no exception is thrown, and no in-game
indicator reveals it.
**Reported by:** SageDragoon
**Observed:** 2026-10-20, captured live with instrumentation

---

## Summary

`CERandom`'s `GAMEPLAY` generator can end up with a seed of **0**. Because `RNG` is a Lehmer
generator (`x = 16807·x mod 2147483647`), **zero is an absorbing state** — `16807 · 0 mod m = 0`.
Once the seed is 0 it remains 0 for the rest of the session.

In that state:

- `RNG.Generate()` returns `0`
- `RNG.GenerateFloat()` returns `0f`
- `RNG.Range(min, max)` returns exactly **`min`** on every call

Every probability test in the game is written as either `chance >= CERandom.Range(.., 0f, 1f)` or
`CERandom.Range(.., 0, 100) <= pct`. With `Range` pinned to its minimum, **every one of those
tests passes, forever.**

The generator still *exists*, so `CERandom.GeneratorExists(Type.GAMEPLAY)` continues to report it
as healthy. There is no symptom available to the player other than the consequences.

## Observed impact

A 526-villager town, healthy and stable, captured across three readings minutes apart:

```
19:49   GAMEPLAY seed 1410498401      0 sick of 526
19:53   GAMEPLAY seed  430619988      0 sick of 526
19:56   GAMEPLAY seed          0    519 sick of 526   (187 bedridden)
```

The in-game health panel at that moment:

```
519 villagers are sick
 10 Influenza      260 Worms        97 Fractured Bone
 90 Bee Sting      373 Typhoid     357 Cholera
 26 Food Poisoning   5 Eagle Attack   8 Dysentery
 10 Frostbite       10 Scurvy        5 Dehydration
```

Two details confirm the mechanism rather than a disease-balance problem:

1. **The spawn gates were shut.** `Disease.GetFactoredScore` returns `false` when any factor
   marked `isRequired` evaluates to ~0, and `RollDiseaseByDuration` then never rolls. At the time
   of capture only **one** villager had `ResidenceStocked_Soap` non-zero — yet 373 had Typhoid and
   357 had Cholera, both of which require that factor. Those cases cannot have come from the
   environmental roll.

2. **They came through contagion, which has no factor gate.**
   `ImmuneSystemComponent.ProcessExposure` tests
   `elapsedDays * spreadRatePerDay * diseaseSpreadMultiplier >= CERandom.Range(GAMEPLAY, 0f, 1f)`
   and calls `ContractDisease`, which checks only immunity and current infection — never the
   disease's factors. With `Range` returning 0, every exposure converts on the first tick.

Personal (`PD_`) diseases show the complementary signature: each landed on **exactly** its gate
population. Instrumentation minutes earlier measured `Occupation_HazardousJobs` open for 82–92
villagers; the event produced 97 Fractured Bone. Bee Sting hit all 90 eligible, Eagle Attack all 5.

The same failure affects everything else that rolls. `CatastropheManager.OnMonthPassed` uses
`CERandom.Range(GAMEPLAY, 0, 100) <= fireChancePct`, so **a fire starts every month**, and
`RollForSpreadFire` uses `Range(0f,1f) <= spreadChance`, so **every fire spreads**. An earlier
session reached 16 simultaneous building fires.

## Root cause

A valid Lehmer state can never reach 0 on its own — 0 lies outside the cycle. This was verified by
simulating `Generate()` with exact C# unchecked 32-bit semantics: 3000 random seeds from
`[1, 2^31−2]` and 3000 from `[2^31, 2^32)`, 2000 steps each, produced no zero state. The seed has
to be **written**.

`Assembly-CSharp` contains exactly one place that writes the shared GAMEPLAY seed directly —
`TerrainManager.SpawnTreePlantingSites()`:

```csharp
RNG generator = CERandom.GetGenerator(CERandom.Type.GAMEPLAY);
uint seed = generator.Seed;                       // save

for (int i = num3; i <= num5 + 1; i += 2)
{
    for (int j = num4; j <= num6 + 1; j += 2)
    {
        int num7 = i + j * num;
        if (occupiedGrids.Contains(num7) || ...) continue;

        generator.Seed = (uint)num7;              // ← ZERO when i == 0 && j == 0
        generator.Range(0, 3);

        for (int k = -1; k < 1; k++)
            for (int l = -1; l < 1; l++)
                if (!agricultureManager.workCampValidTreeData[i + k, j + l])   // ← index −1
                    continue;
        ...
    }
}
generator.Seed = seed;                            // restore — NOT in a finally
```

Two problems:

1. **`num7` is zero at grid cell (0,0)**, so the shared gameplay seed is deliberately set to the
   one value from which the generator cannot recover.
2. **The restore is not protected.** There is no `try/finally`, so any exception between the
   hijack and the restore leaves the seed at a grid index. The inner body indexes
   `workCampValidTreeData[i - 1, j - 1]`, which is out of bounds precisely at the boundary cells
   where `i` or `j` is 0 — the same cells that produce seed 0.

**To be explicit about confidence:** the state (`seed == 0` causing mass simultaneous infection) is
directly observed and reproducible. The specific path that writes the zero is **inferred** — no
exception appeared in the MelonLoader log during the captured event, so the throw-before-restore
chain is not proven. It is offered as the most likely candidate because it is the only code found
that assigns this seed a caller-controlled value that can be zero.

## Reproduction

Not yet reduced to deterministic steps. Observed twice in one session, in both cases within
seconds of closing the tech tree screen after spending points. A town large enough to have work
camps and tree-planting activity near the map origin appears relevant.

The reliable detector, regardless of trigger:

```csharp
CERandom.GetGenerator(CERandom.Type.GAMEPLAY).Seed == 0
```

Once that is true the session is unrecoverable until something re-seeds. Loading a save calls
`GenerateInternal` → `CERandom.BeginGeneration`, which re-initialises GAMEPLAY, so **reloading
fixes new rolls** — but villagers already infected stay infected, which makes the bug look like it
survives a reload.

## Suggested fixes

Any one of these closes it; the first two are the cheapest.

1. **Guard the setter.** Reject or remap 0 in `RNG.Seed`:
   ```csharp
   set { previous = (value == 0u || value > 2147483646u) ? 1u : value; }
   ```
   Zero is never a legitimate state for this generator, so this cannot change working behaviour.

2. **Protect the restore** in `SpawnTreePlantingSites` with `try/finally`, and offset the per-cell
   seed so it can never be zero (`generator.Seed = (uint)num7 + 1`).

3. **Don't hijack the shared generator at all.** The function wants deterministic per-cell values;
   a private `RNG` instance would do that without touching gameplay randomness. `TerrainManager`
   already uses a private `rng` field elsewhere (`GetNewTreePosition`) for exactly this pattern.

4. **Fix the out-of-bounds read**: the `k`/`l` loops start at −1 without clamping `i`/`j` to ≥ 1.

## Notes

- Reproduced with mods loaded (MelonLoader 0.7.0), but **no mod writes this seed** — all mod usage
  audited and is read-only via `CERandom.Range`. The writes quoted above are vanilla.
- A diagnostic and a runtime repair for this exist in the Divine Hands mod
  (`RngGuard`), which detects `seed == 0` and re-seeds from the clock. That is a workaround for
  players, not a substitute for the engine fix.
