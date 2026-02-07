using System;
using AES_Lacrima.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace AES_Lacrima
{
    /// <summary>
    /// Simple view locator implementing <see cref="IDataTemplate"/>.
    ///
    /// Maps a view-model instance to a view by replacing the suffix
    /// "ViewModel" with "View" in the view-model's full type name and
    /// attempting to instantiate the corresponding control type via
    /// <see cref="Activator.CreateInstance(Type)"/>.
    /// </summary>
    public class ViewLocator : IDataTemplate
    {
        /// <summary>
        /// Build a view for the supplied view-model parameter.
        /// </summary>
        /// <param name="param">The view-model instance for which to create a view. May be <c>null</c>.</param>
        /// <returns>
        /// A <see cref="Control"/> instance for the view-model, or <c>null</c> when <paramref name="param"/> is <c>null</c>.
        /// If the corresponding view type cannot be found a <see cref="TextBlock"/> with an error message is returned.
        /// </returns>
        public Control? Build(object? param)
        {
            if (param is null)
                return null;

            var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            var type = Type.GetType(name);

            if (type != null)
            {
                return (Control)Activator.CreateInstance(type)!;
            }

            return new TextBlock { Text = "Not Found: " + name };
        }

        /// <summary>
        /// Determine whether the provided data object is a view-model that this
        /// locator can provide a view for.
        /// </summary>
        /// <param name="data">The data object to evaluate.</param>
        /// <returns><c>true</c> when <paramref name="data"/> is a <see cref="ViewModelBase"/>; otherwise <c>false</c>.</returns>
        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }
    }
}
