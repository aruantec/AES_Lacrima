using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// Executes a command when the associated control loses keyboard focus.
/// This replaces EventTriggerBehavior/InvokeCommandAction for AOT-safe XAML.
/// </summary>
public class ExecuteCommandOnLostFocusBehavior : Behavior<Control>
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ExecuteCommandOnLostFocusBehavior, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<ExecuteCommandOnLostFocusBehavior, object?>(nameof(CommandParameter));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
            AssociatedObject.LostFocus += AssociatedObject_LostFocus;
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject != null)
            AssociatedObject.LostFocus -= AssociatedObject_LostFocus;

        base.OnDetaching();
    }

    private void AssociatedObject_LostFocus(object? sender, RoutedEventArgs e)
    {
        var command = Command;
        var parameter = CommandParameter;
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }
}
