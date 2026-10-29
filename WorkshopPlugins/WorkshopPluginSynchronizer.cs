using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    /// <summary>
    /// Refreshes the persisted plugin list from the current Steam subscriptions.
    /// This runs once Steam is available so the next startup can load plugins without Steam.
    /// </summary>
    internal static class WorkshopPluginSynchronizer
    {
        private sealed class SubscribedFolder
        {
            public ulong Id;
            public string Folder;
        }

        public static void SyncAndLoadDiscoveredPlugins()
        {
            var recordFile = WorkshopPluginRecordStore.Load();
            var previous = new Dictionary<ulong, WorkshopPluginRecord>();
            foreach (var record in recordFile.Items)
            {
                if (record != null && record.WorkshopId != 0)
                {
                    previous[record.WorkshopId] = record;
                }
            }

            List<SubscribedFolder> subscribed;
            try
            {
                subscribed = GetSubscribedFolders();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to query subscribed workshop items: {ex}");
                return;
            }

            var refreshed = new List<WorkshopPluginRecord>();
            var nextLoadOrder = recordFile.Items.Count;
            foreach (var item in subscribed)
            {
                var workshopId = item.Id;
                var folder = item.Folder;

                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    KeepPreviousRecord(previous, workshopId, refreshed, $"item {workshopId} has no usable install folder");
                    continue;
                }

                if (!PluginPackage.TryFindPackageRoot(folder, out var packageRoot, out var manifest, out var packageError))
                {
                    if (previous.ContainsKey(workshopId) || PluginPackage.HasManifest(folder))
                    {
                        KeepPreviousRecord(previous, workshopId, refreshed, $"item {workshopId} {packageError}");
                    }
                    else
                    {
                        Plugin.Logger?.LogDebug($"Workshop item {workshopId} is not a plugin package.");
                    }

                    continue;
                }

                List<WorkshopPluginCandidate> found;
                try
                {
                    found = WorkshopPluginScanner.ScanPackage(packageRoot, manifest);
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"Failed to inspect workshop plugin item {workshopId} at '{packageRoot}': {ex}");
                    KeepPreviousRecord(previous, workshopId, refreshed, $"item {workshopId} could not be inspected");
                    continue;
                }

                if (found.Count == 0)
                {
                    KeepPreviousRecord(previous, workshopId, refreshed, $"item {workshopId} contains no BepInEx plugin assembly");
                    continue;
                }

                previous.TryGetValue(workshopId, out var record);
                if (record == null)
                {
                    var enabled = string.IsNullOrEmpty(recordFile.ActivePreset);
                    var loadOrder = nextLoadOrder++;

                    if (!string.IsNullOrEmpty(recordFile.ActivePreset))
                    {
                        var preset = WorkshopPresetManager.Find(recordFile, recordFile.ActivePreset);
                        var presetItem = preset?.Items?.FirstOrDefault(item => item.WorkshopId == workshopId);
                        if (presetItem != null)
                        {
                            enabled = true;
                            loadOrder = presetItem.LoadOrder;
                        }
                    }

                    record = new WorkshopPluginRecord
                    {
                        WorkshopId = workshopId,
                        Enabled = enabled,
                        LoadOrder = loadOrder
                    };

                    Plugin.Logger?.LogInfo(
                        $"Discovered workshop plugin item {workshopId}: '{GetDisplayName(manifest, found[0].Info, packageRoot)}' (enabled={enabled}).");
                }

                record.Folder = packageRoot;
                record.Name = GetDisplayName(manifest, found[0].Info, packageRoot);
                record.UpdatedUtc = DateTime.UtcNow;
                refreshed.Add(record);
            }

            foreach (var removedId in previous.Keys.Except(refreshed.Select(record => record.WorkshopId)))
            {
                Plugin.Logger?.LogInfo($"Workshop plugin item {removedId} is no longer subscribed; dropping its persisted record.");
            }

            recordFile.Items = refreshed;
            WorkshopPluginRecordStore.Normalize(recordFile);
            WorkshopPluginRecordStore.Save(recordFile);

            Plugin.Logger?.LogInfo($"Workshop plugin records refreshed: {refreshed.Count} item(s) persisted.");

            // Plugins that were not loaded during startup (new subscriptions or transient failures)
            // are activated here, now that Steam and the game assemblies are ready.
            if (refreshed.Count != 0)
            {
                var loaded = WorkshopPluginLoader.LoadRecords(recordFile.Items);
                if (loaded != 0)
                {
                    Plugin.Logger?.LogInfo($"Activated {loaded} workshop plugin(s) after the Steam refresh.");
                }
            }
        }

        /// <summary>
        /// Enumerates every subscribed workshop item directly through Steam. The game's helper
        /// requires workshop.json, but plugin packages are identified by Plugin.json instead.
        /// </summary>
        private static List<SubscribedFolder> GetSubscribedFolders()
        {
            var folders = new List<SubscribedFolder>();

            if (SteamManager.ClientInitialized)
            {
                var count = (int)SteamUGC.GetNumSubscribedItems();
                if (count == 0)
                {
                    return folders;
                }

                var ids = new PublishedFileId_t[count];
                var actualCount = SteamUGC.GetSubscribedItems(ids, (uint)ids.Length);
                for (uint i = 0; i < actualCount; i++)
                {
                    if (SteamUGC.GetItemInstallInfo(ids[i], out _, out var folder, 1000u, out _))
                    {
                        folders.Add(new SubscribedFolder
                        {
                            Id = ids[i].m_PublishedFileId,
                            Folder = folder
                        });
                    }
                }
            }
            else if (SteamManager.ServerInitialized)
            {
                var count = (int)SteamGameServerUGC.GetNumSubscribedItems();
                if (count == 0)
                {
                    return folders;
                }

                var ids = new PublishedFileId_t[count];
                var actualCount = SteamGameServerUGC.GetSubscribedItems(ids, (uint)ids.Length);
                for (uint i = 0; i < actualCount; i++)
                {
                    if (SteamGameServerUGC.GetItemInstallInfo(ids[i], out _, out var folder, 1000u, out _))
                    {
                        folders.Add(new SubscribedFolder
                        {
                            Id = ids[i].m_PublishedFileId,
                            Folder = folder
                        });
                    }
                }
            }

            return folders;
        }

        private static string GetDisplayName(PluginManifest manifest, BepInEx.PluginInfo info, string packageRoot)
        {
            if (!string.IsNullOrWhiteSpace(manifest?.Name))
            {
                return manifest.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(info?.Metadata?.Name))
            {
                return info.Metadata.Name;
            }

            return Path.GetFileName(packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        /// <summary>
        /// Steam may temporarily be unable to resolve an item or its files while downloading.
        /// Keep the previous record so the plugin is not forgotten because of a transient state.
        /// </summary>
        private static void KeepPreviousRecord(
            Dictionary<ulong, WorkshopPluginRecord> previous,
            ulong workshopId,
            List<WorkshopPluginRecord> refreshed,
            string reason)
        {
            if (previous.TryGetValue(workshopId, out var record) &&
                !string.IsNullOrEmpty(record.Folder) &&
                Directory.Exists(record.Folder))
            {
                Plugin.Logger?.LogWarning($"Keeping the previous record for workshop plugin {workshopId}: {reason}.");
                refreshed.Add(record);
                return;
            }

            Plugin.Logger?.LogWarning($"Skipping workshop plugin {workshopId}: {reason}.");
        }
    }
}
