using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace AES_Lacrima.Behaviors
{
    /// <summary>
    /// Enables window dragging behavior for a visual element by allowing the user to click and drag the associated
    /// control to move the window.
    /// </summary>
    internal class WindowDragBehavior : Behavior<Visual>
    {
        protected override void OnAttached()
        {
            //Subscribe to the PointerPressed event of the associated control
            //to enable dragging the window when the user clicks and drags on it.
            if (AssociatedObject is Control control && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                control.PointerPressed += (_, e) =>
                {
                    if (desktop.MainWindow?.WindowState != WindowState.FullScreen)
                        desktop.MainWindow?.BeginMoveDrag(e);
                };
            }
        }
    }
}