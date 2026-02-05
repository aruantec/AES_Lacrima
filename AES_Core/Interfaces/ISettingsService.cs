namespace AES_Core.Interfaces;

/// <summary>
/// Service responsible for managing application settings. Provides an
/// entrypoint for persisting all settings and for registering individual
/// settings-aware components with the central settings system.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Persist all registered settings to the underlying storage.
    /// Implementations should aggregate changes from registered
    /// <see cref="ISetting"/> instances and ensure they are saved.
    /// </summary>
    void SaveSettings();
    
    /// <summary>
    /// Register a settings-capable component with the service so it can be
    /// included during save/load operations.
    /// </summary>
    /// <param name="setting">The settings-aware component to register (not null).</param>
    void AddSettingsItem(ISetting setting);
}
