using System;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Repairs the GAMEPLAY random generator when its seed reaches ZERO — a vanilla state that
    /// silently breaks every probability check in the game.
    ///
    /// CERandom.RNG is a Lehmer generator: <c>x = 16807x mod 2147483647</c>, implemented with
    /// Schrage's method. **Zero is an absorbing state.** 16807 * 0 mod m is 0, so once the seed
    /// becomes 0 it stays 0 forever: Generate() returns 0, GenerateFloat() returns 0f, and
    /// Range(min, max) returns exactly <c>min</c> on every call for the rest of the session.
    ///
    /// Every probability test in Farthest Frontier is written as either
    /// <c>chance >= CERandom.Range(.., 0f, 1f)</c> or <c>CERandom.Range(.., 0, 100) &lt;= pct</c>,
    /// so a zero seed means EVERY ROLL SUCCEEDS. Observed live on 2026-10-20: a town went from
    /// 0 sick to 519 of 526 in seconds, with the disease spawn gates still shut — the damage came
    /// through ImmuneSystemComponent.ProcessExposure, whose contagion check
    /// (<c>elapsedDays * spreadRatePerDay * mult >= Range(0f,1f)</c>) passes unconditionally and
    /// bypasses required factors entirely. Fires start every month and every fire spreads for the
    /// same reason. Nothing is logged and no exception is thrown; the generator still EXISTS, so
    /// even CERandom.GeneratorExists reports it healthy.
    ///
    /// A valid Lehmer state can never reach 0 on its own — 0 lies outside the cycle, verified by
    /// simulation over millions of steps from both in-range and out-of-range seeds. It has to be
    /// WRITTEN, and vanilla writes that shared seed directly in TerrainManager
    /// .SpawnTreePlantingSites, which hijacks it per grid cell (<c>generator.Seed = (uint)num7</c>,
    /// where num7 is <c>i + j * num</c> and therefore ZERO at cell 0,0) and only restores it after
    /// the loops, with no try/finally. The inner body indexes <c>workCampValidTreeData[i-1, j-1]</c>,
    /// so the boundary cell that produces seed 0 is also the one that can throw before the restore
    /// runs. That exact chain is unproven — no exception appeared in the log — so the guard keys on
    /// the STATE, not on any theory of how it got there.
    ///
    /// Safe by construction: zero is never a legitimate seed for this generator, so repairing it
    /// cannot alter behaviour that was working. It only ever converts a permanently broken session
    /// into a working one.
    /// </summary>
    public static class RngGuard
    {
        /// <summary>Largest valid state for modulus 2147483647. Above this, the uint casts
        /// NEGATIVE inside Schrage's method and the output is undefined.</summary>
        private const uint MaxSeed = 2147483646u;

        /// <summary>Times the seed has been found dead and repaired this session.</summary>
        public static int Repairs { get; private set; }

        /// <summary>Full explanation for the first few, then one line per this many. Observed in
        /// the field at roughly one death every 2.7 seconds - about twice per in-game day - so an
        /// unthrottled warning would put ~1300 lines an hour in the log and bury everything else.
        /// The condition is permanent while it lasts, so repetition carries no new information.</summary>
        private const int LogFirst = 3;
        private const int LogEvery = 100;

        /// <summary>Last value installed, for the panel status line.</summary>
        public static uint LastSeedInstalled { get; private set; }

        public static void OnMapLoaded() { Repairs = 0; LastSeedInstalled = 0u; }
        public static void OnSceneExit() { Repairs = 0; LastSeedInstalled = 0u; }

        /// <summary>
        /// One property read per frame in the healthy case, which is the whole point — the window
        /// between the seed dying and the next disease or fire tick is short, and a check that only
        /// ran occasionally would let a wave through before repairing anything.
        /// </summary>
        public static void OnUpdate()
        {
            if (!Config.MasterEnable.Value || !Config.RngGuardEnable.Value) return;
            // Primary fix is the setter patch; this stays on as a fallback for the case where
            // Harmony could not resolve the property (a game update renaming it, say).

            try
            {
                if (!CERandom.GeneratorExists(CERandom.Type.GAMEPLAY)) return;
                var g = CERandom.GetGenerator(CERandom.Type.GAMEPLAY);
                if (g == null) return;

                uint s = g.Seed;
                if (s != 0u && s <= MaxSeed) return;   // healthy, overwhelmingly the common path

                uint fresh = FreshSeed();
                g.Seed = fresh;
                Repairs++;
                LastSeedInstalled = fresh;

                // With Patches.RngSeedGuardPatch applied, an invalid seed can never be STORED, so
                // reaching here at all means that patch did not cover the write. Worth saying
                // loudly and every time - it is a different, unknown path, not the known one.
                if (Patches.RngSeedGuardPatch.Applied)
                {
                    MelonLogger.Warning(
                        $"[DivineHands] rng seed reached {s} DESPITE the setter patch being applied " +
                        $"— repaired to {fresh}. Something is writing the backing field directly, or " +
                        "through a path the patch does not cover. Please report this.");
                    return;
                }

                bool verbose = Repairs <= LogFirst;
                bool periodic = Repairs % LogEvery == 0;
                if (!verbose && !periodic) return;   // repaired silently; nothing new to say

                if (!verbose)
                {
                    MelonLogger.Warning(
                        $"[DivineHands] GAMEPLAY rng seed has now been repaired {Repairs} times this " +
                        "session - something is zeroing it continuously. Still a vanilla bug; the " +
                        "guard is holding.");
                    return;
                }

                if (s == 0u)
                {
                    MelonLogger.Warning(
                        $"[DivineHands] GAMEPLAY rng seed was 0 (dead) — re-seeded to {fresh}. " +
                        "A zero seed is a Lehmer absorbing state: every probability roll in the " +
                        "game was succeeding, which mass-infects the town through contagion and " +
                        "starts a fire every month. This is a vanilla bug, not a mod. " +
                        $"(repair #{Repairs} this session)");
                }
                else
                {
                    MelonLogger.Warning(
                        $"[DivineHands] GAMEPLAY rng seed was {s}, past the modulus — re-seeded to " +
                        $"{fresh}. Above {MaxSeed} the uint casts negative inside Schrage's method " +
                        $"and the generator's output is undefined. (repair #{Repairs} this session)");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] RNG guard failed: {ex.Message}");
            }
        }

        /// <summary>A clock-derived seed forced into the valid range [1, 2147483646]. Never 0, so
        /// the repair cannot reinstate the very state it exists to clear.</summary>
        private static uint FreshSeed()
        {
            ulong t = (ulong)DateTime.UtcNow.Ticks;
            return 1u + (uint)(t % MaxSeed);
        }
    }
}
