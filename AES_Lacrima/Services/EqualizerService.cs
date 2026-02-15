using AES_Controls.Player;
using AES_Controls.Player.Interfaces;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Nodes;

namespace AES_Lacrima.Services
{
    public interface IEqualizerService;

    [AutoRegister]
    internal partial class EqualizerService : ViewModelBase, IEqualizerService
    {
        [ObservableProperty]
        private Equalizer? _equalizer;

        // Temporarily store bands loaded from settings until the media player is
        // available and the Equalizer instance is created.
        private AvaloniaList<BandModel>? _loadedBands;

        /// <summary>
        /// Initializes the equalizer using the specified media interface.
        /// If persisted bands exist they will be applied to the equalizer.
        /// </summary>
        public void Initialize(IMediaInterface player)
        {
            // Load persisted settings (bands) before creating the Equalizer instance
            LoadSettings();

            // Create the Equalizer instance with the media player
            Equalizer = new Equalizer(player);

            // Apply previously loaded bands if available
            if (_loadedBands != null && _loadedBands.Count > 0)
            {
                Equalizer.Bands = _loadedBands;
            }

            // Ensure callbacks are wired for existing bands
            Equalizer.InitializeBands();
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            // Read persisted bands (if any) and hold until Initialize is called
            _loadedBands = ReadCollectionSetting<BandModel>(section, "Bands", "Band");
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            // Persist current bands from the live equalizer instance
            WriteCollectionSetting(section, "Bands", "Band", Equalizer?.Bands);
        }
    }
}