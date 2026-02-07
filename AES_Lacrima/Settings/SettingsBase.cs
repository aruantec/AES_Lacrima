using AES_Core.Interfaces;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace AES_Lacrima.Settings
{
    /// <summary>
    /// Base class for view-model settings persistence.
    /// Provides helpers to read and write a per-viewmodel section under a common
    /// "ViewModels" container in a JSON settings file. Derived classes should
    /// override <see cref="OnSaveSettings(JsonObject)"/> and
    /// <see cref="OnLoadSettings(JsonObject)"/> to persist their state.
    /// </summary>
    public abstract class SettingsBase : ObservableObject, ISetting
    {
        /// <summary>
        /// Default path for the settings file. Derived classes can override this
        /// property to change where settings are stored.
        /// The default is "{AppContext.BaseDirectory}/Settings/Settings.json".
        /// </summary>
        protected virtual string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "Settings.json");

        /// <summary>
        /// JSON property name under which view model settings are grouped in the
        /// settings file.
        /// </summary>
        private const string ViewModelsSectionName = "ViewModels";

        /// <summary>
        /// Process-wide lock used to serialize access to the settings file so
        /// concurrent reads/writes do not corrupt the file.
        /// </summary>
        private static readonly Lock FileLock = new();

        /// <summary>
        /// Save this object's settings into the flat ViewModels section.
        /// Derived classes implement OnSaveSettings to populate the provided JsonObject.
        /// </summary>
        public void SaveSettings()
        {
            lock (FileLock)
            {
                // Ensure the directory for the settings file exists. Use full path to handle
                // relative or unusual SettingsFilePath values and fall back to AppContext.BaseDirectory.
                try
                {
                    var fullPath = Path.GetFullPath(SettingsFilePath);
                    var dir = Path.GetDirectoryName(fullPath);
                    if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to create settings directory for '{SettingsFilePath}': {ex.Message}");
                    try { Directory.CreateDirectory(AppContext.BaseDirectory); } catch { /* ignore */ }
                }

                JsonObject root;
                if (File.Exists(SettingsFilePath))
                {
                    try
                    {
                        var content = File.ReadAllText(SettingsFilePath);
                        root = JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
                    }
                    catch { root = new JsonObject(); }
                }
                else
                {
                    root = new JsonObject();
                }

                if (!root.ContainsKey(ViewModelsSectionName))
                {
                    root[ViewModelsSectionName] = new JsonObject();
                }

                var vmsElement = root[ViewModelsSectionName]!.AsObject();
                string typeName = GetType().Name;

                // Create the section in-memory ONLY.
                var section = new JsonObject();

                // Let the ViewModel try to populate it.
                OnSaveSettings(section);

                // Remove any existing entry for this ViewModel first.
                vmsElement.Remove(typeName);

                // Only add to the tree if there is actually data inside.
                if (section.Count > 0)
                {
                    vmsElement.Add(typeName, section);
                }

                //Remove the ViewModels wrapper if it's empty
                if (vmsElement.Count == 0)
                {
                    root.Remove(ViewModelsSectionName);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsFilePath, root.ToJsonString(options));
            }
        }

        /// <summary>
        /// Load settings for this object from Settings.json (if present).
        /// Calls OnLoadSettings for derived classes to read values.
        /// </summary>
        public void LoadSettings()
        {
            lock (FileLock)
            {
                if (!File.Exists(SettingsFilePath)) return;

                JsonObject root;
                try
                {
                    var content = File.ReadAllText(SettingsFilePath);
                    root = JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
                }
                catch { return; }

                if (!root.TryGetPropertyValue(ViewModelsSectionName, out var vmsNode) || vmsNode is not JsonObject vmsElement)
                    return;

                if (!vmsElement.TryGetPropertyValue(GetType().Name, out var sectionNode) || sectionNode is not JsonObject section)
                    return;

                try
                {
                    OnLoadSettings(section);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Settings fail for {GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Remove the saved section for this concrete type from the Settings.json.
        /// </summary>
        public void RemoveSavedSection()
        {
            lock (FileLock)
            {
                if (!File.Exists(SettingsFilePath)) return;

                JsonObject root;
                try
                {
                    var content = File.ReadAllText(SettingsFilePath);
                    root = JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
                }
                catch { return; }

                if (root.TryGetPropertyValue(ViewModelsSectionName, out var vmsNode) && vmsNode is JsonObject vmsElement)
                {
                    if (vmsElement.Remove(GetType().Name))
                    {
                        if (vmsElement.Count == 0)
                            root.Remove(ViewModelsSectionName);

                        var options = new JsonSerializerOptions { WriteIndented = true };
                        File.WriteAllText(SettingsFilePath, root.ToJsonString(options));
                    }
                }
            }
        }

        /// <summary>
        /// Derived classes override to write settings into the provided JsonObject (for this concrete type).
        /// </summary>
        protected virtual void OnSaveSettings(JsonObject section) { }

        /// <summary>
        /// Derived classes override to read settings from the provided JsonObject.
        /// </summary>
        protected virtual void OnLoadSettings(JsonObject section) { }

        /// <summary>
        /// Serialize a single object as a nested element under `section` using System.Text.Json Source Generation.
        /// </summary>
        protected void WriteObjectSetting<T>(JsonObject section, string name, T? obj)
        {
            if (obj == null)
            {
                section[name] = null;
                return;
            }

            try
            {
                var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)SettingsJsonContext.Default.GetTypeInfo(typeof(T))!;
                {
                    var node = JsonSerializer.SerializeToNode(obj, typeInfo);
                    section[name] = node;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WriteObjectSetting failed for {name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Deserialize a single object under `section` with key `name` using System.Text.Json Source Generation.
        /// </summary>
        protected T? ReadObjectSetting<T>(JsonObject section, string name) where T : class
        {
            if (section.TryGetPropertyValue(name, out var node) && node != null)
            {
                try
                {
                    var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)SettingsJsonContext.Default.GetTypeInfo(typeof(T))!;
                    return node.Deserialize(typeInfo);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ReadObjectSetting failed for {name}: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Serialize an IEnumerable as a named array in `section` using System.Text.Json Source Generation.
        /// </summary>
        protected void WriteCollectionSetting<T>(JsonObject section, string containerName, string itemElementName, IEnumerable<T>? items)
        {
            if (items == null)
            {
                section.Remove(containerName);
                return;
            }

            try
            {
                // We serialize the entire collection as a list
                var list = items.ToList();
                var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>>)SettingsJsonContext.Default.GetTypeInfo(typeof(List<T>))!;
                {
                    var node = JsonSerializer.SerializeToNode(list, typeInfo);
                    section[containerName] = node;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WriteCollectionSetting failed for {containerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Read a collection of T from the named key and return an AvaloniaList.
        /// </summary>
        protected AvaloniaList<T> ReadCollectionSetting<T>(JsonObject section, string containerName, string itemElementName, AvaloniaList<T>? target = null)
        {
            var result = target ?? new AvaloniaList<T>();

            if (section.TryGetPropertyValue(containerName, out var node) && node != null)
            {
                try
                {
                    var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>>)SettingsJsonContext.Default.GetTypeInfo(typeof(List<T>))!;
                    {
                        var list = node.Deserialize(typeInfo);
                        if (list != null)
                        {
                            result.Clear();
                            result.AddRange(list);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ReadCollectionSetting failed for {containerName}: {ex.Message}");
                }
            }

            return result;
        }

        protected void WriteSetting(JsonObject section, string name, string? value)
        {
            section[name] = value;
        }

        protected void WriteSetting(JsonObject section, string name, int value)
            => section[name] = value;

        protected void WriteSetting(JsonObject section, string name, double value)
            => section[name] = value;

        protected void WriteSetting(JsonObject section, string name, bool value)
            => section[name] = value;

        protected string? ReadStringSetting(JsonObject section, string name, string? defaultValue = null)
        {
            if (section.TryGetPropertyValue(name, out var node) && node != null)
            {
                return node.GetValue<string>();
            }
            return defaultValue;
        }

        protected int ReadIntSetting(JsonObject section, string name, int defaultValue = 0)
        {
            if (section.TryGetPropertyValue(name, out var node) && node != null)
            {
                return node.GetValue<int>();
            }
            return defaultValue;
        }

        protected double ReadDoubleSetting(JsonObject section, string name, double defaultValue = 0)
        {
            if (section.TryGetPropertyValue(name, out var node) && node != null)
            {
                return node.GetValue<double>();
            }
            return defaultValue;
        }

        protected bool ReadBoolSetting(JsonObject section, string name, bool defaultValue = false)
        {
            if (section.TryGetPropertyValue(name, out var node) && node != null)
            {
                return node.GetValue<bool>();
            }
            return defaultValue;
        }
    }
}
