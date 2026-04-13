namespace AES_Core.DI;

/// <summary>
/// Specifies the lifetime of a service registered in the DI container.
/// </summary>
public enum DependencyLifetime
{
    /// <summary>
    /// A single instance is created for the entire application lifetime.
    /// </summary>
    Singleton,

    /// <summary>
    /// A unique instance is created per lifetime scope (Shared within a context).
    /// </summary>
    Scoped,

    /// <summary>
    /// A new instance is created every time the service is requested.
    /// </summary>
    Transient
}
