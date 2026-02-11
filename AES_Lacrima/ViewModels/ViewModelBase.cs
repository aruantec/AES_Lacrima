using AES_Core.Interfaces;
using AES_Lacrima.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels
{
    /// <summary>
    /// Common base class for application view-models.
    /// Provides default lifecycle hooks and integrates with the
    /// application's settings persistence via <see cref="SettingsBase"/>.
    /// </summary>
    public partial class ViewModelBase : SettingsBase, IViewModelBase
    {
        /// <summary>
        /// Determines if the view-model is active
        /// </summary>
        [ObservableProperty]
        private bool _isActive;

        /// <summary>
        /// Indicates whether the view-model has been prepared (initialized).
        /// Implementations should set this to <c>true</c> once one-time
        /// initialization has completed.
        /// </summary>
        public virtual bool IsPrepared { get; set; }

        /// <summary>
        /// Perform any initialization or preparation required after the
        /// view-model instance has been created. This method is intended
        /// to be called once prior to the view-model being used.
        /// </summary>
        public virtual void Prepare()
        {
            // Override if needed
            IsActive = true;
        }

        /// <summary>
        /// Called when the view associated with this view-model is about to be
        /// displayed. Override to refresh transient UI state or start
        /// short-lived operations tied to the view being visible.
        /// </summary>
        public virtual void OnShowViewModel()
        {
            // Override if needed
            IsActive = true;
        }

        /// <summary>
        /// Called before navigating away from this view-model. Use this hook
        /// to stop operations started in <see cref="OnShowViewModel"/>, to
        /// release resources, or to persist transient state.
        /// </summary>
        public virtual void OnLeaveViewModel()
        {
            // Override if needed
            IsActive = false;
        }
    }
}
