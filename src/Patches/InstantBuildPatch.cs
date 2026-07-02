using HarmonyLib;

namespace DivineHands.Patches
{
    /// <summary>
    /// Feeds new build sites to <see cref="Modules.InstantBuild"/>. Postfix on
    /// <c>BuildSiteResource.Initialize</c> [147324] — the same hook point vanilla's own free-buildings
    /// cheat uses (it starts CompleteBuildingNextFrame from inside Initialize @147333). We only queue
    /// here; completion happens next frame in InstantBuild.OnUpdate, never mid-placement. Inert unless
    /// the power is armed.
    /// </summary>
    [HarmonyPatch(typeof(BuildSiteResource), "Initialize")]
    internal static class InstantBuildPatch
    {
        private static void Postfix(BuildSiteResource __instance)
        {
            Modules.InstantBuild.OnBuildSitePlaced(__instance);
        }
    }
}
