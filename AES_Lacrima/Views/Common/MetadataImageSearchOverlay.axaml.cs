using System;
using AES_Controls.Composition;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima;

public partial class MetadataImageSearchOverlay : UserControl
{
    public static readonly StyledProperty<CoverLayoutMode> CoverLayoutModeProperty =
        AvaloniaProperty.Register<MetadataImageSearchOverlay, CoverLayoutMode>(
            nameof(CoverLayoutMode),
            CoverLayoutMode.Carousel);

    public static readonly StyledProperty<string?> PreviewTitleProperty =
        AvaloniaProperty.Register<MetadataImageSearchOverlay, string?>(nameof(PreviewTitle));

    public CoverLayoutMode CoverLayoutMode
    {
        get => GetValue(CoverLayoutModeProperty);
        set => SetValue(CoverLayoutModeProperty, value);
    }

    public string? PreviewTitle
    {
        get => GetValue(PreviewTitleProperty);
        set => SetValue(PreviewTitleProperty, value);
    }

    public MetadataImageSearchOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnOverlayPropertyChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => SyncPreviewBindings();

    private void OnOverlayPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == CoverLayoutModeProperty || e.Property == PreviewTitleProperty)
            SyncPreviewBindings();
    }

    private void SyncPreviewBindings()
    {
        if (DataContext is not MetadataService metadata)
            return;

        metadata.CoverPreviewLayoutMode = CoverLayoutMode;
        metadata.ImageSearchPreviewTitle = PreviewTitle;
    }

    private void OnResultPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MetadataService metadata)
            return;

        if (sender is Control { DataContext: WebImageSearchResult result })
            metadata.SetImageSearchPreview(result);
    }

    private void OnResultsPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is MetadataService metadata)
            metadata.ClearImageSearchPreview();
    }
}
