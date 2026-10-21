using HarmonyLib;
using MelonLoader;

namespace DivineHands.Patches
{
    /// <summary>
    /// Makes it impossible to store an invalid seed in a <c>CERandom.RNG</c>. Prefix on the
    /// <c>RNG.Seed</c> setter.
    ///
    /// RNG is a Lehmer generator, <c>x = 16807x mod 2147483647</c>, via Schrage's method. Two
    /// states break it permanently:
    ///   • <b>0</b> is an ABSORBING state - 16807*0 mod m is 0, so the generator can never leave.
    ///     Generate() returns 0, GenerateFloat() returns 0f, and Range(min,max) returns exactly
    ///     <c>min</c> for the rest of the session. Every probability test in the game reads
    ///     `chance >= Range(..,0f,1f)` or `Range(..,0,100) &lt;= pct`, so EVERY ROLL SUCCEEDS:
    ///     the town catches every contagious disease within seconds (contagion bypasses the
    ///     required-factor gates entirely), a fire starts every month, and every fire spreads.
    ///   • anything <b>above 2147483646</b> casts NEGATIVE in the `(int)previous` inside
    ///     Generate(), and the output is undefined.
    ///
    /// Vanilla writes this shared seed directly - TerrainManager.SpawnTreePlantingSites hijacks it
    /// per grid cell with <c>generator.Seed = (uint)num7</c>, where num7 is <c>i + j * num</c> and
    /// therefore ZERO at cell (0,0), restoring it only after the loops with no try/finally.
    /// Measured in the field at roughly one zeroing every 2.7 real seconds in a mature town. Since
    /// nothing in vanilla ever re-seeds, the FIRST one is permanent: 519 of 526 villagers sick in
    /// seconds, with the disease spawn gates still shut.
    ///
    /// <b>Why a patch instead of the polling guard:</b> <see cref="Modules.RngGuard"/> repairs on
    /// the next frame, which leaves a window of a few milliseconds per zeroing where rolls still
    /// auto-pass. Across 146 repairs in one session that window let a measurable excess of fires
    /// through. Blocking the write closes it outright - the invalid state is never reachable.
    ///
    /// <b>Why this preserves the stream:</b> rather than inventing a seed, an invalid write keeps
    /// the generator's CURRENT state, so the random sequence continues uninterrupted and the save
    /// stays as deterministic as it was. Re-seeding from a clock on every block would instead
    /// restart the stream ~20 times a minute. The only behavioural difference is that the one grid
    /// cell whose deterministic sub-seed would have been 0 now reuses the running state, which
    /// changes nothing a player can observe.
    ///
    /// Cannot misfire: zero and out-of-range are never legitimate states for this generator, so
    /// this only ever replaces a broken value with a working one.
    /// </summary>
    [HarmonyPatch(typeof(RNG), "Seed", MethodType.Setter)]
    internal static class RngSeedGuardPatch
    {
        /// <summary>Largest valid state for modulus 2147483647.</summary>
        internal const uint MaxSeed = 2147483646u;

        /// <summary>True once Harmony has resolved and applied the setter patch. The polling guard
        /// reads this to decide whether it is the primary fix or a fallback.</summary>
        internal static bool Applied;

        /// <summary>Invalid writes blocked this session.</summary>
        internal static int Blocked;

        private const int LogFirst = 3;
        private const int LogEvery = 250;

        private static bool Prepare()
        {
            Applied = AccessTools.PropertySetter(typeof(RNG), "Seed") != null;
            return Applied;
        }

        private static void Prefix(RNG __instance, ref uint value)
        {
            if (value != 0u && value <= MaxSeed) return;   // valid, the overwhelming common path

            uint invalid = value;

            // Keep the live state if it is usable; only manufacture one if the generator is
            // already sitting on a broken value (i.e. this patch went in after the damage).
            uint current = __instance != null ? __instance.Seed : 0u;
            value = (current != 0u && current <= MaxSeed)
                ? current
                : 1u + (uint)((ulong)System.DateTime.UtcNow.Ticks % MaxSeed);

            Blocked++;
            if (Blocked <= LogFirst)
            {
                MelonLogger.Warning(
                    $"[DivineHands] Blocked an invalid rng seed write ({invalid}) — kept {value}. " +
                    (invalid == 0u
                        ? "Zero is a Lehmer absorbing state: it would have made every probability " +
                          "roll in the game succeed for the rest of the session (mass contagion, a " +
                          "fire every month). This is a vanilla bug, not a mod."
                        : "Values above 2147483646 cast negative inside the generator and its " +
                          "output becomes undefined.") +
                    $" (block #{Blocked})");
            }
            else if (Blocked % LogEvery == 0)
            {
                MelonLogger.Warning($"[DivineHands] Invalid rng seed writes blocked: {Blocked} this " +
                                    "session. Something is zeroing it continuously; the patch is holding.");
            }
        }
    }
}
