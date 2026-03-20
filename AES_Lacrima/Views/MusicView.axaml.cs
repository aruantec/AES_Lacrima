using Avalonia.Controls;
using Avalonia.Threading;
using AES_Lacrima.ViewModels;
using System.ComponentModel;

namespace AES_Lacrima.Views;

public partial class MusicView : UserControl
{
    private MusicViewModel? _viewModel;

    public MusicView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MusicViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null) return;

        if (e.PropertyName == nameof(MusicViewModel.IsAddUrlPopupOpen) && _viewModel.IsAddUrlPopupOpen)
        {
            FocusPopupTextBox(AddUrlTextBox);
        }
        else if (e.PropertyName == nameof(MusicViewModel.IsAddPlaylistPopupOpen) && _viewModel.IsAddPlaylistPopupOpen)
        {
            FocusPopupTextBox(AddPlaylistTextBox);
        }
    }

    private static void FocusPopupTextBox(TextBox? textBox)
    {
        if (textBox == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
