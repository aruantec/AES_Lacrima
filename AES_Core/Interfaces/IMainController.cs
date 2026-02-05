namespace AES_Core.Interfaces;

/// <summary>
/// Controller contract used by the application to host the current view
/// and perform navigation between view-models or views.
/// </summary>
public interface IMainController
{
    /// <summary>
    /// Gets or sets the current view object displayed by the application.
    /// This is typically a view-model instance used by the UI layer.
    /// </summary>
    object View { get; set; }
    
    /// <summary>
    /// Navigate to the specified view-model type. The concrete
    /// implementation determines how the navigation is performed and how
    /// the target is instantiated or resolved.
    /// </summary>
    /// <typeparam name="T">Target view-model type to navigate to.</typeparam>
    void NavigateTo<T>();
}
