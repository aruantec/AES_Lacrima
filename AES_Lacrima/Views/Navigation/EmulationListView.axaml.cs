using AES_Controls.Composition;
using Avalonia;
using Avalonia.Controls;

namespace AES_Lacrima.Views.Navigation;

public partial class EmulationListView : UserControl
{
    public static readonly StyledProperty<double> PresentationOpacityProperty =
        AvaloniaProperty.Register<EmulationListView, double>(nameof(PresentationOpacity), 1.0);

    public double PresentationOpacity
    {
        get => GetValue(PresentationOpacityProperty);
        set => SetValue(PresentationOpacityProperty, value);
    }

    public EmulationListView()
    {
        InitializeComponent();
    }

    public void RefreshAlbumTileCovers() =>
        this.FindControl<CompositionAlbumRowControl>("AlbumList")?.RefreshAllTileCovers();

    public void ResetAlbumListScroll() =>
        this.FindControl<CompositionAlbumRowControl>("AlbumList")?.ResetScrollPositionOnViewShown();
}
