using AES_Controls.Player;
using AES_Controls.Player.Interfaces;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Nodes;
using System;
using log4net;

namespace AES_Lacrima.Services
{
    public interface IEqualizerService;

    [AutoRegister]
    public partial class EqualizerService : ViewModelBase, IEqualizerService
    {
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<EqualizerService>();
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
            // Apply currently-loaded band gains to the player so settings take effect immediately
            try
            {
                if (Equalizer.Bands != null && Equalizer.Bands.Count > 0)
                    player.SetEqualizerBandsThrottled(Equalizer.Bands);
            }
            catch (Exception ex)
            {
                // Log and continue silently; applying bands is best-effort
                Log?.Warn("EqualizerService.Initialize: failed to apply bands to player", ex);
            }
        }

        /// <summary>
        /// Asynchronously initializes the equalizer using the specified media interface.
        /// If persisted bands exist they will be applied to the equalizer.
        /// </summary>
        /// <param name="player">The media interface used to apply equalizer settings.</param>
        /// <returns>A task representing the initialization operation.</returns>
        public async System.Threading.Tasks.Task InitializeAsync(IMediaInterface player)
        {
            // Load persisted settings (bands) before creating the Equalizer instance
            await LoadSettingsAsync();

            // Create the Equalizer instance with the media player
            Equalizer = new Equalizer(player);

            // Apply previously loaded bands if available
            if (_loadedBands != null && _loadedBands.Count > 0)
            {
                Equalizer.Bands = _loadedBands;
            }

            // Ensure callbacks are wired for existing bands
            Equalizer.InitializeBands();
            // Apply currently-loaded band gains to the player so settings take effect immediately
            try
            {
                if (Equalizer.Bands != null && Equalizer.Bands.Count > 0)
                    player.SetEqualizerBandsThrottled(Equalizer.Bands);
            }
            catch (Exception ex)
            {
                Log?.Warn("EqualizerService.InitializeAsync: failed to apply bands to player", ex);
            }
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
