using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace AES_Controls.Behaviors;

/// <summary>
/// Ensures a <see cref="ComboBox"/> participates in <see cref="ComboBoxDropDownOpenTracker"/>.
/// </summary>
public sealed class ComboBoxDropDownTrackerBehavior : Behavior<ComboBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
            ComboBoxDropDownOpenTracker.EnsureTracking(AssociatedObject);
    }
}
