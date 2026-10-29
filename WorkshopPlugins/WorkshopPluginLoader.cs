using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepPluginInfo = BepInEx.PluginInfo;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    /// <summary>
    /// Two-stage loader for BepInEx plugins that live in Steam Workshop item folders.
    /// Stage 1 prepares a dependency-ordered plan from metadata only; stage 2 loads the
    /// assemblies and attaches the plugin components to the BepInEx manager object.
    /// </summary>
    internal static class WorkshopPluginLoader
    {
        private static readonly object SyncRoot = new object();
        private static readonly List<string> SearchDirectories = new List<string>();
        private static readonly Dictionary<string, Assembly> AssembliesByPath =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Version ZeroVersion = new Version();

        private static readonly PropertyInfo LocationProperty =
            typeof(BepPluginInfo).GetProperty("Location", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly PropertyInfo InstanceProperty =
            typeof(BepPluginInfo).GetProperty("Instance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool resolverInstalled;

        private sealed class LoadPlan
        {
            public List<WorkshopPluginCandidate> Plugins = new List<WorkshopPluginCandidate>();
            public int ScannedFolders;
            public int DiscoveredPlugins;
            public int SkippedPlugins;
        }

        public static void InstallAssemblyResolver()
        {
            lock (SyncRoot)
            {
                if (resolverInstalled)
                {
                    return;
                }

                resolverInstalled = true;
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
        }

        public static void AddSearchDirectory(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            lock (SyncRoot)
            {
                if (!SearchDirectories.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    SearchDirectories.Add(folder);
                }
            }
        }

        public static void AddSearchDirectoryTree(string root)
        {
            AddSearchDirectory(root);

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                {
                    if (Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any())
                    {
                        AddSearchDirectory(directory);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"Failed to enumerate dependency folders under '{root}': {ex.Message}");
            }
        }

        public static int LoadRecords(IEnumerable<WorkshopPluginRecord> records)
        {
            if (records == null)
            {
                return 0;
            }

            var ordered = records
                .Where(record => record != null && record.Enabled && !string.IsNullOrEmpty(record.Folder))
                .OrderBy(record => record.LoadOrder)
                .ThenBy(record => record.WorkshopId)
                .ToList();

            var plan = Prepare(ordered);
            return Activate(plan);
        }

        /// <summary>
        /// Stage 1: discover plugin metadata, drop unusable plugins and order the rest by user
        /// priority first, then by plugin dependencies. No plugin code runs during this stage.
        /// </summary>
        private static LoadPlan Prepare(IEnumerable<WorkshopPluginRecord> records)
        {
            var plan = new LoadPlan();
            var candidates = new List<WorkshopPluginCandidate>();
            var visitedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records)
            {
                plan.ScannedFolders++;

                if (!Directory.Exists(record.Folder))
                {
                    Plugin.Logger?.LogWarning($"Workshop plugin folder no longer exists: '{record.Folder}'.");
                    continue;
                }

                if (!PluginPackage.TryFindPackageRoot(record.Folder, out var packageRoot, out var manifest, out var packageError))
                {
                    plan.SkippedPlugins++;
                    Plugin.Logger?.LogWarning($"Skipping plugin package '{record.Folder}': {packageError}");
                    continue;
                }

                if (!visitedRoots.Add(packageRoot))
                {
                    continue;
                }

                AddSearchDirectoryTree(packageRoot);
                var scanned = WorkshopPluginScanner.ScanPackage(packageRoot, manifest);
                for (var i = 0; i < scanned.Count; i++)
                {
                    scanned[i].LoadOrder = record.LoadOrder;
                    scanned[i].Sequence = i;
                }

                plan.DiscoveredPlugins += scanned.Count;
                candidates.AddRange(scanned);
            }

            var chosen = new Dictionary<string, WorkshopPluginCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates.GroupBy(candidate => candidate.Info.Metadata.GUID, StringComparer.OrdinalIgnoreCase))
            {
                var guid = group.Key;

                if (Chainloader.PluginInfos.ContainsKey(guid) || LoadedGuids.Contains(guid))
                {
                    plan.SkippedPlugins += group.Count();
                    Plugin.Logger?.LogDebug($"Skipping workshop plugin '{guid}' because it is already loaded.");
                    continue;
                }

                var best = group
                    .OrderByDescending(candidate => candidate.Info.Metadata.Version)
                    .ThenBy(candidate => candidate.LoadOrder)
                    .ThenBy(candidate => candidate.Sequence)
                    .ThenBy(candidate => candidate.TypeName, StringComparer.OrdinalIgnoreCase)
                    .First();
                chosen[guid] = best;
                plan.SkippedPlugins += group.Count() - 1;

                if (group.Count() > 1)
                {
                    Plugin.Logger?.LogWarning($"Multiple workshop plugin assemblies provide '{guid}'; using version {best.Info.Metadata.Version} from '{best.DllPath}'.");
                }
            }

            var available = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Chainloader.PluginInfos)
            {
                if (pair.Value?.Metadata != null)
                {
                    available[pair.Key] = pair.Value.Metadata.Version;
                }
            }
            foreach (var candidate in chosen.Values)
            {
                available[candidate.Info.Metadata.GUID] = candidate.Info.Metadata.Version;
            }

            var accepted = new Dictionary<string, WorkshopPluginCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in chosen.Values)
            {
                if (!PassesProcessFilter(candidate.Info))
                {
                    plan.SkippedPlugins++;
                    Plugin.Logger?.LogInfo($"Skipping workshop plugin '{candidate.Info.Metadata.GUID}' because it targets other processes.");
                    continue;
                }

                if (HasIncompatibility(candidate.Info, chosen))
                {
                    plan.SkippedPlugins++;
                    Plugin.Logger?.LogError($"Skipping workshop plugin '{candidate.Info.Metadata.GUID}' because it is incompatible with another loaded plugin.");
                    continue;
                }

                var dependencyError = GetUnsatisfiedDependencyReason(candidate.Info, available);
                if (dependencyError != null)
                {
                    plan.SkippedPlugins++;
                    Plugin.Logger?.LogError($"Skipping workshop plugin '{candidate.Info.Metadata.GUID}' because {dependencyError}.");
                    continue;
                }

                accepted[candidate.Info.Metadata.GUID] = candidate;
            }

            // A plugin that was accepted above may depend on a plugin from this batch that was
            // dropped. Repeat until no accepted plugin is left with a missing hard dependency.
            bool removedAny;
            do
            {
                removedAny = false;
                foreach (var candidate in accepted.Values.ToList())
                {
                    var guid = candidate.Info.Metadata.GUID;
                    var dependencies = candidate.Info.Dependencies ?? Enumerable.Empty<BepInDependency>();
                    if (dependencies.Any(dependency =>
                        IsHardDependency(dependency) &&
                        chosen.ContainsKey(dependency.DependencyGUID) &&
                        !accepted.ContainsKey(dependency.DependencyGUID)))
                    {
                        accepted.Remove(guid);
                        plan.SkippedPlugins++;
                        removedAny = true;
                        Plugin.Logger?.LogError($"Skipping workshop plugin '{guid}' because one of its dependencies was skipped.");
                    }
                }
            }
            while (removedAny);

            plan.Plugins = SortByDependencies(accepted);
            return plan;
        }

        /// <summary>
        /// Stage 2: load the assemblies and let Unity invoke the plugins' Awake/Start callbacks.
        /// </summary>
        private static int Activate(LoadPlan plan)
        {
            if (plan.Plugins.Count == 0)
            {
                if (plan.ScannedFolders != 0)
                {
                    Plugin.Logger?.LogInfo($"No loadable workshop plugins found ({plan.DiscoveredPlugins} discovered, {plan.SkippedPlugins} skipped).");
                }
                return 0;
            }

            if (Chainloader.ManagerObject == null)
            {
                Plugin.Logger?.LogError("Cannot activate workshop plugins because the BepInEx manager object is unavailable.");
                return 0;
            }

            Plugin.Logger?.LogInfo(
                "Workshop plugin load order: " +
                string.Join(" -> ", plan.Plugins.Select(plugin => plugin.Info.Metadata.GUID)));

            var loaded = 0;
            foreach (var candidate in plan.Plugins)
            {
                if (ActivatePlugin(candidate))
                {
                    loaded++;
                }
            }

            Plugin.Logger?.LogInfo($"Loaded {loaded}/{plan.Plugins.Count} workshop plugin(s) from {plan.ScannedFolders} item folder(s).");
            return loaded;
        }

        private static bool ActivatePlugin(WorkshopPluginCandidate candidate)
        {
            var info = candidate.Info;
            var guid = info.Metadata.GUID;

            try
            {
                var assembly = LoadAssembly(candidate.DllPath);
                var pluginType = assembly.GetType(candidate.TypeName, throwOnError: true);
                if (!typeof(BaseUnityPlugin).IsAssignableFrom(pluginType))
                {
                    throw new InvalidOperationException($"Type '{candidate.TypeName}' does not derive from BaseUnityPlugin.");
                }

                SetInternalProperty(LocationProperty, info, candidate.DllPath);
                LoadedGuids.Add(guid);
                Chainloader.PluginInfos[guid] = info;

                try
                {
                    var instance = (BaseUnityPlugin)Chainloader.ManagerObject.AddComponent(pluginType);
                    SetInternalProperty(InstanceProperty, info, instance);
                    Plugin.Logger?.LogInfo($"Loaded workshop plugin [{info}] from '{candidate.DllPath}'.");
                    return true;
                }
                catch
                {
                    Chainloader.PluginInfos.Remove(guid);
                    LoadedGuids.Remove(guid);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to load workshop plugin [{info.Metadata?.Name}] ({guid}) from '{candidate.DllPath}': {ex}");
                return false;
            }
        }

        private static Assembly LoadAssembly(string path)
        {
            lock (SyncRoot)
            {
                if (AssembliesByPath.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }

            var assembly = Assembly.LoadFile(path);

            lock (SyncRoot)
            {
                AssembliesByPath[path] = assembly;
            }

            return assembly;
        }

        private static List<WorkshopPluginCandidate> SortByDependencies(Dictionary<string, WorkshopPluginCandidate> plugins)
        {
            var sorted = new List<WorkshopPluginCandidate>();
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void Visit(string guid)
            {
                if (state.TryGetValue(guid, out var currentState))
                {
                    if (currentState == 1)
                    {
                        Plugin.Logger?.LogWarning($"Detected a dependency cycle involving workshop plugin '{guid}'.");
                    }
                    return;
                }

                state[guid] = 1;

                var dependencies = plugins[guid].Info.Dependencies ?? Enumerable.Empty<BepInDependency>();
                foreach (var dependency in dependencies)
                {
                    if (plugins.ContainsKey(dependency.DependencyGUID))
                    {
                        Visit(dependency.DependencyGUID);
                    }
                }

                state[guid] = 2;
                sorted.Add(plugins[guid]);
            }

            foreach (var candidate in plugins.Values
                .OrderBy(candidate => candidate.LoadOrder)
                .ThenBy(candidate => candidate.Sequence)
                .ThenBy(candidate => candidate.TypeName, StringComparer.OrdinalIgnoreCase))
            {
                Visit(candidate.Info.Metadata.GUID);
            }

            return sorted;
        }

        private static bool PassesProcessFilter(BepPluginInfo info)
        {
            var processes = info.Processes?.ToList();
            if (processes == null || processes.Count == 0)
            {
                return true;
            }

            return processes.Any(process =>
            {
                if (process == null)
                {
                    return false;
                }

                var name = (process.ProcessName ?? string.Empty).Replace(".exe", string.Empty);
                return string.Equals(name, Paths.ProcessName, StringComparison.InvariantCultureIgnoreCase);
            });
        }

        private static bool HasIncompatibility(BepPluginInfo info, Dictionary<string, WorkshopPluginCandidate> batch)
        {
            var incompatibilities = info.Incompatibilities ?? Enumerable.Empty<BepInIncompatibility>();
            return incompatibilities.Any(incompatibility =>
                Chainloader.PluginInfos.ContainsKey(incompatibility.IncompatibilityGUID) ||
                batch.ContainsKey(incompatibility.IncompatibilityGUID));
        }

        private static string GetUnsatisfiedDependencyReason(BepPluginInfo info, Dictionary<string, Version> available)
        {
            var dependencies = info.Dependencies ?? Enumerable.Empty<BepInDependency>();
            foreach (var dependency in dependencies)
            {
                if (!available.TryGetValue(dependency.DependencyGUID, out var version))
                {
                    if (IsHardDependency(dependency))
                    {
                        return $"it requires missing plugin '{dependency.DependencyGUID}'";
                    }
                    continue;
                }

                var minimum = dependency.MinimumVersion;
                if (minimum != null && minimum != ZeroVersion && version < minimum)
                {
                    return $"it requires plugin '{dependency.DependencyGUID}' version {minimum} or newer, but version {version} is available";
                }
            }

            return null;
        }

        private static bool IsHardDependency(BepInDependency dependency)
        {
            return dependency != null &&
                (dependency.Flags & BepInDependency.DependencyFlags.HardDependency) != 0;
        }

        private static void SetInternalProperty(PropertyInfo property, object target, object value)
        {
            if (property == null)
            {
                return;
            }

            try
            {
                property.SetValue(target, value, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"Failed to set '{property.Name}' on the plugin info: {ex.Message}");
            }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var fileName = requested.Name + ".dll";

                var requesting = args.RequestingAssembly;
                if (requesting != null && !string.IsNullOrEmpty(requesting.Location))
                {
                    var local = TryResolveFromDirectory(Path.GetDirectoryName(requesting.Location), requested, fileName);
                    if (local != null)
                    {
                        return local;
                    }
                }

                List<string> directories;
                lock (SyncRoot)
                {
                    directories = new List<string>(SearchDirectories);
                }

                foreach (var directory in directories)
                {
                    var resolved = TryResolveFromDirectory(directory, requested, fileName);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"Assembly resolution for '{args.Name}' failed: {ex.Message}");
            }

            return null;
        }

        private static Assembly TryResolveFromDirectory(string directory, AssemblyName requested, string fileName)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.IsDynamic)
                {
                    continue;
                }

                try
                {
                    if (AssemblyName.ReferenceMatchesDefinition(requested, loaded.GetName()))
                    {
                        return loaded;
                    }
                }
                catch (Exception)
                {
                    // Ignore assemblies with unreadable metadata.
                }
            }

            try
            {
                return Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"Failed to load dependency '{path}': {ex.Message}");
                return null;
            }
        }
    }
}
