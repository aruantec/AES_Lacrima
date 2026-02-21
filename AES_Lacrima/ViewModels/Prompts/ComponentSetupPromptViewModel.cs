using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Core.DI;

namespace AES_Lacrima.ViewModels.Prompts
{
    public partial class ComponentSetupPromptViewModel : ViewModelBase
    {
        private readonly FFmpegManager? _ffmpeg;
        private readonly MpvLibraryManager? _mpv;
        private readonly YtDlpManager? _ytdlp;
        private readonly Action _onOpenSettings;

        [ObservableProperty]
        private bool _isInstalling;

        [ObservableProperty]
        private string _message = "Critical components (FFmpeg and libmpv) are missing. These are required for media playback and processing. Functionality will be severely restricted until they are installed.";

        public event Action? RequestClose;

        public ComponentSetupPromptViewModel(FFmpegManager? ffmpeg, MpvLibraryManager? mpv, YtDlpManager? ytdlp, Action onOpenSettings)
        {
            _ffmpeg = ffmpeg;
            _mpv = mpv;
            _ytdlp = ytdlp;
            _onOpenSettings = onOpenSettings;
        }

        [RelayCommand]
        private async Task AutoInstall()
        {
            IsInstalling = true;
            try
            {
                bool ffmpegFailed = false;
                bool mpvFailed = false;
                bool ytdlpFailed = false;

                // Install FFmpeg if missing.
                if (_ffmpeg != null && !_ffmpeg.IsFFmpegAvailable())
                {
                    if (!await _ffmpeg.InstallAsync())
                        ffmpegFailed = true;
                }

                // Install libmpv if missing.
                if (_mpv != null && !_mpv.IsLibraryInstalled())
                {
                    await _mpv.EnsureLibraryInstalledAsync();
                    if (!_mpv.IsLibraryInstalled() && !_mpv.IsPendingRestart)
                        mpvFailed = true;
                }

                // Install yt-dlp if missing.
                if (_ytdlp != null && !YtDlpManager.IsInstalled)
                {
                    if (!await _ytdlp.EnsureInstalledAsync())
                        ytdlpFailed = true;
                }

                // Notify settings to refresh status info so the UI reflects current installation state.
                if (DiLocator.ResolveViewModel<SettingsViewModel>() is { } settings)
                {
                    await settings.RefreshFFmpegInfo();
                    await settings.RefreshMpvInfo();
                    await settings.RefreshYtDlpInfo();
                }

                if (ffmpegFailed || mpvFailed || ytdlpFailed)
                {
                    Message = "One or more tools failed to install properly. Please try manual installation through settings or check your connection.";
                    return; // Don't close so the user sees the error message.
                }

                // If libmpv is pending restart, it will have replaced this prompt with the restart prompt in MainWindowViewModel.
                // We should only call RequestClose if this prompt is still the active one (meaning no restart prompt showing).
                if (_mpv == null || !_mpv.IsPendingRestart)
                    RequestClose?.Invoke();
            }
            finally
            {
                IsInstalling = false;
            }
        }

        [RelayCommand]
        private void OpenSettings()
        {
            _onOpenSettings?.Invoke();
            RequestClose?.Invoke();
        }

        [RelayCommand]
        private void Skip()
        {
            RequestClose?.Invoke();
        }
    }
}
