using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    /// <summary>
    /// Persisted pointer to one subscribed workshop item that contains BepInEx plugins.
    /// </summary>
    internal sealed class WorkshopPluginRecord
    {
        public ulong WorkshopId { get; set; }

        public string Folder { get; set; }

        public string Name { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Ascending load order. Lower values are loaded first, subject to plugin dependencies.
        /// </summary>
        public int LoadOrder { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }

    internal sealed class WorkshopPluginRecordFile
    {
        public int FormatVersion { get; set; } = 2;

        /// <summary>
        /// Name of the applied preset, or null/empty for manual mode.
        /// </summary>
        public string ActivePreset { get; set; }

        public List<WorkshopPluginRecord> Items { get; set; } = new List<WorkshopPluginRecord>();

        public List<WorkshopPluginPreset> Presets { get; set; } = new List<WorkshopPluginPreset>();
    }

    /// <summary>
    /// Stores the workshop plugin folders, their load order and the user's presets.
    /// The list is rebuilt whenever the game can query Steam, so startup loading does not need Steam.
    /// </summary>
    internal static class WorkshopPluginRecordStore
    {
        private const string FileName = "workshop-plugins.json";

        public static string DirectoryPath
        {
            get { return Path.Combine(Paths.ConfigPath, BepInExSecondaryLoader.PluginInfo.PLUGIN_NAME); }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, FileName); }
        }

        public static WorkshopPluginRecordFile Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new WorkshopPluginRecordFile();
                }

                var file = JsonConvert.DeserializeObject<WorkshopPluginRecordFile>(File.ReadAllText(FilePath));
                if (file == null)
                {
                    return new WorkshopPluginRecordFile();
                }

                Normalize(file);
                return file;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to read '{FilePath}': {ex}");
                return new WorkshopPluginRecordFile();
            }
        }

        public static void Save(WorkshopPluginRecordFile file)
        {
            try
            {
                if (file == null)
                {
                    return;
                }

                Normalize(file);
                System.IO.Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to write '{FilePath}': {ex}");
            }
        }

        /// <summary>
        /// Removes duplicates, canonicalizes preset names and assigns contiguous load orders while
        /// preserving the previous relative order (LINQ OrderBy is stable).
        /// </summary>
        public static void Normalize(WorkshopPluginRecordFile file)
        {
            if (file == null)
            {
                return;
            }

            file.FormatVersion = 2;

            if (file.Items == null)
            {
                file.Items = new List<WorkshopPluginRecord>();
            }

            file.Items = file.Items.Where(record => record != null).ToList();
            file.Items = DeduplicateById(file.Items, record => record.WorkshopId);

            var orderedItems = file.Items.OrderBy(record => record.LoadOrder).ToList();
            for (var i = 0; i < orderedItems.Count; i++)
            {
                orderedItems[i].LoadOrder = i;
            }
            file.Items = orderedItems;

            if (file.Presets == null)
            {
                file.Presets = new List<WorkshopPluginPreset>();
            }

            var presetsByName = new Dictionary<string, WorkshopPluginPreset>(StringComparer.OrdinalIgnoreCase);
            var presetOrder = new List<string>();
            foreach (var preset in file.Presets)
            {
                if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
                {
                    continue;
                }

                preset.Name = preset.Name.Trim();
                if (!presetsByName.ContainsKey(preset.Name))
                {
                    presetOrder.Add(preset.Name);
                }
                presetsByName[preset.Name] = preset;
            }

            file.Presets = presetOrder.Select(name => presetsByName[name]).ToList();

            foreach (var preset in file.Presets)
            {
                if (preset.Items == null)
                {
                    preset.Items = new List<WorkshopPluginPresetItem>();
                }

                preset.Items = preset.Items.Where(item => item != null).ToList();
                preset.Items = DeduplicateById(preset.Items, item => item.WorkshopId);

                var orderedPresetItems = preset.Items.OrderBy(item => item.LoadOrder).ToList();
                for (var i = 0; i < orderedPresetItems.Count; i++)
                {
                    orderedPresetItems[i].LoadOrder = i;
                }
                preset.Items = orderedPresetItems;
            }

            if (!string.IsNullOrWhiteSpace(file.ActivePreset))
            {
                var active = file.Presets.FirstOrDefault(preset =>
                    string.Equals(preset.Name, file.ActivePreset.Trim(), StringComparison.OrdinalIgnoreCase));
                file.ActivePreset = active?.Name;
            }
            else
            {
                file.ActivePreset = null;
            }
        }

        private static List<T> DeduplicateById<T>(List<T> items, Func<T, ulong> idSelector)
        {
            var byId = new Dictionary<ulong, T>();
            var order = new List<ulong>();

            foreach (var item in items)
            {
                var id = idSelector(item);
                if (!byId.ContainsKey(id))
                {
                    order.Add(id);
                }
                byId[id] = item;
            }

            return order.Select(id => byId[id]).ToList();
        }
    }
}
