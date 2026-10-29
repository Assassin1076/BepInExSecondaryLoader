using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Mono.Cecil;
using BepPluginInfo = BepInEx.PluginInfo;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    internal sealed class WorkshopPluginCandidate
    {
        public string DllPath;
        public BepPluginInfo Info;
        public string TypeName;
        public int LoadOrder;
        public int Sequence;
    }

    /// <summary>
    /// Reads BepInEx plugin metadata from a folder without loading the assemblies into the AppDomain.
    /// </summary>
    internal static class WorkshopPluginScanner
    {
        private static readonly PropertyInfo TypeNameProperty =
            typeof(BepPluginInfo).GetProperty("TypeName", BindingFlags.Instance | BindingFlags.NonPublic);

        public static List<WorkshopPluginCandidate> ScanPackage(string packageRoot, PluginManifest manifest)
        {
            var candidates = new List<WorkshopPluginCandidate>();
            if (string.IsNullOrEmpty(packageRoot) || !Directory.Exists(packageRoot))
            {
                return candidates;
            }

            AddSearchDirectories(packageRoot);

            if (manifest?.EntryAssemblies != null && manifest.EntryAssemblies.Count != 0)
            {
                foreach (var entry in manifest.EntryAssemblies)
                {
                    if (!PluginPackage.TryResolveFile(packageRoot, entry, out var dllPath, out var error))
                    {
                        Plugin.Logger?.LogWarning($"Could not load entry assembly '{entry}' from '{packageRoot}': {error}");
                        continue;
                    }

                    if (!dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Logger?.LogWarning($"Entry assembly '{entry}' in '{packageRoot}' is not a .dll file.");
                        continue;
                    }

                    ScanAssembly(dllPath, candidates);
                }
            }
            else
            {
                ScanDirectory(packageRoot, candidates);
            }

            return candidates
                .GroupBy(candidate => candidate.DllPath + "|" + candidate.TypeName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static void ScanDirectory(string packageRoot, List<WorkshopPluginCandidate> candidates)
        {
            Dictionary<string, List<BepPluginInfo>> found;
            try
            {
                found = TypeLoader.FindPluginTypes(packageRoot, Chainloader.ToPluginInfo, IsBepInExPluginAssembly, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to scan plugin package '{packageRoot}': {ex}");
                return;
            }

            foreach (var assembly in found.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var info in assembly.Value)
                {
                    AddCandidate(candidates, assembly.Key, info);
                }
            }
        }

        private static void ScanAssembly(string dllPath, List<WorkshopPluginCandidate> candidates)
        {
            try
            {
                using (var assembly = AssemblyDefinition.ReadAssembly(dllPath, TypeLoader.ReaderParameters))
                {
                    if (!IsBepInExPluginAssembly(assembly))
                    {
                        return;
                    }

                    foreach (var type in assembly.MainModule.Types)
                    {
                        BepPluginInfo info;
                        try
                        {
                            info = Chainloader.ToPluginInfo(type);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger?.LogDebug($"Failed to read plugin metadata from '{type.FullName}' in '{dllPath}': {ex.Message}");
                            continue;
                        }

                        if (info != null)
                        {
                            AddCandidate(candidates, dllPath, info);
                        }
                    }
                }
            }
            catch (BadImageFormatException ex)
            {
                Plugin.Logger?.LogDebug($"'{dllPath}' is not a valid .NET assembly: {ex.Message}");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to read plugin assembly '{dllPath}': {ex}");
            }
        }

        private static void AddCandidate(List<WorkshopPluginCandidate> candidates, string dllPath, BepPluginInfo info)
        {
            var typeName = GetTypeName(info);
            if (string.IsNullOrEmpty(typeName))
            {
                Plugin.Logger?.LogWarning($"Skipping plugin '{info.Metadata?.GUID}' in '{dllPath}': type name could not be resolved.");
                return;
            }

            candidates.Add(new WorkshopPluginCandidate
            {
                DllPath = dllPath,
                Info = info,
                TypeName = typeName
            });
        }

        private static void AddSearchDirectories(string packageRoot)
        {
            try
            {
                TypeLoader.Resolver.AddSearchDirectory(packageRoot);
                foreach (var directory in Directory.GetDirectories(packageRoot, "*", SearchOption.AllDirectories))
                {
                    if (Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any())
                    {
                        TypeLoader.Resolver.AddSearchDirectory(directory);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"Failed to register package directories for '{packageRoot}': {ex.Message}");
            }
        }

        private static string GetTypeName(BepPluginInfo info)
        {
            try
            {
                return TypeNameProperty?.GetValue(info, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsBepInExPluginAssembly(AssemblyDefinition assembly)
        {
            return assembly.MainModule.AssemblyReferences.Any(reference => reference.Name == "BepInEx");
        }
    }
}
