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
using SkiaSharp;
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
using AES_Core.Logging;
using DrawingIcon = System.Drawing.Icon;


namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private void RefreshGameplayPreviewForCurrentSelection(bool immediate = false)
        {
            if (CoverItems.Count == 0)
                return;

            int index = GetRoundedSelectedIndex(SelectedIndex);
            if (index < 0 || index >= CoverItems.Count)
                index = PointedIndex >= 0 && PointedIndex < CoverItems.Count ? PointedIndex : 0;

            if (immediate && _isGameplayPreviewActive)
                StopGameplayPreview();

            QueueGameplayPreview(CoverItems[index], immediate);
        }

        private bool CanPlayGameplayPreviewFor(MediaItem? item) =>
            IsGameplayAutoplayEnabled &&
            !IsEmulatorRunning &&
            (IsYtDlpInstalled || EmulationPreviewCacheHelper.HasPreview(item?.FileName));

        private static string? ResolvePreferredGameplayPreviewUrl(MediaItem item)
        {
            var localPreview = EmulationPreviewCacheHelper.TryGetPreviewPath(item.FileName);
            if (!string.IsNullOrWhiteSpace(localPreview))
                return localPreview;

            return string.IsNullOrWhiteSpace(item.VideoUrl) ? null : item.VideoUrl;
        }

        private void SyncGameplayPreviewItemIndex()
        {
            if (CoverItems.Count == 0)
            {
                if (GameplayPreviewItemIndex >= 0)
                    GameplayPreviewItemIndex = -1;
                return;
            }

            var previewPath = _activeGameplayPreviewItemPath ?? _pendingGameplayPreviewItemPath;
            if (string.IsNullOrWhiteSpace(previewPath))
                return;

            var previewItem = CoverItems.FirstOrDefault(candidate =>
                string.Equals(candidate.FileName, previewPath, StringComparison.OrdinalIgnoreCase));
            if (previewItem == null)
                return;

            int index = CoverItems.IndexOf(previewItem);
            if (index >= 0 && index != GameplayPreviewItemIndex)
                GameplayPreviewItemIndex = index;
        }

        private bool ShouldStartGameplayPreviewImmediately(MediaItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return false;

            if (EmulationPreviewCacheHelper.HasPreview(item.FileName))
                return true;

            int index = CoverItems.IndexOf(item);
            return index >= 0 && index == GetRoundedSelectedIndex(SelectedIndex);
        }

        private void QueueGameplayPreview(MediaItem? item, bool immediate = false)
        {
            ApplyGameplayPreviewSelectionVisuals(item);
            StartGameplayPreviewLoad(item, immediate);
        }

        private int ResolveActiveGameplayPreviewCoverIndex()
        {
            if (string.IsNullOrWhiteSpace(_activeGameplayPreviewItemPath))
                return -1;

            var activeItem = CoverItems.FirstOrDefault(candidate =>
                string.Equals(candidate.FileName, _activeGameplayPreviewItemPath, StringComparison.OrdinalIgnoreCase));
            return activeItem == null ? -1 : CoverItems.IndexOf(activeItem);
        }

        private void PinGameplayPreviewVisualsToActivePlayback()
        {
            if (!_isGameplayPreviewActive)
                return;

            int activeIndex = ResolveActiveGameplayPreviewCoverIndex();
            if (activeIndex < 0)
                return;

            if (GameplayPreviewItemIndex != activeIndex)
                GameplayPreviewItemIndex = activeIndex;

            IsGameplayPreviewHostVisible = true;
            IsGameplayVideoVisible = true;
        }

        private void ApplyGameplayPreviewSelectionVisuals(MediaItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return;

            if (_isGameplayPreviewActive &&
                !string.Equals(_activeGameplayPreviewItemPath, item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                PinGameplayPreviewVisualsToActivePlayback();
                return;
            }

            if (!CanPlayGameplayPreviewFor(item))
                return;

            if (HighlightedItem == null ||
                !string.Equals(item.FileName, HighlightedItem.FileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int previewIndex = ResolveGameplayPreviewItemIndex(item);
            if (previewIndex >= 0 && previewIndex != GameplayPreviewItemIndex)
                GameplayPreviewItemIndex = previewIndex;
        }

        private void StartGameplayPreviewLoad(MediaItem? item, bool immediate = false)
        {
            if (!CanPlayGameplayPreviewFor(item) || item == null || string.IsNullOrWhiteSpace(item.FileName))
                return;

            if (HighlightedItem == null ||
                !string.Equals(item.FileName, HighlightedItem.FileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dispatcher.UIThread.Post(() => MetadataService?.StopGameplayVideoSearchPreview(), DispatcherPriority.Background);

            var requestedPath = item.FileName;
            if (string.Equals(_pendingGameplayPreviewItemPath, requestedPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (_isGameplayPreviewActive &&
                string.Equals(_activeGameplayPreviewItemPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                var currentPlaybackUrl = AudioPlayer?.CurrentMediaItem?.FileName;
                var requestedPlaybackUrl = ResolvePreferredGameplayPreviewUrl(item);
                if (!string.IsNullOrWhiteSpace(requestedPlaybackUrl) &&
                    !string.Equals(currentPlaybackUrl, requestedPlaybackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // Same selected item but playback source changed -> force restart with new URL.
                }
                else
                {
                    GameplayPreviewItemIndex = ResolveGameplayPreviewItemIndex(item);
                    IsGameplayPreviewHostVisible = true;
                    IsGameplayVideoVisible = true;
                    return;
                }
            }

            // Cancel any in-flight load and start the next preview on the selected tile.
            CancelPendingGameplayPreview();
            _pendingGameplayPreviewItemPath = requestedPath;
            long requestVersion = Interlocked.Increment(ref _gameplayPreviewRequestVersion);

            var cts = new CancellationTokenSource();
            _gameplayPreviewCts = cts;
            var token = cts.Token;
            _ = StartGameplayPreviewAsync(item, token, immediate, requestVersion);
        }

        private async Task StartGameplayPreviewAsync(MediaItem item, CancellationToken cancellationToken, bool immediate, long requestVersion)
        {
            try
            {
                Task<GameplayPreviewSource?>? earlyResolveTask = null;
                if (!immediate && !EmulationPreviewCacheHelper.HasPreview(item.FileName))
                    earlyResolveTask = ResolveGameplayPreviewSourceAsync(item, cancellationToken);

                if (!immediate)
                    await Task.Delay(GameplayPreviewHoverDelayMs, cancellationToken);

                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                var previewSource = earlyResolveTask != null
                    ? await earlyResolveTask.ConfigureAwait(false)
                    : await ResolveGameplayPreviewSourceAsync(item, cancellationToken).ConfigureAwait(false);
                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                if (previewSource == null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (string.Equals(_pendingGameplayPreviewItemPath, item.FileName, StringComparison.OrdinalIgnoreCase))
                            _pendingGameplayPreviewItemPath = null;
                    }, DispatcherPriority.Background);
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(EnsureGameplayAudioPlayer, DispatcherPriority.Background);
                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                var player = AudioPlayer;
                if (player == null)
                    return;

                bool stillHighlighted = await Dispatcher.UIThread.InvokeAsync(() =>
                        HighlightedItem != null &&
                        string.Equals(HighlightedItem.FileName, item.FileName, StringComparison.OrdinalIgnoreCase),
                    DispatcherPriority.Background);
                if (!stillHighlighted || requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GameplayPreviewItemIndex = ResolveGameplayPreviewItemIndex(item);
                    IsGameplayPreviewHostVisible = true;
                    IsGameplayVideoVisible = true;
                }, DispatcherPriority.Background);

                await player.PlayFile(previewSource.PreviewItem, video: true, enableMediaAnalysis: false).ConfigureAwait(false);
                player.SetPreviewMuted(false);

                await WaitForPreviewPlaybackReadyAsync(player, cancellationToken).ConfigureAwait(false);
                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                {
                    try
                    {
                        player.Stop();
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn("Failed to stop stale gameplay preview video.", ex);
                    }

                    return;
                }

                _isGameplayPreviewActive = true;
                _activeGameplayPreviewItemPath = item.FileName;
                _pendingGameplayPreviewItemPath = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GameplayPreviewItemIndex = ResolveGameplayPreviewItemIndex(item);
                    IsGameplayPreviewHostVisible = true;
                    IsGameplayVideoVisible = true;
                    ScheduleGameplayPreviewPresentationRefresh();
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException logEx) { SLog.Warn("Non-critical error", logEx); }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to autoplay gameplay preview for '{item.Title}'.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => IsGameplayVideoVisible = false, DispatcherPriority.Background);
                _isGameplayPreviewActive = false;
            }
        }

        private void CancelPendingGameplayPreview()
        {
            try
            {
                _gameplayPreviewCts?.Cancel();
                _gameplayPreviewCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to cancel or dispose the gameplay preview token source cleanly.", ex);
            }
            finally
            {
                _gameplayPreviewCts = null;
            }

            Interlocked.Increment(ref _gameplayPreviewRequestVersion);
            _pendingGameplayPreviewItemPath = null;
        }

        private void HideGameplayPreviewImmediately()
        {
            CancelPendingGameplayPreview();
            IsGameplayPreviewHostVisible = false;
            IsGameplayVideoVisible = false;
            GameplayPreviewItemIndex = -1;

            if (_gameplayPreviewPausedForCapture)
                return;

            try
            {
                AudioPlayer?.SetProperty("mute", false);
                AudioPlayer?.Stop();
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to stop gameplay preview video.", ex);
            }

            _activeGameplayPreviewItemPath = null;
            _isGameplayPreviewActive = false;
        }

        private int ResolveGameplayPreviewItemIndex(MediaItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return -1;

            int index = CoverItems.IndexOf(item);
            if (index >= 0)
                return index;

            var previewPath = _activeGameplayPreviewItemPath ?? _pendingGameplayPreviewItemPath;
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                var previewItem = CoverItems.FirstOrDefault(candidate =>
                    string.Equals(candidate.FileName, previewPath, StringComparison.OrdinalIgnoreCase));
                if (previewItem != null)
                {
                    index = CoverItems.IndexOf(previewItem);
                    if (index >= 0)
                        return index;
                }
            }

            return GetRoundedSelectedIndex(SelectedIndex);
        }

        private void PauseGameplayPreviewForCapture()
        {
            if (!_isGameplayPreviewActive &&
                !IsGameplayPreviewHostVisible &&
                _gameplayPreviewCts == null &&
                string.IsNullOrWhiteSpace(_pendingGameplayPreviewItemPath))
            {
                return;
            }

            _gameplayPreviewPausedItemPath = _activeGameplayPreviewItemPath ?? HighlightedItem?.FileName;
            _gameplayPreviewPausedItemIndex = GameplayPreviewItemIndex;
            _gameplayPreviewPausedForCapture = _isGameplayPreviewActive;

            CancelPendingGameplayPreview();
            IsGameplayPreviewHostVisible = false;
            IsGameplayVideoVisible = false;

            if (_gameplayPreviewPausedForCapture)
            {
                try
                {
                    AudioPlayer?.Pause();
                }
                catch (Exception ex)
                {
                    SLog.Warn("Failed to pause gameplay preview for capture.", ex);
                }
            }
            else
            {
                try
                {
                    AudioPlayer?.Stop();
                }
                catch (Exception ex)
                {
                    SLog.Warn("Failed to stop pending gameplay preview for capture.", ex);
                }

                _activeGameplayPreviewItemPath = null;
                _isGameplayPreviewActive = false;
            }
        }

        private void ResumeGameplayPreviewAfterCapture()
        {
            if (!_gameplayPreviewPausedForCapture)
            {
                if (IsGameplayPreviewAvailable)
                    QueueGameplayPreview(HighlightedItem, immediate: true);
                return;
            }

            _gameplayPreviewPausedForCapture = false;
            var pausedPath = _gameplayPreviewPausedItemPath;
            var pausedIndex = _gameplayPreviewPausedItemIndex;
            _gameplayPreviewPausedItemPath = null;
            _gameplayPreviewPausedItemIndex = -1;

            if (!IsGameplayPreviewAvailable || string.IsNullOrWhiteSpace(pausedPath))
                return;

            var item = HighlightedItem;
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return;

            if (!string.Equals(item.FileName, pausedPath, StringComparison.OrdinalIgnoreCase))
            {
                QueueGameplayPreview(item, immediate: true);
                return;
            }

            IsGameplayPreviewHostVisible = true;
            IsGameplayVideoVisible = true;
            GameplayPreviewItemIndex = pausedIndex >= 0
                ? pausedIndex
                : ResolveGameplayPreviewItemIndex(item);
            _isGameplayPreviewActive = true;
            _activeGameplayPreviewItemPath = item.FileName;

            try
            {
                AudioPlayer?.Play();
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to resume gameplay preview after capture.", ex);
                QueueGameplayPreview(item, immediate: true);
            }
        }

        private void StopGameplayPreview()
        {
            _gameplayPreviewPausedForCapture = false;
            _gameplayPreviewPausedItemPath = null;
            _gameplayPreviewPausedItemIndex = -1;
            CancelPendingGameplayPreview();

            _activeGameplayPreviewItemPath = null;
            IsGameplayPreviewHostVisible = false;
            IsGameplayVideoVisible = false;
            GameplayPreviewItemIndex = -1;

            try
            {
                AudioPlayer?.SetProperty("mute", false);
                AudioPlayer?.Stop();
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to stop gameplay preview video.", ex);
            }

            _isGameplayPreviewActive = false;
        }

        private static string GetMetadataCachePath(string? filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }

        private async Task<string?> ResolveGameplayVideoUrlAsync(MediaItem item, CancellationToken cancellationToken)
        {
            var localPreview = EmulationPreviewCacheHelper.TryGetPreviewPath(item.FileName);
            if (!string.IsNullOrWhiteSpace(localPreview))
                return localPreview;

            if (!string.IsNullOrWhiteSpace(item.VideoUrl))
                return item.VideoUrl;

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath), cancellationToken).ConfigureAwait(false);
            var cachedVideoUrl = metadata?.VideoUrl;
            if (!string.IsNullOrWhiteSpace(cachedVideoUrl))
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.VideoUrl = cachedVideoUrl, DispatcherPriority.Background);
                return cachedVideoUrl;
            }

            return EmulationPreviewCacheHelper.TryGetPreviewPath(item.FileName);
        }

        private async Task<GameplayPreviewSource?> ResolveGameplayPreviewSourceAsync(MediaItem item, CancellationToken cancellationToken)
        {
            var videoUrl = await ResolveGameplayVideoUrlAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(videoUrl))
                return null;

            var previewItem = new MediaItem
            {
                FileName = videoUrl,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                VideoUrl = videoUrl
            };

            if (videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                _mediaUrlService ??= DiLocator.ResolveViewModel<MediaUrlService>();
                if (_mediaUrlService == null)
                    return null;

                var resolvedSource = await _mediaUrlService.ResolveMediaSourceAsync(videoUrl, preferVideo: true).ConfigureAwait(false);
                if (resolvedSource == null)
                    return null;

                previewItem.OnlineUrls = resolvedSource.OnlineUrls;
            }

            return new GameplayPreviewSource(previewItem);
        }

        private static async Task WaitForPreviewPlaybackReadyAsync(AudioPlayer player, CancellationToken cancellationToken)
        {
            const int timeoutMs = 20_000;
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!player.IsLoadingMedia && (player.Duration > 0 || player.Position > 0.05))
                    return;

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        private void ScheduleGameplayPreviewPresentationRefresh()
        {
            Dispatcher.UIThread.Post(NotifyGameplayPreviewPresentationRefresh, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(NotifyGameplayPreviewPresentationRefresh, DispatcherPriority.Render);
        }

        internal event EventHandler? GameplayPreviewPresentationRefreshRequested;

        private void NotifyGameplayPreviewPresentationRefresh()
            => GameplayPreviewPresentationRefreshRequested?.Invoke(this, EventArgs.Empty);

        private void EnsureGameplayAudioPlayer()
        {
            if (AudioPlayer != null)
            {
                AudioPlayer.RepeatMode = RepeatMode.One;
                return;
            }

            var ffmpegManager = DiLocator.ResolveViewModel<FFmpegManager>();
            var mpvLibraryManager = DiLocator.ResolveViewModel<MpvLibraryManager>();
            AudioPlayer = new AudioPlayer(ffmpegManager, mpvLibraryManager);
            AudioPlayer.RepeatMode = RepeatMode.One;
        }

        private const int CarouselCoverDecodeSize = 384;

        /// <summary>
        /// Fast path for album shell tiles: decode only so the list can paint immediately.
        /// Bar-crop persistence runs separately on a background thread.
        /// </summary>
        private static Bitmap? LoadAlbumShellBitmap(string imagePath)
        {
            try
            {
                using var stream = File.OpenRead(imagePath);
                try
                {
                    return Bitmap.DecodeToWidth(stream, CarouselCoverDecodeSize);
                }
                catch
                {
                    stream.Position = 0;
                    return new Bitmap(stream);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to load console bitmap '{imagePath}'.", ex);
                return null;
            }
        }

        private static void QueueConsoleCoverBarCropPersist(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    using var codec = SKCodec.Create(imagePath);
                    if (codec == null)
                        return;

                    using var bmp = new SKBitmap(codec.Info);
                    codec.GetPixels(bmp.Info, bmp.GetPixels());
                    using var cropped = CoverImageBarCropHelper.TryCrop(bmp, out bool didCrop);
                    if (didCrop && cropped != null)
                        CoverImageBarCropHelper.TryPersistCroppedCover(cropped, imagePath, null);
                }
                catch (Exception ex)
                {
                    SLog.Debug($"Background console cover bar-crop skipped for '{imagePath}'.", ex);
                }
            });
        }
    }
}
