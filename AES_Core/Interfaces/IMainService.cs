using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Core.Interfaces;

/// <summary>
/// Main application service abstraction. Provides application-level
/// state (paths, time information) and operations used by the UI layer
/// such as view-model preparation and common window commands.
/// </summary>
public interface IMainService
{
    /// <summary>
    /// List of Shadertoy asset file paths available to the application.
    /// May be <c>null</c> if no files have been discovered.
    /// </summary>
    List<string>? ShadertoyFiles { get; set; }

    /// <summary>
    /// Path to the ffmpeg executable used by media features.
    /// </summary>
    string FfmpegPath { get; set; }
    
    /// <summary>
    /// Current time string formatted for display in the UI, or <c>null</c> when not set.
    /// </summary>
    string? CurrentTime { get; set; }

    /// <summary>
    /// Current day and date-of-week string for display (e.g. "Mon, 01 Jan").
    /// </summary>
    string? CurrentDateOfWeek { get; set; }

    /// <summary>
    /// Initialize the service with a reference to the main application controller.
    /// Called during application startup to provide the service with context and callbacks.
    /// </summary>
    /// <param name="controller">The application <see cref="IMainController"/> instance.</param>
    void InitService(IMainController controller);

    /// <summary>
    /// Prepare a view-model instance asynchronously. This method can be used to
    /// perform any UI-thread or background initialization required before a
    /// view-model is presented.
    /// </summary>
    /// <param name="vm">The view-model instance to prepare (may be <c>null</c>).</param>
    /// <param name="preparedAction">Optional callback invoked with the view-model when preparation completes.</param>
    /// <param name="ct">Cancellation token to cancel the preparation operation.</param>
    /// <returns>A task that completes when the preparation is finished.</returns>
    Task PrepareViewModelAsync(object? vm, Action<object>? preparedAction, CancellationToken ct = default);
    
    /// <summary>
    /// Command that requests the application/window to close.
    /// </summary>
    IRelayCommand CloseCommand { get; }
    
    /// <summary>
    /// Command that requests the application/window to minimize.
    /// </summary>
    IRelayCommand MinimizeCommand { get; }
    
    /// <summary>
    /// Command that toggles or requests window maximize behaviour. The command parameter may be used to pass additional context.
    /// </summary>
    IRelayCommand<object> MaximizeCommand { get; }
    
    /// <summary>
    /// Command that toggles or requests full-screen mode. The command parameter may be used to pass additional context.
    /// </summary>
    IRelayCommand<object> FullScreenCommand { get; }
}
