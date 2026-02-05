using AES_Core.DI;
using Autofac;
using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace AES_Core;

/// <summary>
/// Helper to configure and build the Autofac container for the application.
/// Scans provided assemblies for types annotated with <c>AutoRegisterAttribute</c>.
/// </summary>
public static class ContainerConfig
{
    private static readonly ILog Logger = LogManager.GetLogger(typeof(ContainerConfig));

    /// <summary>
    /// Builds and returns an Autofac container using registrations discovered in the provided assemblies.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan for registrations (must not be null).</param>
    /// <param name="customActionRegister">Optional callback to perform additional registrations on the builder.</param>
    /// <returns>The built <see cref="IContainer"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assemblies"/> is null.</exception>
    public static IContainer Configure(IEnumerable<Assembly> assemblies, Action<ContainerBuilder>? customActionRegister = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        // Log start
        Logger.Info("Running Configuration");
        // Create builder
        var builder = new ContainerBuilder();
        // Invoke custom registrations
        customActionRegister?.Invoke(builder);
        // Apply any registrations provided by generated module-initializers
        // (preferred, avoids reflection and is AOT-friendly).
        DiLocator.ApplyRegistrations(builder);

        // Registrations are provided by generated module-initializers
        // via DiLocator.AddRegistration. No runtime reflection is required.
        // Build container
        IContainer container;
        try
        {
            container = builder.Build();
        }
        catch (Exception ex)
        {
            Logger.Error("Container build failed", ex);
            throw;
        }
        // Log completion
        Logger.Info("Configuration completed successfully");
        // Return container
        return container;
    }
}