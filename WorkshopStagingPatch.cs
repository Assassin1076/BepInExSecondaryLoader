using System.IO;
using HarmonyLib;
using NuclearOption.Workshop;

namespace BepInExSecondaryLoader
{
    /// <summary>
    /// The game's staging helper only copies files into the staging root. Plugin packages may
    /// contain nested folders, so create the destination directory before the copy.
    /// </summary>
    [HarmonyPatch(typeof(WorkshopStaging), nameof(WorkshopStaging.CopyFile))]
    internal static class WorkshopStagingCopyFilePatch
    {
        private static void Prefix(WorkshopStaging __instance, string fileName)
        {
            if (__instance == null || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            var destination = Path.Combine(__instance.StagePath, fileName);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
