using System;
using System.Collections.Generic;
using System.Linq;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    internal sealed class WorkshopPluginPresetItem
    {
        public ulong WorkshopId { get; set; }

        public int LoadOrder { get; set; }
    }

    /// <summary>
    /// A named group of enabled workshop plugins and their load order.
    /// Applying a preset enables exactly the plugins contained in it and disables the rest.
    /// </summary>
    internal sealed class WorkshopPluginPreset
    {
        public string Name { get; set; }

        public List<WorkshopPluginPresetItem> Items { get; set; } = new List<WorkshopPluginPresetItem>();

        public DateTime UpdatedUtc { get; set; }
    }

    internal static class WorkshopPresetManager
    {
        public static WorkshopPluginPreset Find(WorkshopPluginRecordFile file, string name)
        {
            if (file?.Presets == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var trimmed = name.Trim();
            return file.Presets.FirstOrDefault(preset =>
                preset != null &&
                string.Equals(preset.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        public static bool ApplyActivePreset(WorkshopPluginRecordFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.ActivePreset))
            {
                return false;
            }

            var preset = Find(file, file.ActivePreset);
            if (preset == null)
            {
                Plugin.Logger?.LogWarning($"Active preset '{file.ActivePreset}' no longer exists; switching to manual mode.");
                file.ActivePreset = null;
                return false;
            }

            var changed = ApplyPreset(file, preset);
            Plugin.Logger?.LogInfo($"Applied workshop plugin preset '{preset.Name}'.");
            return changed;
        }

        public static bool ApplyPreset(WorkshopPluginRecordFile file, WorkshopPluginPreset preset)
        {
            if (file == null || preset == null)
            {
                return false;
            }

            var orderById = new Dictionary<ulong, int>();
            foreach (var item in preset.Items ?? Enumerable.Empty<WorkshopPluginPresetItem>())
            {
                if (item != null)
                {
                    orderById[item.WorkshopId] = item.LoadOrder;
                }
            }

            var changed = false;
            foreach (var record in file.Items)
            {
                if (record == null)
                {
                    continue;
                }

                if (orderById.TryGetValue(record.WorkshopId, out var loadOrder))
                {
                    if (!record.Enabled || record.LoadOrder != loadOrder)
                    {
                        changed = true;
                    }

                    record.Enabled = true;
                    record.LoadOrder = loadOrder;
                }
                else if (record.Enabled)
                {
                    record.Enabled = false;
                    changed = true;
                }
            }

            file.ActivePreset = preset.Name;
            WorkshopPluginRecordStore.Normalize(file);
            return changed;
        }

        public static WorkshopPluginPreset SaveCurrentAsPreset(WorkshopPluginRecordFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            name = name.Trim();
            var preset = Find(file, name);
            if (preset == null)
            {
                preset = new WorkshopPluginPreset { Name = name };
                file.Presets.Add(preset);
            }

            preset.Name = name;
            preset.UpdatedUtc = DateTime.UtcNow;
            UpdateContents(file, preset);
            file.ActivePreset = preset.Name;
            WorkshopPluginRecordStore.Normalize(file);
            return preset;
        }

        /// <summary>
        /// Returns true when the live enabled list/order differs from the active preset snapshot.
        /// Presets are not modified automatically; the user decides when to overwrite one.
        /// </summary>
        public static bool IsActivePresetDirty(WorkshopPluginRecordFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.ActivePreset))
            {
                return false;
            }

            var preset = Find(file, file.ActivePreset);
            if (preset == null)
            {
                return true;
            }

            var current = file.Items
                .Where(record => record != null && record.Enabled && record.WorkshopId != 0)
                .OrderBy(record => record.LoadOrder)
                .Select(record => record.WorkshopId)
                .ToList();

            var knownIds = new HashSet<ulong>(file.Items
                .Where(record => record != null)
                .Select(record => record.WorkshopId));

            var saved = (preset.Items ?? Enumerable.Empty<WorkshopPluginPresetItem>())
                .Where(item => item != null && knownIds.Contains(item.WorkshopId))
                .OrderBy(item => item.LoadOrder)
                .Select(item => item.WorkshopId)
                .ToList();

            return !current.SequenceEqual(saved);
        }

        /// <summary>
        /// Explicitly overwrites the active preset with the current enabled list and order.
        /// </summary>
        public static bool UpdateActivePreset(WorkshopPluginRecordFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.ActivePreset))
            {
                return false;
            }

            var preset = Find(file, file.ActivePreset);
            if (preset == null)
            {
                file.ActivePreset = null;
                return false;
            }

            preset.UpdatedUtc = DateTime.UtcNow;
            UpdateContents(file, preset);
            WorkshopPluginRecordStore.Normalize(file);
            return true;
        }

        public static bool DeletePreset(WorkshopPluginRecordFile file, string name)
        {
            if (file?.Presets == null)
            {
                return false;
            }

            var preset = Find(file, name);
            if (preset == null)
            {
                return false;
            }

            file.Presets.Remove(preset);
            if (string.Equals(file.ActivePreset, preset.Name, StringComparison.OrdinalIgnoreCase))
            {
                file.ActivePreset = null;
            }

            return true;
        }

        public static void ClearActivePreset(WorkshopPluginRecordFile file)
        {
            if (file != null)
            {
                file.ActivePreset = null;
            }
        }

        private static void UpdateContents(WorkshopPluginRecordFile file, WorkshopPluginPreset preset)
        {
            var enabled = file.Items
                .Where(record => record != null && record.Enabled && record.WorkshopId != 0)
                .OrderBy(record => record.LoadOrder)
                .ToList();

            preset.Items = new List<WorkshopPluginPresetItem>(enabled.Count);
            for (var i = 0; i < enabled.Count; i++)
            {
                preset.Items.Add(new WorkshopPluginPresetItem
                {
                    WorkshopId = enabled[i].WorkshopId,
                    LoadOrder = i
                });
            }
        }
    }
}
