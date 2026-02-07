using AES_Core.DI;
using Avalonia.Markup.Xaml;
using System;

namespace AES_Lacrima.Helpers
{
    /// <summary>
    /// Markup extension that resolves a service instance for a given <see cref="Type"/>
    /// from the application's DI locator. Intended for use in XAML to obtain
    /// view-models or other services directly from the container.
    /// </summary>
    public class TypeResolverExtension : MarkupExtension
    {
        /// <summary>
        /// Parameterless constructor required for XAML usage.
        /// </summary>
        public TypeResolverExtension() { }

        /// <summary>
        /// Construct the extension with the target <see cref="Type"/> to resolve.
        /// </summary>
        /// <param name="type">The service type to resolve from the DI container.</param>
        public TypeResolverExtension(Type type) { Type = type; }

        /// <summary>
        /// The <see cref="Type"/> that should be resolved from the DI container.
        /// Must be set before calling <see cref="ProvideValue(IServiceProvider)"/>.
        /// </summary>
        public Type? Type { get; set; }

        /// <summary>
        /// Provide the resolved instance for XAML. Throws <see cref="InvalidOperationException"/>
        /// when <see cref="Type"/> is not specified. Uses <see cref="DiLocator.TryResolve(System.Type)"/>
        /// to obtain the instance from the application's DI scope.
        /// </summary>
        /// <param name="serviceProvider">XAML service provider (unused).</param>
        /// <returns>The resolved service instance or throws when <see cref="Type"/> is null.</returns>
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return Type == null ? throw new InvalidOperationException("Type must be specified.") : DiLocator.TryResolve(Type)!;
        }
    }
}