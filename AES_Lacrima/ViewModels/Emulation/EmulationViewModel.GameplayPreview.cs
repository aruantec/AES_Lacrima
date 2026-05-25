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
using AES_Core.Logging;
using DrawingIcon = System.Drawing.Icon;


namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private void QueueGameplayPreview(MediaItem? item, bool immediate = false)
        {
            if (!IsGameplayPreviewAvailable || item == null || string.IsNullOrWhiteSpace(item.FileName))
            {
                if (immediate)
                    _suppressSelectionStopForGameplayPreview = false;
                if (_isGameplayPreviewActive ||
                    !string.IsNullOrWhiteSpace(_pendingGameplayPreviewItemPath) ||
                    _gameplayPreviewCts != null)
                {
                    StopGameplayPreview();
                }
                return;
            }

            var requestedPath = item.FileName;
            if (string.Equals(_pendingGameplayPreviewItemPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                if (immediate)
                    _suppressSelectionStopForGameplayPreview = false;
                return;
            }

            if (_isGameplayPreviewActive &&
                string.Equals(_activeGameplayPreviewItemPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                var currentPlaybackUrl = AudioPlayer?.CurrentMediaItem?.FileName;
                var requestedVideoUrl = item.VideoUrl;
                if (!string.IsNullOrWhiteSpace(requestedVideoUrl) &&
                    !string.Equals(currentPlaybackUrl, requestedVideoUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // Same selected item but gameplay URL changed -> force restart with new URL.
                }
                else
                {
                    IsGameplayVideoVisible = true;
                    if (immediate)
                        _suppressSelectionStopForGameplayPreview = false;
                    return;
                }
            }

            // Selection actually changed -> stop/hide immediately, then delay-start the next item.
            StopGameplayPreview();
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
                if (!immediate)
                    await Task.Delay(GameplayPreviewHoverDelayMs, cancellationToken);

                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                // Start resolving the final playback source immediately so the shell reveal
                // and the yt-dlp/stream work overlap as much as possible.
                var previewSourceTask = ResolveGameplayPreviewSourceAsync(item, cancellationToken);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsGameplayPreviewHostVisible = true;
                    IsGameplayVideoVisible = false;
                    GameplayPreviewTargetAspectRatio = 0;
                }, DispatcherPriority.Background);

                var previewSource = await previewSourceTask.ConfigureAwait(false);
                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                if (previewSource == null)
                {
                    StopGameplayPreview();
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GameplayPreviewTargetAspectRatio = previewSource.AspectRatio ?? 0;
                }, DispatcherPriority.Background);

                await Task.Delay(GameplayPreviewResizeDelayMs, cancellationToken);

                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                EnsureGameplayAudioPlayer();
                var player = AudioPlayer;
                if (player == null)
                {
                    StopGameplayPreview();
                    return;
                }

                await player.PlayFile(previewSource.PreviewItem, video: true);

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
                await Dispatcher.UIThread.InvokeAsync(() => IsGameplayVideoVisible = true, DispatcherPriority.Background);
            }
            catch (OperationCanceledException logEx) { SLog.Warn("Non-critical error", logEx); }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to autoplay gameplay preview for '{item.Title}'.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => IsGameplayVideoVisible = false, DispatcherPriority.Background);
                _isGameplayPreviewActive = false;
            }
            finally
            {
                _suppressSelectionStopForGameplayPreview = false;
            }
        }

        private void StopGameplayPreview()
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
            _activeGameplayPreviewItemPath = null;
            IsGameplayPreviewHostVisible = false;
            IsGameplayVideoVisible = false;
            GameplayPreviewTargetAspectRatio = 0;

            try
            {
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
            if (!string.IsNullOrWhiteSpace(item.VideoUrl))
                return item.VideoUrl;

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath), cancellationToken).ConfigureAwait(false);
            var cachedVideoUrl = metadata?.VideoUrl;
            if (string.IsNullOrWhiteSpace(cachedVideoUrl))
                return null;

            await Dispatcher.UIThread.InvokeAsync(() => item.VideoUrl = cachedVideoUrl, DispatcherPriority.Background);
            return cachedVideoUrl;
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

            double? aspectRatio = null;

            if (videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                _mediaUrlService ??= DiLocator.ResolveViewModel<MediaUrlService>();
                if (_mediaUrlService == null)
                    return null;

                var resolvedSource = await _mediaUrlService.ResolveMediaSourceAsync(videoUrl, preferVideo: true).ConfigureAwait(false);
                if (resolvedSource == null)
                    return null;

                previewItem.OnlineUrls = resolvedSource.OnlineUrls;
                aspectRatio = resolvedSource.AspectRatio;
            }

            return new GameplayPreviewSource(previewItem, aspectRatio);
        }

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

        private static Bitmap? LoadBitmap(string imagePath)
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
    }
}
