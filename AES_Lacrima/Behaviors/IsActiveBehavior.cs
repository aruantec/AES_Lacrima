using System;
using AES_Controls.Helpers;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// A behavior that sets the <c>IsVisible</c> property of the associated control
/// to the value of the <c>IsActive</c> property of the associated view model.
/// </summary>
public class IsActiveBehavior : Behavior<Control>
{
    // Add a bindable property so the behavior can be bound to from XAML.
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<IsActiveBehavior, bool>(nameof(IsActive), defaultValue: false);

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private IDisposable? _isActiveSubscription;

    /// <summary>
    /// Called when the behavior is attached to a <see cref="Control"/>.
    /// </summary>
    protected override void OnAttached()
    {
        base.OnAttached();
        
        // Listen to changes to the behavior's IsActive property so bindings can control visibility.
        _isActiveSubscription = this.GetObservable(IsActiveProperty).Subscribe(new SimpleObserver<bool>(value =>
        {
            if (DiLocator.ResolveViewModel<MainContentViewModel>() is { } mainContentViewModel)
            {
                // Set the associated control's visibility based on the IsActive property.
                AssociatedObject?.SetValue(Visual.IsVisibleProperty, mainContentViewModel.IsActive);
            }
        }));
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();

        // Unsubscribe the behavior's observable subscription
        _isActiveSubscription?.Dispose();
        _isActiveSubscription = null;
    }
}