using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation.Controls;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Platform;
using AES_Emulation.Windows.API;
using AES_Lacrima.Mac.API;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.Services.Cemu;
using AES_Lacrima.Services.Rpcs3;
using AES_Lacrima.Services.ShadPs4;
using AES_Lacrima.Services.Xenia;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DrawingIcon = System.Drawing.Icon;


namespace AES_Lacrima.ViewModels
{
    public record ShaderFileItem(string FilePath, string Name, bool IsSupportedInDirectComposition = true);
    public partial class XeniaPatchEntry : ObservableObject
    {
        [ObservableProperty]
        private bool _isEnabled;

        public string Name { get; }

        public string Description { get; }

        public XeniaPatchEntry(bool isEnabled, string name, string description)
        {
            _isEnabled = isEnabled;
            Name = name;
            Description = description;
        }
    }

    public sealed record XeniaPatchFileItem(string FilePath, string DisplayName);

    public partial class ShadPs4PatchEntry : ObservableObject
    {
        [ObservableProperty]
        private bool _isEnabled;

        public string Name { get; }
        public string Note { get; }
        public string AppVer { get; }

        public ShadPs4PatchEntry(bool isEnabled, string name, string note, string appVer)
        {
            _isEnabled = isEnabled;
            Name = name;
            Note = note;
            AppVer = appVer;
        }
    }

    public partial class Rpcs3PatchEntry : ObservableObject
    {
        [ObservableProperty]
        private bool _isEnabled;

        public string EntryKey { get; }
        public string PpuHash { get; }
        public string GameTitle { get; }
        public string Serial { get; }
        public string Name { get; }
        public string? Subtitle { get; }
        public string AppVersion { get; }

        public Rpcs3PatchEntry(
            bool isEnabled,
            string entryKey,
            string ppuHash,
            string name,
            string gameTitle,
            string serial,
            string appVersion,
            string? subtitle)
        {
            _isEnabled = isEnabled;
            EntryKey = entryKey;
            PpuHash = ppuHash;
            Name = name;
            GameTitle = gameTitle;
            Serial = serial;
            AppVersion = appVersion;
            Subtitle = subtitle;
        }
    }

    public partial class CemuGraphicPackPresetGroupEntry : ObservableObject
    {
        [ObservableProperty]
        private string? _selectedPresetName;

        public string Category { get; }
        public string CategoryLabel { get; }
        public IReadOnlyList<string> PresetNames { get; }

        public CemuGraphicPackPresetGroupEntry(
            string category,
            string categoryLabel,
            IReadOnlyList<string> presetNames,
            string? selectedPresetName)
        {
            Category = category;
            CategoryLabel = categoryLabel;
            PresetNames = presetNames;
            _selectedPresetName = selectedPresetName;
        }
    }

    public partial class CemuGraphicPackEntry : ObservableObject
    {
        [ObservableProperty]
        private bool _isEnabled;

        public string EntryKey { get; }
        public string RelativeRulesPath { get; }
        public string Name { get; }
        public string? Subtitle { get; }
        public AvaloniaList<CemuGraphicPackPresetGroupEntry> PresetGroups { get; } = [];
        public bool HasPresets => PresetGroups.Count > 0;

        public CemuGraphicPackEntry(
            bool isEnabled,
            string entryKey,
            string relativeRulesPath,
            string name,
            string? subtitle,
            IEnumerable<CemuGraphicPackPresetGroupEntry>? presetGroups)
        {
            _isEnabled = isEnabled;
            EntryKey = entryKey;
            RelativeRulesPath = relativeRulesPath;
            Name = name;
            Subtitle = subtitle;

            if (presetGroups != null)
                PresetGroups.AddRange(presetGroups);
        }
    }

    public partial class EmulationAlbumItem : FolderMediaItem
    {
        [ObservableProperty]
        private AvaloniaList<MediaItem> _previewItems = [];
    }
}
