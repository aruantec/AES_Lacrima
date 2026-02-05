namespace AES_Core.Interfaces;

/// <summary>
/// Represents a settings-capable component. Implementations are
/// responsible for persisting and restoring their configuration or
/// state using the application's settings infrastructure.
/// </summary>
public interface ISetting
{
    /// <summary>
    /// Persist current settings/state to the underlying storage.
    /// Implementations should handle any necessary serialization and
    /// error handling as appropriate for the application.
    /// </summary>
    void SaveSettings();

    /// <summary>
    /// Load settings/state from the underlying storage and apply them
    /// to the implementing component. This is typically called during
    /// initialization or when restoring configuration.
    /// </summary>
    void LoadSettings();

    /// <summary>
    /// Remove the saved settings section associated with this component.
    /// Use this to clear persisted settings when they are no longer
    /// required or need resetting to defaults.
    /// </summary>
    void RemoveSavedSection();
}
