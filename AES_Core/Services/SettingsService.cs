using System.Collections.Generic;
using AES_Core.DI;
using AES_Core.Interfaces;

namespace AES_Core.Services;

/// <summary>
/// Concrete implementation of <see cref="ISettingsService"/> that
/// aggregates registered <see cref="ISetting"/> instances and is
/// responsible for invoking their save/load operations.
/// </summary>
[AutoRegister]
public partial class SettingsService : ISettingsService
{
    /// <summary>
    /// Internal list of registered settings-aware components. Items are
    /// added by dependency-injected components (the generator produces
    /// partials that call <c>AddSettingsItem</c> during initialization).
    /// </summary>
    private List<ISetting> SettingsList { get; } = [];

    /// <summary>
    /// Register a settings-capable component with the service. The service
    /// will include the provided item in subsequent save operations.
    /// </summary>
    /// <param name="setting">Settings-aware component to register (not null).</param>
    public void AddSettingsItem(ISetting setting)
    {
        SettingsList.Add(setting);
    }
    
    /// <summary>
    /// Persist all registered settings by delegating to each
    /// <see cref="ISetting.SaveSettings"/> implementation.
    /// </summary>
    public void SaveSettings()
    {
        foreach (var setting in SettingsList)
        {
            setting.SaveSettings();
        }
    }
}
