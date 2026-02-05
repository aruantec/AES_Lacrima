using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using Autofac;

namespace AES_Core.DI;

/// <summary>
/// Central runtime registry for dependency registration callbacks and
/// a simple service locator wrapper around an Autofac lifetime scope.
/// 
/// The source generator emits a module-initializer into each target
/// assembly which calls <see cref="AddRegistration"/> to register an
/// <see cref="Action{ContainerBuilder}"/> callback. At container
/// construction time <see cref="ApplyRegistrations"/> is invoked to
/// apply all collected registrations without using reflection (AOT-friendly).
/// </summary>
public static class DiLocator
{
    private static ILifetimeScope? _scope;
    private static IEnumerable<Assembly> _assemblyList = [];
    private static Action<ContainerBuilder>? _builderAction;
    // Registrations collected from generated module-initializers.
    private static readonly List<Action<ContainerBuilder>> _registrations = new();

    /// <summary>
    /// Set execution assemblies
    /// </summary>
    /// <param name="assemblies">Assemblies to use</param>
    /// <param name="customActionRegister">Optional additional registration callback to run when building the container.</param>
    public static void SetExecutionAssemblies(IEnumerable<Assembly>? assemblies, Action<ContainerBuilder>? customActionRegister = null)
    {
        if (assemblies == null) return;
        _builderAction = customActionRegister;
        //Set assembly list
        _assemblyList = assemblies;
        //Register
        _scope = ContainerConfig.Configure(_assemblyList, _builderAction).BeginLifetimeScope();
    }

    /// <summary>
    /// Add a registration callback. Intended to be called from generated
    /// module-initializers so runtime code does not need to use reflection
    /// to discover registration entrypoints.
    /// </summary>
    /// <param name="registration">Callback that accepts a <see cref="ContainerBuilder"/> and performs registrations.</param>
    public static void AddRegistration(Action<ContainerBuilder> registration)
    {
        if (registration == null) return;
        lock (_registrations)
        {
            _registrations.Add(registration);
        }
    }

    /// <summary>
    /// Apply all registrations previously added via <see cref="AddRegistration"/>.
    /// Called from <c>ContainerConfig.Configure</c> to perform registrations
    /// without using reflection.
    /// </summary>
    /// <param name="builder">The Autofac <see cref="ContainerBuilder"/> to apply callbacks to.</param>
    public static void ApplyRegistrations(ContainerBuilder builder)
    {
        if (builder == null) return;
        lock (_registrations)
        {
            foreach (var reg in _registrations)
            {
                try
                {
                    reg(builder);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DI] Error applying registration: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Provides the viewmodel object with the provided type
    /// </summary>
    /// <returns>The viewmodel requested</returns>
    /// <typeparam name="T">Requested service type.</typeparam>
    /// <returns>Resolved instance of <typeparamref name="T"/> or <c>null</c> if not available.</returns>
    public static T? ResolveViewModel<T>()
    {
        //Try to resolve requested instance
        return (T?)(TryResolve(typeof(T)) ?? null);
    }

    /// <summary>
    /// Attempt to resolve a service by <see cref="Type"/> from the internal lifetime scope.
    /// </summary>
    /// <param name="type">The service type to resolve.</param>
    /// <returns>Resolved object or <c>null</c> if resolution failed or the scope is not initialized.</returns>
    public static object? TryResolve(Type type)
    {
        if (_scope == null) return null;
        _scope.TryResolve(type, out object? result);
        return result;
    }
    
    /// <summary>
    /// Dispose the internal lifetime scope and clear the cached scope reference.
    /// </summary>
    public static void Dispose()
    {
        _scope?.Dispose();
        _scope = null;
    }
}