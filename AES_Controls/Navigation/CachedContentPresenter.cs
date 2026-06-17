using AES_Core.Interfaces;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using System.Collections;
using System.Diagnostics;
using System.Windows.Input;

using log4net;
using AES_Core.Logging;

namespace AES_Controls.Navigation;

/// <summary>
/// A content presenter that caches generated view hosts for view-models and
/// re-uses them to avoid expensive re-creation. Supports optional sequential
/// fade-out-then-fade-in transitions between cached views and a warm-up API to pre-create views.
/// </summary>
/// <remarks>
/// The presenter stores a mapping from view-model objects to their
/// <see cref="ContentControl"/> hosts. When <see cref="CurrentViewModel"/>
/// changes the presenter will either immediately switch the visible host or
/// perform an animated cross-fade if transitions are enabled. Use
/// <see cref="WarmedViewModels"/> to pre-warm frequently-used view-models.
/// </remarks>
public class CachedContentPresenter : Control
{
    private static readonly ILog Log = LogHelper.For<CachedContentPresenter>();
    private static readonly TimeSpan BlackHoldDuration = TimeSpan.FromMilliseconds(100);
    private static readonly Easing FadeOutEasing = new SineEaseIn();
    private static readonly Easing FadeInEasing = new SineEaseOut();
    private readonly Dictionary<object, ContentControl> _cache = [];
    private readonly Dictionary<object, Control> _views = [];
    private readonly Dictionary<ContentControl, object> _hostViewModels = [];
    private readonly Panel _hostPanel = new();
    private CancellationTokenSource? _transitionCts;
    private bool _warmupInProgress;

    /// <summary>
    /// Backing styled property for the current view-model. When changed the
    /// presenter will switch the visible cached view to the one associated
    /// with the new view-model.
    /// </summary>
    public static readonly StyledProperty<object?> CurrentViewModelProperty =
        AvaloniaProperty.Register<CachedContentPresenter, object?>(nameof(CurrentViewModel));

    /// <summary>
    /// Collection of view-models to pre-warm. When set the presenter will
    /// attempt to create and measure hosts for these view-models so that they
    /// are ready for display without delay.
    /// </summary>
    public static readonly StyledProperty<List<IViewModelBase>?> WarmedViewModelsProperty =
        AvaloniaProperty.Register<CachedContentPresenter, List<IViewModelBase>?>(nameof(WarmedViewModels));

    /// <summary>
    /// Command that will be invoked when a transition to a new view has
    /// completed. The command parameter will be the new view-model instance.
    /// </summary>
    public static readonly StyledProperty<ICommand?> TransitionCompletedCommandProperty =
        AvaloniaProperty.Register<CachedContentPresenter, ICommand?>(nameof(TransitionCompletedCommand));

    /// <summary>
    /// Whether transitions (fade-out then fade-in) are enabled when switching views.
    /// </summary>
    public static readonly StyledProperty<bool> TransitionsEnabledProperty =
        AvaloniaProperty.Register<CachedContentPresenter, bool>(nameof(TransitionsEnabled), true);

    /// <summary>
    /// Duration of the view transition (split evenly between fade-out and fade-in).
    /// </summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<CachedContentPresenter, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(900));

    /// <summary>
    /// Easing used for the transition animation.
    /// </summary>
    public static readonly StyledProperty<Easing> EasingProperty =
        AvaloniaProperty.Register<CachedContentPresenter, Easing>(nameof(Easing), new SineEaseInOut());

    /// <summary>
    /// The currently displayed view-model. Setting this will switch the
    /// visible cached view to the one associated with the value.
    /// </summary>
    public object? CurrentViewModel { get => GetValue(CurrentViewModelProperty); set => SetValue(CurrentViewModelProperty, value); }

    /// <summary>
    /// A collection of view-models that should be pre-warmed (created and
    /// measured) so they appear instantly when requested.
    /// </summary>
    public IEnumerable? WarmedViewModels { get => GetValue(WarmedViewModelsProperty); set => SetValue(WarmedViewModelsProperty, value); }

    /// <summary>
    /// Optional command that will be executed after a transition completes.
    /// </summary>
    public ICommand? TransitionCompletedCommand { get => GetValue(TransitionCompletedCommandProperty); set => SetValue(TransitionCompletedCommandProperty, value); }

    /// <summary>
    /// Enables or disables animated transitions when switching views.
    /// </summary>
    public bool TransitionsEnabled { get => GetValue(TransitionsEnabledProperty); set => SetValue(TransitionsEnabledProperty, value); }

    /// <summary>
    /// The duration of the view transition animation used when transitions are
    /// enabled.
    /// </summary>
    public TimeSpan Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }

    /// <summary>
    /// The easing function used for the transition animation.
    /// </summary>
    public Easing Easing { get => GetValue(EasingProperty); set => SetValue(EasingProperty, value); }

    static CachedContentPresenter()
    {
        // Attach property change handlers
        CurrentViewModelProperty.Changed.AddClassHandler<CachedContentPresenter>((x, e) => x.OnViewModelChanged(e));
        WarmedViewModelsProperty.Changed.AddClassHandler<CachedContentPresenter>((x, e) => x.OnWarmedViewModelsChanged(e));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CachedContentPresenter"/> class
    /// and creates the internal host panel used to display cached view hosts.
    /// </summary>
    public CachedContentPresenter()
    {
        // Setup internal panel to host cached views
        _hostPanel.ClipToBounds = false;
        ClipToBounds = false;
        VisualChildren.Add(_hostPanel);
        LogicalChildren.Add(_hostPanel);
    }

    /// <summary>
    /// Handler invoked when the <see cref="WarmedViewModels"/> property
    /// changes. Begins asynchronous warm-up for the provided collection.
    /// </summary>
    private void OnWarmedViewModelsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is List<IViewModelBase> collection)
        {
            _ = WarmupAsync(collection);
        }
    }

    /// <summary>
    /// Invoked when the <see cref="CurrentViewModel"/> property changes.
    /// Ensures a cached host exists for the new view-model and either
    /// performs a sequential fade-out-then-fade-in transition from the previous view or switches
    /// immediately if transitions are disabled.
    /// </summary>
    private async void OnViewModelChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _transitionCts?.Cancel();
        _transitionCts = new CancellationTokenSource();
        var token = _transitionCts.Token;

        var oldViewModel = e.OldValue;
        var newViewModel = e.NewValue;

        if (newViewModel is null) return;

        // If the viewmodel didn't actually change, nothing to do
        if (ReferenceEquals(oldViewModel, newViewModel)) return;

        // Get or Create the new view
        if (!_cache.TryGetValue(newViewModel, out var newViewHost))
        {
            Warmup(newViewModel);
            newViewHost = _cache[newViewModel];
        }

        if (TransitionsEnabled && oldViewModel != null && _cache.TryGetValue(oldViewModel, out var oldViewHost))
        {
            try
            {
                await RunCrossFade(oldViewHost, newViewHost, token);
            }
            catch (TaskCanceledException)
            {
                return; // Navigation was superseded
            }
            if (!token.IsCancellationRequested)
            {
                // Call lifecycle hooks: old -> leave, new -> show
                try { (oldViewModel as IViewModelBase)?.OnLeaveViewModel(); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
                try { (newViewModel as IViewModelBase)?.OnShowViewModel(); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

                // Ensure only the active view is visible just in case
                foreach (var child in _hostPanel.Children)
                {
                    if (child != newViewHost) child.IsVisible = false;
                }
            }
        }
        else
        {
            // Immediate switch
            foreach (var child in _hostPanel.Children)
            {
                if (child != newViewHost) child.IsVisible = false;
            }
            newViewHost.IsVisible = true;
            newViewHost.Opacity = 1.0;
            SetViewTransitionOpacity(oldViewModel, 0.0);
            SetViewTransitionOpacity(newViewModel, 1.0);
            // Call lifecycle hooks synchronously for immediate switch
            try { (oldViewModel as IViewModelBase)?.OnLeaveViewModel(); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            try { (newViewModel as IViewModelBase)?.OnShowViewModel(); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }

        if (!token.IsCancellationRequested)
        {
            TransitionCompletedCommand?.Execute(newViewModel);
        }
    }

    /// <summary>
    /// Runs a sequential view transition: fade the outgoing view to black,
    /// then fade the incoming view in from black.
    /// </summary>
    private async Task RunCrossFade(ContentControl from, ContentControl to, CancellationToken token)
    {
        var phaseDurationMs = Math.Max(Duration.TotalMilliseconds / 2.0, 1.0);
        var oldViewModel = GetViewModelForHost(from);
        var newViewModel = GetViewModelForHost(to);
        var topLevel = TopLevel.GetTopLevel(this);

        from.Opacity = 1.0;
        from.IsVisible = true;
        to.Opacity = 0;
        to.IsVisible = false;
        SetViewTransitionOpacity(oldViewModel, 1.0);
        SetViewTransitionOpacity(newViewModel, 0.0);

        await WaitForRenderFrameAsync(topLevel, token).ConfigureAwait(true);

        await AnimateHostOpacity(from, oldViewModel, 1.0, 0.0, phaseDurationMs, FadeOutEasing, topLevel, token)
            .ConfigureAwait(true);

        from.Opacity = 0;
        from.IsVisible = false;
        SetViewTransitionOpacity(oldViewModel, 0.0);

        if (BlackHoldDuration > TimeSpan.Zero)
        {
            await Task.Delay(BlackHoldDuration, token).ConfigureAwait(true);
        }

        to.Opacity = 0;
        to.IsVisible = true;
        SetViewTransitionOpacity(newViewModel, 0.0);

        await WaitForRenderFrameAsync(topLevel, token).ConfigureAwait(true);

        await AnimateHostOpacity(to, newViewModel, 0.0, 1.0, phaseDurationMs, FadeInEasing, topLevel, token)
            .ConfigureAwait(true);

        to.Opacity = 1.0;
        SetViewTransitionOpacity(newViewModel, 1.0);
    }

    private async Task AnimateHostOpacity(
        ContentControl host,
        object? viewModel,
        double fromOpacity,
        double toOpacity,
        double durationMs,
        Easing easing,
        TopLevel? topLevel,
        CancellationToken token)
    {
        var durationSeconds = durationMs / 1000.0;
        var startTicks = Stopwatch.GetTimestamp();

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var elapsedSeconds = (Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency;
            var linearProgress = Math.Clamp(elapsedSeconds / durationSeconds, 0.0, 1.0);
            var eased = easing.Ease(linearProgress);
            var opacity = fromOpacity + (toOpacity - fromOpacity) * eased;

            host.Opacity = opacity;
            SetViewTransitionOpacity(viewModel, opacity);

            if (linearProgress >= 1.0)
                break;

            await WaitForRenderFrameAsync(topLevel, token).ConfigureAwait(true);
        }

        host.Opacity = toOpacity;
        SetViewTransitionOpacity(viewModel, toOpacity);
    }

    private static Task WaitForRenderFrameAsync(TopLevel? topLevel, CancellationToken token)
    {
        if (topLevel == null)
            return Task.Delay(8, token);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(TimeSpan _)
        {
            tcs.TrySetResult();
        }

        topLevel.RequestAnimationFrame(OnFrame);
        if (token.CanBeCanceled)
        {
            return tcs.Task.WaitAsync(token);
        }

        return tcs.Task;
    }

    private static void SetViewTransitionOpacity(object? viewModel, double opacity)
    {
        if (viewModel is IViewModelBase vm)
            vm.ViewTransitionOpacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    /// <summary>
    /// Builds the view control for a view-model without creating a host.
    /// Use this to pay the XAML/materialization cost as early as possible.
    /// </summary>
    public void PrewarmBuild(object viewModel)
    {
        if (viewModel == null)
            return;

        EnsureViewBuilt(viewModel);
    }

    /// <summary>
    /// Asynchronously warms a collection of view-models by creating their
    /// associated view hosts and measuring them. This helps reduce latency
    /// when switching to those views at runtime.
    /// </summary>
    public async Task WarmupAsync(List<IViewModelBase> viewModels)
    {
        if (_warmupInProgress)
            return;

        _warmupInProgress = true;
        var topLevel = TopLevel.GetTopLevel(this);
        try
        {
            foreach (var vm in viewModels)
            {
                if (vm == null || _cache.ContainsKey(vm))
                    continue;

                Warmup(vm);
                await PrerenderHostAsync(vm, topLevel, CancellationToken.None).ConfigureAwait(true);
                await WaitForRenderFrameAsync(topLevel, CancellationToken.None).ConfigureAwait(true);
            }

            Log.Info($"View warmup completed for {viewModels.Count} view-model(s).");
        }
        finally
        {
            _warmupInProgress = false;
        }
    }

    /// <summary>
    /// Creates and caches a view host for the provided view-model and forces
    /// its template to be applied and measured so it is ready for display.
    /// </summary>
    public void Warmup(object viewModel)
    {
        if (_cache.ContainsKey(viewModel))
            return;

        var view = EnsureViewBuilt(viewModel);
        var size = GetWarmupSize();
        var rect = new Rect(size);

        view.Measure(size);
        view.Arrange(rect);

        var viewHost = CreateViewHost(viewModel);
        _cache[viewModel] = viewHost;
        _hostPanel.Children.Add(viewHost);

        viewHost.Measure(size);
        viewHost.Arrange(rect);
    }

    private async Task PrerenderHostAsync(object viewModel, TopLevel? topLevel, CancellationToken token)
    {
        if (!_cache.TryGetValue(viewModel, out var host))
            return;

        SetViewTransitionOpacity(viewModel, 0.0);
        host.IsVisible = true;
        host.Opacity = 0;

        await WaitForRenderFrameAsync(topLevel, token).ConfigureAwait(true);
        await WaitForRenderFrameAsync(topLevel, token).ConfigureAwait(true);

        host.IsVisible = false;
        host.Opacity = 0;
        SetViewTransitionOpacity(viewModel, 0.0);
    }

    private Control EnsureViewBuilt(object viewModel)
    {
        if (_views.TryGetValue(viewModel, out var existing))
            return existing;

        var templates = Application.Current?.DataTemplates;
        if (templates == null)
            throw new InvalidOperationException("Application data templates are not available.");

        foreach (var template in templates)
        {
            if (!template.Match(viewModel))
                continue;

            var built = template.Build(viewModel);
            if (built == null)
                continue;

            built.DataContext = viewModel;
            _views[viewModel] = built;
            Log.Info($"Built view '{built.GetType().Name}' for '{viewModel.GetType().Name}'.");
            return built;
        }

        throw new InvalidOperationException($"No view template found for {viewModel.GetType().FullName}.");
    }

    private object? GetViewModelForHost(ContentControl host) =>
        _hostViewModels.TryGetValue(host, out var viewModel) ? viewModel : host.Content;

    private Size GetWarmupSize()
    {
        var size = Bounds.Size;
        if (size.Width > 0 && size.Height > 0)
            return size;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            size = topLevel.Bounds.Size;
            if (size.Width > 0 && size.Height > 0)
                return size;
        }

        return new Size(1920, 1080);
    }

    /// <summary>
    /// Creates a new <see cref="ContentControl"/> configured to host the
    /// specified view-model. The control is initially hidden and ready for
    /// use by the presenter.
    /// </summary>
    private ContentControl CreateViewHost(object viewModel)
    {
        var host = new ContentControl
        {
            Content = EnsureViewBuilt(viewModel),
            IsVisible = false,
            Opacity = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            ClipToBounds = false
        };
        _hostViewModels[host] = viewModel;
        return host;
    }

    /// <summary>
    /// Measures the internal host panel and returns its desired size.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _hostPanel.Measure(availableSize);
        return _hostPanel.DesiredSize;
    }

    /// <summary>
    /// Arranges the internal host panel to fill the final layout slot.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _hostPanel.Arrange(new Rect(finalSize));
        return finalSize;
    }
}