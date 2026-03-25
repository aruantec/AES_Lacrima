using System;
using System.Collections.Generic;
using AES_Lacrima.Mini.ViewModels;
using AES_Lacrima.Mini.Views;
using AES_Lacrima.ViewModels.Prompts;
using AES_Lacrima.Views;
using AES_Lacrima.Views.Prompts;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace AES_Lacrima
{
    /// <summary>
    /// Simple view locator implementing <see cref="IDataTemplate"/>.
    ///
    /// Maps known view-model types to pre-registered view factories so
    /// view activation stays explicit and Native AOT-friendly.
    /// </summary>
    public class ViewLocator : IDataTemplate
    {
        private static readonly IReadOnlyDictionary<Type, Func<Control>> ViewFactories =
            new Dictionary<Type, Func<Control>>
            {
                [typeof(AppUpdatePromptViewModel)] = static () => new AppUpdatePromptView(),
                [typeof(ComponentSetupPromptViewModel)] = static () => new ComponentSetupPromptView(),
                [typeof(RestartPromptViewModel)] = static () => new RestartPromptView(),
                [typeof(ViewModels.EmulationViewModel)] = static () => new EmulationView(),
                [typeof(ViewModels.MainContentViewModel)] = static () => new MainContentView(),
                [typeof(ViewModels.MainWindowViewModel)] = static () => new MainWindow(),
                [typeof(MiniEqualizerViewModel)] = static () => new MiniEqualizerView(),
                [typeof(MinViewModel)] = static () => new MinView(),
                [typeof(ViewModels.MusicViewModel)] = static () => new MusicView(),
                [typeof(ViewModels.VideoViewModel)] = static () => new VideoView(),
                [typeof(VisualizerViewModel)] = static () => new VisualizerView()
            };

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

            if (ViewFactories.TryGetValue(param.GetType(), out var factory))
            {
                return factory();
            }

            var viewName = param.GetType().FullName?.Replace("ViewModel", "View", StringComparison.Ordinal)
                           ?? param.GetType().Name;
            return new TextBlock { Text = "Not Found: " + viewName };
        }

        /// <summary>
        /// Determine whether the provided data object is a view-model that this
        /// locator can provide a view for.
        /// </summary>
        /// <param name="data">The data object to evaluate.</param>
        /// <returns><c>true</c> when <paramref name="data"/> is a <see cref="ViewModelBase"/>; otherwise <c>false</c>.</returns>
        public bool Match(object? data)
        {
            return data is ViewModels.ViewModelBase;
        }
    }
}
