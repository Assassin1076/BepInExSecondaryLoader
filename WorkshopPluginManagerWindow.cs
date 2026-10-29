using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInExSecondaryLoader.WorkshopPlugins;
using Cysharp.Threading.Tasks;
using NuclearOption.Workshop;
using SFB;
using Steamworks;
using UnityEngine;

namespace BepInExSecondaryLoader
{
    /// <summary>
    /// In-game IMGUI manager for workshop plugin load order and presets.
    /// Changes are persisted immediately but only affect the next game startup.
    /// </summary>
    internal sealed class WorkshopPluginManagerWindow
    {
        private const int WindowId = 0x4E4F5053;

        private Rect _windowRect = new Rect(60f, 60f, 720f, 700f);
        private Vector2 _itemScroll;
        private Vector2 _presetScroll;
        private static readonly ExtensionFilter[] JsonFileFilters =
        {
            new ExtensionFilter("JSON 文件", "json"),
            new ExtensionFilter("所有文件", "*")
        };

        private static SteamWorkshop _helperWorkshop;

        private WorkshopPluginRecordFile _file;
        private bool _visible;
        private bool _importing;
        private string _presetName = string.Empty;
        private string _status = string.Empty;

        public void Toggle()
        {
            _visible = !_visible;
            if (_visible)
            {
                Reload();
            }
        }

        public void OnGUI()
        {
            if (!_visible)
            {
                return;
            }

            if (_file == null)
            {
                Reload();
            }

            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "Workshop Plugins load orders and presets");
        }

        private void Reload()
        {
            _file = WorkshopPluginRecordStore.Load();
            _status = string.Empty;
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label("列表从上到下即加载顺序；插件依赖会覆盖此顺序。修改在下次启动游戏后生效。");

            var oldGuiEnabled = GUI.enabled;
            GUI.enabled = oldGuiEnabled && !_importing;

            DrawItemList();
            GUILayout.Space(8f);
            DrawPresets();
            GUILayout.Space(8f);

            GUI.enabled = oldGuiEnabled;

            GUILayout.BeginHorizontal();
            var oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && !_importing;

            if (GUILayout.Button("Export Settings", GUILayout.Width(100f)))
            {
                ExportToFile();
            }

            if (GUILayout.Button("Import Settings", GUILayout.Width(100f)))
            {
                ImportFromFile();
            }

            if (GUILayout.Button("Refresh List", GUILayout.Width(100f)))
            {
                Reload();
                _status = "已从磁盘刷新列表。";
            }

            GUI.enabled = oldEnabled;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(100f)))
            {
                _visible = false;
            }
            GUILayout.EndHorizontal();

            if (_importing)
            {
                GUILayout.Label("正在导入配置并下载缺失的 mod，请稍候...");
            }

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Label(_status);
            }

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private void DrawItemList()
        {
            var items = _file.Items.OrderBy(record => record.LoadOrder).ToList();
            if (items.Count == 0)
            {
                GUILayout.Label("尚未发现工坊插件。订阅后等待 Steam 同步，或点击“刷新列表”。");
                return;
            }

            _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(320f));
            var moved = false;

            for (var i = 0; i < items.Count; i++)
            {
                var record = items[i];
                GUILayout.BeginHorizontal();

                var oldEnabled = GUI.enabled;

                GUI.enabled = oldEnabled && i > 0;
                if (GUILayout.Button("上", GUILayout.Width(26f)))
                {
                    MoveItem(items, i, -1);
                    moved = true;
                }

                GUI.enabled = oldEnabled && i < items.Count - 1;
                if (GUILayout.Button("下", GUILayout.Width(26f)))
                {
                    MoveItem(items, i, 1);
                    moved = true;
                }

                GUI.enabled = oldEnabled;

                var enabled = GUILayout.Toggle(record.Enabled, string.Empty, GUILayout.Width(20f));
                if (enabled != record.Enabled)
                {
                    record.Enabled = enabled;
                    PersistEdit();
                }

                var label = string.IsNullOrWhiteSpace(record.Name)
                    ? Path.GetFileName(record.Folder)
                    : record.Name;
                var idText = record.WorkshopId == 0 ? "本地" : record.WorkshopId.ToString();
                GUILayout.Label(
                    new GUIContent($"{i + 1:00}. {label}  (ID: {idText})", record.Folder),
                    GUILayout.ExpandWidth(true));

                GUILayout.EndHorizontal();

                if (moved)
                {
                    break;
                }
            }

            GUILayout.EndScrollView();
        }

        private void MoveItem(List<WorkshopPluginRecord> items, int index, int delta)
        {
            var target = index + delta;
            if (target < 0 || target >= items.Count)
            {
                return;
            }

            var record = items[index];
            items.RemoveAt(index);
            items.Insert(target, record);

            for (var i = 0; i < items.Count; i++)
            {
                items[i].LoadOrder = i;
            }

            _file.Items = items;
            PersistEdit();
        }

        private void PersistEdit()
        {
            WorkshopPluginRecordStore.Normalize(_file);
            WorkshopPluginRecordStore.Save(_file);
            _status = WorkshopPresetManager.IsActivePresetDirty(_file)
                ? "已保存。当前预设与列表不一致，可点击“更新当前预设”。"
                : "已保存。修改将在下次启动游戏后生效。";
        }

        private void DrawPresets()
        {
            GUILayout.Label("预设");

            var isDirty = WorkshopPresetManager.IsActivePresetDirty(_file);
            GUILayout.Label(string.IsNullOrEmpty(_file.ActivePreset)
                ? "当前：手动模式"
                : $"当前：{_file.ActivePreset}{(isDirty ? "（有未保存的修改）" : string.Empty)}");

            var presets = _file.Presets
                .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var options = new List<string> { "(手动模式)" };
            options.AddRange(presets.Select(preset => preset.Name));

            var activeIndex = 0;
            if (!string.IsNullOrEmpty(_file.ActivePreset))
            {
                var found = options.FindIndex(option =>
                    string.Equals(option, _file.ActivePreset, StringComparison.OrdinalIgnoreCase));
                if (found > 0)
                {
                    activeIndex = found;
                }
            }

            _presetScroll = GUILayout.BeginScrollView(_presetScroll, GUILayout.Height(84f));
            var selected = GUILayout.SelectionGrid(activeIndex, options.ToArray(), 2);
            GUILayout.EndScrollView();

            if (selected != activeIndex)
            {
                if (selected == 0)
                {
                    WorkshopPresetManager.ClearActivePreset(_file);
                    WorkshopPluginRecordStore.Save(_file);
                    _status = "已切换为手动模式";
                }
                else
                {
                    var preset = WorkshopPresetManager.Find(_file, options[selected]);
                    if (preset != null)
                    {
                        WorkshopPresetManager.ApplyPreset(_file, preset);
                        WorkshopPluginRecordStore.Save(_file);
                        _status = $"已应用预设“{preset.Name}”；重启游戏后生效。";
                    }
                }
            }

            GUILayout.BeginHorizontal();
            _presetName = GUILayout.TextField(_presetName ?? string.Empty, GUILayout.Width(200f));

            if (GUILayout.Button("Save current as a preset", GUILayout.Width(130f)))
            {
                var preset = WorkshopPresetManager.SaveCurrentAsPreset(_file, _presetName);
                if (preset == null)
                {
                    _status = "请输入预设名称。";
                }
                else
                {
                    WorkshopPluginRecordStore.Save(_file);
                    _presetName = preset.Name;
                    _status = $"已保存预设“{preset.Name}”。";
                }
            }

            var canUpdate = !string.IsNullOrEmpty(_file.ActivePreset) && isDirty;
            var canDelete = !string.IsNullOrEmpty(_file.ActivePreset);
            var oldEnabled = GUI.enabled;

            GUI.enabled = oldEnabled && canUpdate;
            if (GUILayout.Button("Update Current Preset", GUILayout.Width(110f)) && canUpdate)
            {
                if (WorkshopPresetManager.UpdateActivePreset(_file))
                {
                    WorkshopPluginRecordStore.Save(_file);
                    _status = $"已更新预设“{_file.ActivePreset}”。";
                }
                else
                {
                    WorkshopPluginRecordStore.Save(_file);
                    _status = "当前预设不存在，已切换为手动模式。";
                }
            }

            GUI.enabled = oldEnabled && canDelete;
            if (GUILayout.Button("Delete Current Preset", GUILayout.Width(110f)) && canDelete)
            {
                var name = _file.ActivePreset;
                if (WorkshopPresetManager.DeletePreset(_file, name))
                {
                    WorkshopPluginRecordStore.Save(_file);
                    _status = $"已删除预设“{name}”，当前为手动模式。";
                }
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();

            GUILayout.Label("提示：依赖插件始终会优先于依赖它的插件加载；预设只保存已启用的插件组，预设模式下新订阅的插件默认不启用。");
            GUILayout.Label("导出时会尝试下载未知ID的mod");
        }

        private void ExportToFile()
        {
            try
            {
                WorkshopPluginRecordStore.Normalize(_file);
                Directory.CreateDirectory(WorkshopPluginRecordStore.DirectoryPath);

                var path = StandaloneFileBrowser.SaveFilePanel(
                    "导出工坊插件配置",
                    WorkshopPluginRecordStore.DirectoryPath,
                    "workshop-plugins-export.json",
                    JsonFileFilters);

                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                if (string.IsNullOrEmpty(Path.GetExtension(path)))
                {
                    path += ".json";
                }

                _status = WorkshopPluginTransfer.TryWriteExport(_file, path, out var error)
                    ? $"已导出配置：{path}"
                    : $"导出失败：{error}";
            }
            catch (Exception ex)
            {
                _status = $"导出失败：{ex.Message}";
                Plugin.Logger?.LogError($"Failed to export workshop plugin configuration: {ex}");
            }
        }

        private void ImportFromFile()
        {
            try
            {
                Directory.CreateDirectory(WorkshopPluginRecordStore.DirectoryPath);

                var paths = StandaloneFileBrowser.OpenFilePanel(
                    "导入工坊插件配置",
                    WorkshopPluginRecordStore.DirectoryPath,
                    JsonFileFilters,
                    false);

                if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                {
                    return;
                }

                ImportFromFileAsync(paths[0]);
            }
            catch (Exception ex)
            {
                _status = $"导入失败：{ex.Message}";
                Plugin.Logger?.LogError($"Failed to open workshop plugin configuration: {ex}");
            }
        }

        private async void ImportFromFileAsync(string path)
        {
            if (_importing)
            {
                return;
            }

            _importing = true;
            try
            {
                if (!WorkshopPluginTransfer.TryReadExport(path, out var export, out var error))
                {
                    _status = $"导入失败：{error}";
                    Plugin.Logger?.LogWarning($"Failed to read workshop plugin configuration '{path}': {error}");
                    return;
                }

                var recordFile = WorkshopPluginRecordStore.Load();
                _file = recordFile;

                var result = WorkshopPluginTransfer.ApplyExport(recordFile, export);
                WorkshopPluginRecordStore.Save(recordFile);

                _status = $"已导入 {result.ImportedMods} 个 mod、{result.ImportedPresets} 个预设" +
                    (result.AddedMods != 0 ? $"，新增 {result.AddedMods} 个" : string.Empty) +
                    "。";

                if (result.UnknownMods.Count != 0)
                {
                    await DownloadUnknownModsAsync(recordFile, result.UnknownMods);
                }
                else
                {
                    _status += " 重启游戏后生效。";
                }

                _file = WorkshopPluginRecordStore.Load();
            }
            catch (Exception ex)
            {
                _status = $"导入失败：{ex.Message}";
                Plugin.Logger?.LogError($"Failed to import workshop plugin configuration: {ex}");
            }
            finally
            {
                _importing = false;
            }
        }

        private async UniTask DownloadUnknownModsAsync(WorkshopPluginRecordFile recordFile, List<ulong> unknownMods)
        {
            if (!SteamManager.ClientInitialized)
            {
                _status += " Steam 未初始化，无法下载未知 mod；请重启游戏并再次导入。";
                return;
            }

            SteamWorkshop workshop;
            try
            {
                workshop = GetSteamWorkshop();
            }
            catch (Exception ex)
            {
                _status += $" 无法创建 SteamWorkshop 组件：{ex.Message}";
                Plugin.Logger?.LogError($"Failed to create SteamWorkshop helper: {ex}");
                return;
            }

            if (workshop == null)
            {
                _status += " 无法获取 SteamWorkshop 组件，未知 mod 未下载。";
                return;
            }

            var failed = 0;
            for (var i = 0; i < unknownMods.Count; i++)
            {
                var workshopId = unknownMods[i];
                var record = recordFile.Items.FirstOrDefault(item => item != null && item.WorkshopId == workshopId);
                var displayName = string.IsNullOrWhiteSpace(record?.Name)
                    ? workshopId.ToString()
                    : record.Name;
                var label = $"正在下载 {i + 1}/{unknownMods.Count}：{displayName}";

                var item = new SteamWorkshopItem(
                    displayName,
                    new[] { PluginMod.MOD_NAME },
                    new PublishedFileId_t(workshopId));
                var progress = new DownloadProgress(value =>
                {
                    var percent = float.IsNaN(value) ? 0f : Mathf.Clamp01(value);
                    _status = $"{label} {percent:P0}";
                });

                try
                {
                    _status = label;
                    await workshop.DownloadItem(item, progress);
                    UpdateRecordLocation(record, workshopId);
                    WorkshopPluginRecordStore.Save(recordFile);
                    _status = $"{label} 完成";
                }
                catch (Exception ex)
                {
                    failed++;
                    Plugin.Logger?.LogError($"Failed to download workshop plugin {workshopId}: {ex}");
                    _status = $"{label} 失败：{ex.Message}";
                }

                await UniTask.Yield();
            }

            WorkshopPluginRecordStore.Save(recordFile);
            _status = $"导入完成；下载 {unknownMods.Count - failed}/{unknownMods.Count} 个未知 mod。重启游戏后按新配置加载。";
        }

        private static void UpdateRecordLocation(WorkshopPluginRecord record, ulong workshopId)
        {
            if (record == null)
            {
                return;
            }

            try
            {
                if (!SteamWorkshop.TryGetInstallFolder(new PublishedFileId_t(workshopId), out var installFolder) ||
                    string.IsNullOrEmpty(installFolder))
                {
                    return;
                }

                if (PluginPackage.TryFindPackageRoot(installFolder, out var packageRoot, out var manifest, out _))
                {
                    record.Folder = packageRoot;
                    if (string.IsNullOrWhiteSpace(record.Name) && !string.IsNullOrWhiteSpace(manifest?.Name))
                    {
                        record.Name = manifest.Name.Trim();
                    }
                }
                else
                {
                    record.Folder = installFolder;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed to resolve install folder for workshop item {workshopId}: {ex.Message}");
            }
        }

        private static SteamWorkshop GetSteamWorkshop()
        {
            if (_helperWorkshop != null)
            {
                return _helperWorkshop;
            }

            var existing = Resources.FindObjectsOfTypeAll<SteamWorkshop>()
                .FirstOrDefault(workshop =>
                    workshop != null &&
                    workshop.gameObject != null &&
                    workshop.gameObject.scene.IsValid());
            if (existing != null)
            {
                return _helperWorkshop = existing;
            }

            var helper = new GameObject("BepInExSecondaryLoader_SteamWorkshop");
            helper.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(helper);
            return _helperWorkshop = helper.AddComponent<SteamWorkshop>();
        }

        private sealed class DownloadProgress : IProgress<float>
        {
            private readonly Action<float> _report;

            public DownloadProgress(Action<float> report)
            {
                _report = report;
            }

            public void Report(float value)
            {
                _report?.Invoke(value);
            }
        }
    }
}
