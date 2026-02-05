namespace AES_Core.Interfaces;

/// <summary>
/// Base contract for view-models used by the application. Defines
/// lifecycle hooks that view-model implementations can use to prepare
/// state, respond when shown or hidden, and persist settings.
/// </summary>
public interface IViewModelBase
{
    /// <summary>
    /// Perform any initialization or preparation required after the
    /// view-model instance has been created. Called once prior to use.
    /// </summary>
    void Prepare();

    /// <summary>
    /// Called when the view associated with this view-model is about to be
    /// displayed. Implementations may reset or refresh transient state
    /// necessary for presentation.
    /// </summary>
    void OnShowViewModel();

    /// <summary>
    /// Called before navigating away from this view-model. Use this hook
    /// to perform cleanup or to persist transient state before another
    /// view-model is shown.
    /// </summary>
    void OnLeaveViewModel();
    
    /// <summary>
    /// Persist view-model specific settings via the application's
    /// settings infrastructure.
    /// </summary>
    void SaveSettings();
    
    /// <summary>
    /// Load persisted settings and apply them to the view-model.
    /// </summary>
    void LoadSettings();
}
