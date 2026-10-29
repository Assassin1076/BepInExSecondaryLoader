using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInExSecondaryLoader.WorkshopPlugins;
using HarmonyLib;
using NuclearOption.ModScripts;
using NuclearOption.Workshop;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BepInExSecondaryLoader
{
    public class PluginInfo
    {
        public const string PLUGIN_GUID = "experimental.assassin1076.BepInExSecondaryLoader";
        public const string PLUGIN_NAME = "BepInExSecondaryLoader";
        public const string PLUGIN_VERSION = "1.0.0";
    }

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private ConfigEntry<KeyboardShortcut> _toggleManagerKey;
        private WorkshopPluginManagerWindow _managerWindow;

        private void Awake()
        {
            // Plugin startup logic
            Logger = base.Logger;
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            _toggleManagerKey = Config.Bind(
                "Interface",
                "Toggle Workshop Plugin Manager",
                new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "打开/关闭工坊插件加载顺序与预设窗口。");
            _managerWindow = new WorkshopPluginManagerWindow();

            try
            {
                var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo("Applied workshop compatibility patches.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to apply Harmony patches: {ex}");
            }

            ModTypesRegistration.Register();
            WorkshopPluginLoader.InstallAssemblyResolver();

            // Stage 1: load the workshop plugins recorded during the previous session. Steam is not
            // available yet, so the persisted folders are the only source of truth here.
            try
            {
                var loaded = LoadPersistedPlugins();
                Logger.LogInfo($"Startup stage loaded {loaded} workshop plugin(s) from the persisted records.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load persisted workshop plugins: {ex}");
            }

            StartCoroutine(StageTwoWhenSteamReady());
        }

        private void Update()
        {
            if (_toggleManagerKey != null && _toggleManagerKey.Value.IsDown())
            {
                _managerWindow?.Toggle();
            }
        }

        private void OnGUI()
        {
            _managerWindow?.OnGUI();
        }

        private static int LoadPersistedPlugins()
        {
            var records = WorkshopPluginRecordStore.Load();

            if (WorkshopPresetManager.ApplyActivePreset(records))
            {
                WorkshopPluginRecordStore.Save(records);
            }

            return WorkshopPluginLoader.LoadRecords(records.Items);
        }

        private IEnumerator StageTwoWhenSteamReady()
        {
            // Let the BepInEx chainloader finish adding the remaining plugins, then retry records
            // that were skipped because a dependency had not been loaded yet.
            yield return null;

            try
            {
                var retried = LoadPersistedPlugins();
                if (retried != 0)
                {
                    Logger.LogInfo($"Retry loaded {retried} persisted workshop plugin(s) after the chainloader finished.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to retry persisted workshop plugins: {ex}");
            }

            var loggedWait = false;
            while (!SteamManager.ClientInitialized && !SteamManager.ServerInitialized)
            {
                if (!loggedWait)
                {
                    Logger.LogInfo("Waiting for Steam to initialize before refreshing workshop plugin records...");
                    loggedWait = true;
                }

                yield return new WaitForSecondsRealtime(2f);
            }

            // Steam has just initialized; give workshop install info a moment to settle.
            yield return new WaitForSecondsRealtime(2f);

            try
            {
                WorkshopPluginSynchronizer.SyncAndLoadDiscoveredPlugins();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to refresh workshop plugin records: {ex}");
            }
        }
    }

    /// <summary>
    /// Workshop item type for BepInEx plugin packages.
    /// A plugin package is a folder that contains a Plugin.json manifest; the whole folder tree
    /// below it is uploaded and loaded. The game only defines Mission and AircraftLivery, so 3 is
    /// used as an extension value.
    /// </summary>
    public class PluginMod : ModType
    {
        public const string MOD_NAME = "BepInEx Plugin";

        public const SubscribedItemType ITEM_TYPE = (SubscribedItemType)3;

        public PluginMod() : base(ITEM_TYPE, MOD_NAME)
        {
        }

        public override string GetRootFolder()
        {
            return Paths.PluginPath;
        }

        public override void GetUploadFiles(List<string> outList, WorkshopUploadItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (!Directory.Exists(item.Folder))
            {
                throw new DirectoryNotFoundException($"Plugin package folder not found: {item.Folder}");
            }

            if (!PluginPackage.HasManifest(item.Folder))
            {
                throw new FileNotFoundException(
                    $"'{PluginPackage.ManifestFileName}' was not found in the plugin package root: {item.Folder}");
            }

            var files = Directory.GetFiles(item.Folder, "*", SearchOption.AllDirectories)
                .Select(fullPath => PluginPackage.GetRelativePath(item.Folder, fullPath))
                .Where(relativePath => !string.IsNullOrEmpty(relativePath) && !Path.IsPathRooted(relativePath))
                .Where(relativePath => !string.Equals(relativePath, "workshop.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!files.Any(relativePath => relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                throw new FileNotFoundException($"No .dll file found in plugin package: {item.Folder}");
            }

            outList.AddRange(files);
        }

        public override List<string> ListItems()
        {
            var root = GetRootFolder();
            if (!Directory.Exists(root))
            {
                return new List<string>();
            }

            return PluginPackage.FindPackageRoots(root)
                .Select(packageRoot => PluginPackage.GetRelativePath(root, packageRoot))
                .Where(relativePath => !string.IsNullOrEmpty(relativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public override bool TryGetItem(string itemName, out WorkshopUploadItem item, out string error)
        {
            if (string.IsNullOrEmpty(itemName))
            {
                throw new InvalidOperationException("Page 3 should not be active when item name is null");
            }

            var folder = Path.Combine(GetRootFolder(), itemName);
            if (!Directory.Exists(folder))
            {
                error = $"Plugin package folder not found: {folder}";
                item = null;
                return false;
            }

            if (!PluginPackage.TryReadManifest(folder, out var manifest, out var manifestError))
            {
                error = $"Invalid plugin package root '{folder}': {manifestError}";
                item = null;
                return false;
            }

            var plugins = WorkshopPluginScanner.ScanPackage(folder, manifest);
            if (plugins.Count == 0)
            {
                error = $"No BepInEx plugin was found in '{folder}'. Check '{PluginPackage.ManifestFileName}' and the assemblies listed in it.";
                item = null;
                return false;
            }

            var tags = new List<string>(2 + (manifest.Tags?.Count ?? 0)) { MOD_NAME, "BepInEx" };
            if (manifest.Tags != null)
            {
                foreach (var tag in manifest.Tags)
                {
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(tag);
                    }
                }
            }

            var displayName = !string.IsNullOrWhiteSpace(manifest.Name)
                ? manifest.Name.Trim()
                : Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var description = !string.IsNullOrWhiteSpace(manifest.Description)
                ? manifest.Description
                : "BepInEx plugin package:" + Environment.NewLine +
                    string.Join(Environment.NewLine, plugins.Select(plugin =>
                        $"{plugin.Info.Metadata.Name} {plugin.Info.Metadata.Version} ({plugin.Info.Metadata.GUID})"));

            item = new WorkshopUploadItem(tags, ITEM_TYPE, folder, displayName)
            {
                Description = description,
                ModRequired = true
            };
            error = null;
            return true;
        }
    }
}
