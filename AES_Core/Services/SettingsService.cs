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
    /// Internal list of registered settings-aware components.
    /// </summary>
    private List<ISetting> SettingsList { get; } = [];

    private readonly object _lock = new();

    /// <summary>
    /// Register a settings-capable component with the service.
    /// </summary>
    /// <param name="setting">Settings-aware component to register (not null).</param>
    public void AddSettingsItem(ISetting setting)
    {
        lock (_lock)
        {
            SettingsList.Add(setting);
        }
    }
    
    /// <summary>
    /// Persist all registered settings by delegating to each
    /// <see cref="ISetting.SaveSettings"/> implementation.
    /// </summary>
    public void SaveSettings()
    {
        List<ISetting> snapshot;
        lock (_lock)
        {
            snapshot = [.. SettingsList];
        }

        foreach (var setting in snapshot)
        {
            setting.SaveSettings();
        }
    }
}
