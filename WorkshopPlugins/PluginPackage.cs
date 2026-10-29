using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BepInExSecondaryLoader.WorkshopPlugins
{
    /// <summary>
    /// Manifest that marks a folder as the root of a BepInEx plugin workshop package.
    /// </summary>
    internal sealed class PluginManifest
    {
        public string Name { get; set; }

        public string Description { get; set; }

        public string Version { get; set; }

        /// <summary>
        /// Plugin assemblies relative to the package root. When empty, the whole package is scanned.
        /// </summary>
        public List<string> EntryAssemblies { get; set; }

        public List<string> Tags { get; set; }
    }

    /// <summary>
    /// Locates plugin packages by their manifest file. The folder containing the manifest is the
    /// package root, and its entire directory tree is treated as part of the package.
    /// </summary>
    internal static class PluginPackage
    {
        public const string ManifestFileName = "Plugin.json";

        public static bool HasManifest(string folder)
        {
            return GetManifestPath(folder) != null;
        }

        public static string GetManifestPath(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            try
            {
                return Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file => string.Equals(
                        Path.GetFileName(file),
                        ManifestFileName,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool TryReadManifest(string folder, out PluginManifest manifest, out string error)
        {
            manifest = null;

            var manifestPath = GetManifestPath(folder);
            if (manifestPath == null)
            {
                error = $"'{ManifestFileName}' was not found in '{folder}'.";
                return false;
            }

            try
            {
                manifest = JsonConvert.DeserializeObject<PluginManifest>(File.ReadAllText(manifestPath)) ??
                    new PluginManifest();
                manifest.EntryAssemblies = NormalizeList(manifest.EntryAssemblies);
                manifest.Tags = NormalizeList(manifest.Tags);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to read '{manifestPath}': {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Finds the package root for a workshop item. The item root is preferred; a recursive
        /// search is used as a fallback for packages that were nested by the author.
        /// </summary>
        public static bool TryFindPackageRoot(
            string searchRoot,
            out string packageRoot,
            out PluginManifest manifest,
            out string error)
        {
            packageRoot = null;
            manifest = null;

            if (string.IsNullOrEmpty(searchRoot) || !Directory.Exists(searchRoot))
            {
                error = $"Package search folder not found: '{searchRoot}'.";
                return false;
            }

            if (HasManifest(searchRoot))
            {
                packageRoot = searchRoot;
                return TryReadManifest(packageRoot, out manifest, out error);
            }

            var roots = FindPackageRoots(searchRoot, includeSelf: true).ToList();
            if (roots.Count == 0)
            {
                error = $"No '{ManifestFileName}' was found under '{searchRoot}'.";
                return false;
            }

            if (roots.Count > 1)
            {
                Plugin.Logger?.LogWarning(
                    $"Found {roots.Count} plugin packages under '{searchRoot}'; using '{roots[0]}'.");
            }

            packageRoot = roots[0];
            return TryReadManifest(packageRoot, out manifest, out error);
        }

        /// <summary>
        /// Enumerates package roots. Directories containing a manifest are not searched further,
        /// so nested folders stay part of the outer package.
        /// </summary>
        public static IEnumerable<string> FindPackageRoots(string directory, bool includeSelf = false)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return FindPackageRootsIterator(directory, includeSelf, visited);
        }

        public static string GetRelativePath(string root, string fullPath)
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(fullPath);

            if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var prefix = rootFull + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length)
                : full;
        }

        public static bool TryResolveFile(string root, string relativePath, out string fullPath, out string error)
        {
            fullPath = null;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "Path is empty.";
                return false;
            }

            var normalized = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
            {
                error = $"Path must be relative to the package root: '{relativePath}'.";
                return false;
            }

            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));
            var prefix = rootFull + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Path escapes the package root: '{relativePath}'.";
                return false;
            }

            if (!File.Exists(candidate))
            {
                error = $"File not found: '{relativePath}'.";
                return false;
            }

            fullPath = candidate;
            error = null;
            return true;
        }

        private static IEnumerable<string> FindPackageRootsIterator(
            string directory,
            bool includeSelf,
            HashSet<string> visited)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch (Exception)
            {
                yield break;
            }

            if (!visited.Add(fullPath))
            {
                yield break;
            }

            if (includeSelf && HasManifest(directory))
            {
                yield return directory;
                yield break;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var child in children.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (HasManifest(child))
                {
                    yield return child;
                    continue;
                }

                foreach (var nested in FindPackageRootsIterator(child, includeSelf: false, visited))
                {
                    yield return nested;
                }
            }
        }

        private static List<string> NormalizeList(List<string> values)
        {
            if (values == null)
            {
                return null;
            }

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
        }
    }
}
