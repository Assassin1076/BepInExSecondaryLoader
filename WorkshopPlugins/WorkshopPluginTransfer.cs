using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    /// <summary>
    /// Portable representation of the load list and presets. Mods are keyed by workshop id and
    /// contain no filesystem paths, so the file can be moved between machines.
    /// </summary>
    internal sealed class WorkshopPluginExportFile
    {
        public int FormatVersion { get; set; } = 1;

        public string ActivePreset { get; set; }

        public Dictionary<string, WorkshopPluginExportMod> Mods { get; set; } =
            new Dictionary<string, WorkshopPluginExportMod>();

        public Dictionary<string, List<WorkshopPluginExportPresetItem>> Presets { get; set; } =
            new Dictionary<string, List<WorkshopPluginExportPresetItem>>();
    }

    internal sealed class WorkshopPluginExportMod
    {
        public string Name { get; set; }

        public bool Enabled { get; set; }

        public int LoadOrder { get; set; }
    }

    internal sealed class WorkshopPluginExportPresetItem
    {
        public ulong WorkshopId { get; set; }

        public int LoadOrder { get; set; }
    }

    internal sealed class WorkshopPluginImportResult
    {
        public int ImportedMods;
        public int AddedMods;
        public int ImportedPresets;

        /// <summary>
        /// Workshop ids referenced by the import whose local content is missing.
        /// </summary>
        public List<ulong> UnknownMods = new List<ulong>();
    }

    internal static class WorkshopPluginTransfer
    {
        public static WorkshopPluginExportFile CreateExport(WorkshopPluginRecordFile file)
        {
            var export = new WorkshopPluginExportFile
            {
                ActivePreset = file?.ActivePreset
            };

            if (file == null)
            {
                return export;
            }

            foreach (var record in file.Items
                .Where(record => record != null && record.WorkshopId != 0)
                .OrderBy(record => record.LoadOrder))
            {
                export.Mods[record.WorkshopId.ToString()] = new WorkshopPluginExportMod
                {
                    Name = record.Name,
                    Enabled = record.Enabled,
                    LoadOrder = record.LoadOrder
                };
            }

            foreach (var preset in file.Presets)
            {
                if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
                {
                    continue;
                }

                var items = new List<WorkshopPluginExportPresetItem>();
                foreach (var item in (preset.Items ?? Enumerable.Empty<WorkshopPluginPresetItem>())
                    .Where(item => item != null && item.WorkshopId != 0)
                    .OrderBy(item => item.LoadOrder))
                {
                    items.Add(new WorkshopPluginExportPresetItem
                    {
                        WorkshopId = item.WorkshopId,
                        LoadOrder = item.LoadOrder
                    });

                    var id = item.WorkshopId.ToString();
                    if (!export.Mods.ContainsKey(id))
                    {
                        export.Mods[id] = new WorkshopPluginExportMod
                        {
                            Enabled = false,
                            LoadOrder = item.LoadOrder
                        };
                    }
                }

                export.Presets[preset.Name] = items;
            }

            return export;
        }

        public static bool TryWriteExport(WorkshopPluginRecordFile file, string path, out string error)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(CreateExport(file), Formatting.Indented));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads a configuration file tolerantly. Both the exported dictionary shape and the
        /// array-based local record shape (workshop-plugins.json) are accepted.
        /// </summary>
        public static bool TryReadExport(string path, out WorkshopPluginExportFile export, out string error)
        {
            export = null;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = $"文件不存在：{path}";
                    return false;
                }

                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = "文件为空。";
                    return false;
                }

                JToken token;
                try
                {
                    token = JToken.Parse(text);
                }
                catch (Exception ex)
                {
                    error = $"不是有效的 JSON：{ex.Message}";
                    return false;
                }

                var root = token as JObject;
                if (root == null)
                {
                    error = "配置文件的根节点必须是 JSON 对象。";
                    return false;
                }

                export = ReadExport(root, out error);
                return export != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static WorkshopPluginExportFile ReadExport(JObject root, out string error)
        {
            error = null;

            var modsToken = GetProperty(root, "Mods") ?? GetProperty(root, "Items");
            var presetsToken = GetProperty(root, "Presets");

            if (modsToken == null && presetsToken == null && GetProperty(root, "FormatVersion") == null)
            {
                error = "文件格式无法识别：缺少 Mods/Items 或 Presets 字段。";
                return null;
            }

            var export = new WorkshopPluginExportFile
            {
                FormatVersion = ReadInt(GetProperty(root, "FormatVersion")) ?? 1,
                ActivePreset = ReadString(GetProperty(root, "ActivePreset"))
            };

            if (modsToken != null)
            {
                ReadMods(modsToken, export);
            }

            if (presetsToken != null)
            {
                ReadPresets(presetsToken, export);
            }

            return export;
        }

        private static void ReadMods(JToken token, WorkshopPluginExportFile export)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (!ulong.TryParse(property.Name, out var workshopId) || workshopId == 0)
                    {
                        continue;
                    }

                    var mod = property.Value as JObject;
                    export.Mods[workshopId.ToString()] = new WorkshopPluginExportMod
                    {
                        Name = ReadString(mod, "Name"),
                        Enabled = ReadBool(mod, "Enabled") ?? true,
                        LoadOrder = ReadInt(mod, "LoadOrder") ?? 0
                    };
                }

                return;
            }

            if (token is JArray array)
            {
                foreach (var element in array)
                {
                    if (!(element is JObject mod))
                    {
                        continue;
                    }

                    var workshopId =
                        ReadUlong(mod, "WorkshopId") ??
                        ReadUlong(mod, "PublishedFileId") ??
                        ReadUlong(mod, "Id");
                    if (!workshopId.HasValue || workshopId.Value == 0)
                    {
                        continue;
                    }

                    export.Mods[workshopId.Value.ToString()] = new WorkshopPluginExportMod
                    {
                        Name = ReadString(mod, "Name"),
                        Enabled = ReadBool(mod, "Enabled") ?? true,
                        LoadOrder = ReadInt(mod, "LoadOrder") ?? 0
                    };
                }

                return;
            }

            Plugin.Logger?.LogWarning($"Ignored 'Mods' because its JSON type is {token.Type}.");
        }

        private static void ReadPresets(JToken token, WorkshopPluginExportFile export)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    var name = property.Name?.Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var itemsToken = property.Value is JObject wrapper
                        ? GetProperty(wrapper, "Items")
                        : property.Value;

                    export.Presets[name] = ReadPresetItems(itemsToken);
                }

                return;
            }

            if (token is JArray array)
            {
                var namelessIndex = 1;
                foreach (var element in array)
                {
                    if (element is JArray rawItems)
                    {
                        export.Presets[$"Preset {namelessIndex++}"] = ReadPresetItems(rawItems);
                        continue;
                    }

                    if (!(element is JObject preset))
                    {
                        continue;
                    }

                    var name = ReadString(preset, "Name")?.Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        name = $"Preset {namelessIndex++}";
                    }

                    export.Presets[name] = ReadPresetItems(GetProperty(preset, "Items"));
                }

                return;
            }

            Plugin.Logger?.LogWarning($"Ignored 'Presets' because its JSON type is {token.Type}.");
        }

        private static List<WorkshopPluginExportPresetItem> ReadPresetItems(JToken token)
        {
            var items = new List<WorkshopPluginExportPresetItem>();

            if (!(token is JArray array))
            {
                return items;
            }

            foreach (var element in array)
            {
                if (element is JObject item)
                {
                    var workshopId =
                        ReadUlong(item, "WorkshopId") ??
                        ReadUlong(item, "PublishedFileId") ??
                        ReadUlong(item, "Id");
                    if (!workshopId.HasValue || workshopId.Value == 0)
                    {
                        continue;
                    }

                    items.Add(new WorkshopPluginExportPresetItem
                    {
                        WorkshopId = workshopId.Value,
                        LoadOrder = ReadInt(item, "LoadOrder") ?? items.Count
                    });
                }
                else if (element.Type == JTokenType.Integer)
                {
                    var workshopId = element.Value<ulong>();
                    if (workshopId != 0)
                    {
                        items.Add(new WorkshopPluginExportPresetItem
                        {
                            WorkshopId = workshopId,
                            LoadOrder = items.Count
                        });
                    }
                }
            }

            return items;
        }

        private static JToken GetProperty(JObject obj, string name)
        {
            if (obj == null)
            {
                return null;
            }

            foreach (var property in obj.Properties())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        private static string ReadString(JToken token, string name)
        {
            return ReadString(GetProperty(token as JObject, name));
        }

        private static string ReadString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        private static int? ReadInt(JToken token, string name)
        {
            return ReadInt(GetProperty(token as JObject, name));
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return token.Value<int>();
            }

            return int.TryParse(token.ToString(), out var value) ? value : (int?)null;
        }

        private static ulong? ReadUlong(JToken token, string name)
        {
            return ReadUlong(GetProperty(token as JObject, name));
        }

        private static ulong? ReadUlong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<ulong>();
            }

            return ulong.TryParse(token.ToString(), out var value) ? value : (ulong?)null;
        }

        private static bool? ReadBool(JToken token, string name)
        {
            return ReadBool(GetProperty(token as JObject, name));
        }

        private static bool? ReadBool(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            return bool.TryParse(token.ToString(), out var value) ? value : (bool?)null;
        }

        public static WorkshopPluginImportResult ApplyExport(
            WorkshopPluginRecordFile file,
            WorkshopPluginExportFile export)
        {
            var result = new WorkshopPluginImportResult();
            if (file == null || export == null)
            {
                return result;
            }

            if (export.Mods == null)
            {
                export.Mods = new Dictionary<string, WorkshopPluginExportMod>();
            }

            if (export.Presets == null)
            {
                export.Presets = new Dictionary<string, List<WorkshopPluginExportPresetItem>>();
            }

            if (file.Items == null)
            {
                file.Items = new List<WorkshopPluginRecord>();
            }

            if (file.Presets == null)
            {
                file.Presets = new List<WorkshopPluginPreset>();
            }

            var localById = new Dictionary<ulong, WorkshopPluginRecord>();
            foreach (var record in file.Items.Where(record => record != null && record.WorkshopId != 0))
            {
                localById[record.WorkshopId] = record;
            }

            var importedMods = new List<KeyValuePair<ulong, WorkshopPluginExportMod>>();
            var seenModIds = new HashSet<ulong>();
            foreach (var pair in export.Mods)
            {
                if (!ulong.TryParse(pair.Key, out var workshopId) || workshopId == 0 || pair.Value == null)
                {
                    continue;
                }

                if (!seenModIds.Add(workshopId))
                {
                    continue;
                }

                importedMods.Add(new KeyValuePair<ulong, WorkshopPluginExportMod>(workshopId, pair.Value));
            }

            importedMods.Sort((left, right) =>
            {
                var order = left.Value.LoadOrder.CompareTo(right.Value.LoadOrder);
                return order != 0 ? order : left.Key.CompareTo(right.Key);
            });

            var importedIds = new HashSet<ulong>();
            var nextLoadOrder = 0;

            foreach (var pair in importedMods)
            {
                var workshopId = pair.Key;
                if (!localById.TryGetValue(workshopId, out var record))
                {
                    record = new WorkshopPluginRecord
                    {
                        WorkshopId = workshopId,
                        Folder = string.Empty
                    };
                    file.Items.Add(record);
                    localById[workshopId] = record;
                    result.AddedMods++;
                }

                if (!string.IsNullOrWhiteSpace(pair.Value.Name))
                {
                    record.Name = pair.Value.Name.Trim();
                }

                record.Enabled = pair.Value.Enabled;
                record.LoadOrder = nextLoadOrder++;
                record.UpdatedUtc = DateTime.UtcNow;

                importedIds.Add(workshopId);
                result.ImportedMods++;
            }

            // Keep local-only mods, but append them after the imported list.
            foreach (var record in file.Items
                .Where(record => record != null && !importedIds.Contains(record.WorkshopId))
                .OrderBy(record => record.LoadOrder))
            {
                record.LoadOrder = nextLoadOrder++;
            }

            foreach (var pair in export.Presets)
            {
                var name = pair.Key?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var preset = WorkshopPresetManager.Find(file, name);
                if (preset == null)
                {
                    preset = new WorkshopPluginPreset { Name = name };
                    file.Presets.Add(preset);
                }

                preset.Name = name;
                preset.UpdatedUtc = DateTime.UtcNow;

                var presetItems = new List<WorkshopPluginPresetItem>();
                var presetOrder = 0;
                foreach (var item in (pair.Value ?? Enumerable.Empty<WorkshopPluginExportPresetItem>())
                    .Where(item => item != null && item.WorkshopId != 0)
                    .OrderBy(item => item.LoadOrder))
                {
                    presetItems.Add(new WorkshopPluginPresetItem
                    {
                        WorkshopId = item.WorkshopId,
                        LoadOrder = presetOrder++
                    });

                    if (!localById.ContainsKey(item.WorkshopId))
                    {
                        var placeholder = new WorkshopPluginRecord
                        {
                            WorkshopId = item.WorkshopId,
                            Folder = string.Empty,
                            Enabled = false,
                            LoadOrder = nextLoadOrder++
                        };
                        file.Items.Add(placeholder);
                        localById[item.WorkshopId] = placeholder;
                    }
                }

                preset.Items = presetItems;
                result.ImportedPresets++;
            }

            file.ActivePreset = export.ActivePreset;
            WorkshopPluginRecordStore.Normalize(file);

            var referencedIds = new HashSet<ulong>(importedIds);
            foreach (var items in export.Presets.Values)
            {
                foreach (var item in items ?? Enumerable.Empty<WorkshopPluginExportPresetItem>())
                {
                    if (item != null && item.WorkshopId != 0)
                    {
                        referencedIds.Add(item.WorkshopId);
                    }
                }
            }

            result.UnknownMods = referencedIds
                .Where(workshopId =>
                {
                    if (!localById.TryGetValue(workshopId, out var record))
                    {
                        return true;
                    }

                    return string.IsNullOrEmpty(record.Folder) || !Directory.Exists(record.Folder);
                })
                .OrderBy(workshopId => localById.TryGetValue(workshopId, out var record) ? record.LoadOrder : int.MaxValue)
                .ToList();

            return result;
        }
    }
}
