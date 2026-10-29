using System;
using System.Linq;
using System.Reflection;
using NuclearOption.ModScripts;

namespace BepInExSecondaryLoader
{
    /// <summary>
    /// Adds the plugin workshop item type to the game's static <see cref="ModTypes"/> registry.
    /// </summary>
    internal static class ModTypesRegistration
    {
        private static readonly PluginMod PluginType = new PluginMod();
        private static bool registered;

        public static PluginMod Instance
        {
            get { return PluginType; }
        }

        public static void Register()
        {
            if (registered)
            {
                return;
            }

            try
            {
                var field = typeof(ModTypes).GetField(nameof(ModTypes.ModTypesArray), BindingFlags.Public | BindingFlags.Static);
                if (field == null)
                {
                    Plugin.Logger?.LogError($"Could not find {nameof(ModTypes)}.{nameof(ModTypes.ModTypesArray)}.");
                    return;
                }

                var current = field.GetValue(null) as ModType[];
                if (current == null)
                {
                    Plugin.Logger?.LogError($"{nameof(ModTypes)}.{nameof(ModTypes.ModTypesArray)} is null.");
                    return;
                }

                if (current.Any(type => type != null && type.ItemType == PluginType.ItemType))
                {
                    registered = true;
                    return;
                }

                var extended = new ModType[current.Length + 1];
                Array.Copy(current, extended, current.Length);
                extended[current.Length] = PluginType;
                field.SetValue(null, extended);

                registered = true;
                Plugin.Logger?.LogInfo($"Registered workshop item type '{PluginType.Name}'.");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Failed to register the plugin workshop item type: {ex}");
            }
        }
    }
}
