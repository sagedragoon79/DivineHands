using HarmonyLib;

namespace DivineHands.Patches
{
    /// <summary>
    /// Ties the stamped-lake sidecar to FF's own save lifecycle:
    ///   • after a game save (<c>SaveManager.SaveInternal</c> [182329]) → (re)write the sidecar next to
    ///     the .sav just written, so it always matches the save,
    ///   • after a save is deleted (<c>SaveManager.DeleteSavedGame</c> [181989]) → remove its sidecar so
    ///     no orphan file is left behind.
    /// See <see cref="Modules.LakeStamp"/> / <see cref="Modules.LakeSidecar"/>. Positional __n args per
    /// project convention (Harmony matches named prefix/postfix args by parameter name).
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "SaveInternal")]
    internal static class SaveGameSidecarPatch
    {
        // __0 = string savedGameFileNameNoExtension
        private static void Postfix(string __0)
        {
            Modules.LakeStamp.WriteSidecarForSave(__0);
        }
    }

    [HarmonyPatch(typeof(SaveManager), "DeleteSavedGame")]
    internal static class DeleteSaveSidecarPatch
    {
        // __0 = string fileNameNoExtension (same "folder/name" key the sidecar path uses)
        private static void Postfix(string __0)
        {
            Modules.LakeSidecar.Delete(__0);
        }
    }
}
