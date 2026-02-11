using AES_Core.Interfaces;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Collections;
using System.Windows.Input;

namespace AES_Controls.Navigation;

/// <summary>
/// A content presenter that caches generated view hosts for view-models and
/// re-uses them to avoid expensive re-creation. Supports optional cross-fade
/// transitions between cached views and a warm-up API to pre-create views.
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
    private readonly Dictionary<object, ContentControl> _cache = [];
    private readonly Panel _hostPanel = new();
    private CancellationTokenSource? _transitionCts;

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
    /// Whether transitions (cross-fade) are enabled when switching views.
    /// </summary>
    public static readonly StyledProperty<bool> TransitionsEnabledProperty =
        AvaloniaProperty.Register<CachedContentPresenter, bool>(nameof(TransitionsEnabled), true);

    /// <summary>
    /// Duration of the cross-fade transition.
    /// </summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<CachedContentPresenter, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(500));

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
    /// The duration of the cross-fade animation used when transitions are
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
    /// performs a cross-fade transition from the previous view or switches
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
            newViewHost = CreateViewHost(newViewModel);
            _hostPanel.Children.Add(newViewHost);
            _cache[newViewModel] = newViewHost;
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
                try { (oldViewModel as IViewModelBase)?.OnLeaveViewModel(); } catch { }
                try { (newViewModel as IViewModelBase)?.OnShowViewModel(); } catch { }
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
            // Call lifecycle hooks synchronously for immediate switch
            try { (oldViewModel as IViewModelBase)?.OnLeaveViewModel(); } catch { }
            try { (newViewModel as IViewModelBase)?.OnShowViewModel(); } catch { }
        }

        if (!token.IsCancellationRequested)
        {
            TransitionCompletedCommand?.Execute(newViewModel);
        }
    }

    /// <summary>
    /// Runs a cross-fade transition between two content hosts. This method
    /// prepares the target view, assigns transitions, and awaits the
    /// completion of the animation or cancellation via the provided token.
    /// </summary>
    private async Task RunCrossFade(ContentControl from, ContentControl to, CancellationToken token)
    {
        var duration = Duration;

        // Prepare the destination view to avoid a one-frame flash.
        to.Opacity = 0;
        to.IsVisible = true;

        // Wait one frame for layout to recognize visibility and prepare the view
        await Task.Delay(16, token);

        // Setup transitions
        var transition = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = Easing } };
        from.Transitions = transition;
        to.Transitions = transition;

        // Start animations by changing the target properties. The transitions
        // assigned above will animate the property changes.
        from.Opacity = 0;
        to.Opacity = 1.0;

        await Task.Delay(duration, token);

        if (token.IsCancellationRequested) return;

        from.IsVisible = false;
        from.Transitions = null;
        to.Transitions = null;
    }

    /// <summary>
    /// Asynchronously warms a collection of view-models by creating their
    /// associated view hosts and measuring them. This helps reduce latency
    /// when switching to those views at runtime.
    /// </summary>
    public async Task WarmupAsync(List<IViewModelBase> viewModels)
    {
        foreach (var vm in viewModels)
        {
            if (vm == null || _cache.ContainsKey(vm)) continue;
            Warmup(vm);
            await Task.Delay(16); // allow UI to breathe between warmups
        }
    }

    /// <summary>
    /// Creates and caches a view host for the provided view-model and forces
    /// its template to be applied and measured so it is ready for display.
    /// </summary>
    public void Warmup(object viewModel)
    {
        if (_cache.ContainsKey(viewModel)) return;

        var viewHost = CreateViewHost(viewModel);
        _cache[viewModel] = viewHost;
        _hostPanel.Children.Add(viewHost);

        // Force templates and measure to ensure the view is ready for layout
        viewHost.ApplyTemplate();
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(1920, 1080);
        viewHost.Measure(size);
    }

    /// <summary>
    /// Creates a new <see cref="ContentControl"/> configured to host the
    /// specified view-model. The control is initially hidden and ready for
    /// use by the presenter.
    /// </summary>
    private ContentControl CreateViewHost(object viewModel)
    {
        return new ContentControl
        {
            Content = viewModel,
            IsVisible = false,
            Opacity = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
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