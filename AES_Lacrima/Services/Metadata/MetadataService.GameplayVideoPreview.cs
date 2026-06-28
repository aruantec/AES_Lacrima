using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services;

public partial class MetadataService
{
    private void EnsureGameplayVideoPreviewPlayer()
    {
        if (_gameplayVideoPreviewPlayer != null)
        {
            _gameplayVideoPreviewPlayer.RepeatMode = RepeatMode.One;
            return;
        }

        var ffmpegManager = DiLocator.ResolveViewModel<FFmpegManager>();
        var mpvLibraryManager = DiLocator.ResolveViewModel<MpvLibraryManager>();
        _gameplayVideoPreviewPlayer = new AudioPlayer(ffmpegManager, mpvLibraryManager)
        {
            RepeatMode = RepeatMode.One
        };
        OnPropertyChanged(nameof(GameplayVideoPreviewPlayer));
    }

    private async Task StartGameplayVideoPreviewAsync(WebImageSearchResult? result)
    {
        StopGameplayVideoPreview();

        if (result == null || string.IsNullOrWhiteSpace(result.FullImageUrl))
            return;

        ImageSearchPreviewThumbnailUrl = result.ThumbnailUrl;
        var generation = ++_gameplayVideoPreviewGeneration;
        var cts = new CancellationTokenSource();
        _gameplayVideoPreviewCts = cts;
        var token = cts.Token;

        IsGameplayVideoPreviewLoading = true;

        try
        {
            _mediaUrlService ??= DiLocator.ResolveViewModel<MediaUrlService>();
            if (_mediaUrlService == null)
                return;

            var videoUrl = result.FullImageUrl;
            var previewItem = new MediaItem
            {
                FileName = videoUrl,
                Title = !string.IsNullOrWhiteSpace(result.Title) ? result.Title : Title,
                VideoUrl = videoUrl
            };

            var resolvedSource = await _mediaUrlService
                .ResolveMediaSourceAsync(videoUrl, preferVideo: true)
                .ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _gameplayVideoPreviewGeneration)
                return;

            if (resolvedSource == null)
                return;

            previewItem.OnlineUrls = resolvedSource.OnlineUrls;
            previewItem.MuxedStreamFallbackUrl = resolvedSource.MuxedFallbackUrl;

            await Dispatcher.UIThread.InvokeAsync(EnsureGameplayVideoPreviewPlayer, DispatcherPriority.Background);
            if (token.IsCancellationRequested || generation != _gameplayVideoPreviewGeneration)
                return;

            var player = _gameplayVideoPreviewPlayer;
            if (player == null)
                return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await player.PlayFile(previewItem, video: true);
            });
            if (token.IsCancellationRequested || generation != _gameplayVideoPreviewGeneration)
            {
                try
                {
                    player.Stop();
                }
                catch (Exception ex)
                {
                    SLog.Debug("Stopped stale gameplay search preview playback.", ex);
                }

                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _gameplayVideoPreviewGeneration)
                    IsGameplayVideoPreviewLoading = false;
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SLog.Warn("Gameplay video search preview failed.", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _gameplayVideoPreviewGeneration)
                    IsGameplayVideoPreviewLoading = false;
            }, DispatcherPriority.Background);
        }
    }

    internal void StopGameplayVideoSearchPreview() => StopGameplayVideoPreview();

    private void StopGameplayVideoPreview()
    {
        try
        {
            _gameplayVideoPreviewCts?.Cancel();
            _gameplayVideoPreviewCts?.Dispose();
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to cancel gameplay search preview.", ex);
        }
        finally
        {
            _gameplayVideoPreviewCts = null;
        }

        _gameplayVideoPreviewGeneration++;
        IsGameplayVideoPreviewLoading = false;
        ImageSearchPreviewThumbnailUrl = null;

        try
        {
            _gameplayVideoPreviewPlayer?.Stop();
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to stop gameplay search preview player.", ex);
        }
    }
}
