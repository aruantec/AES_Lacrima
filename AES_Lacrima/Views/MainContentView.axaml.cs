using System;
using Avalonia.Controls;
using AES_Lacrima.ViewModels;

namespace AES_Lacrima.Views;

public partial class MainContentView : UserControl
{
    private bool _defaultLayoutApplied;

    public MainContentView()
    {
        InitializeComponent();
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_defaultLayoutApplied || DataContext is not MainContentViewModel vm)
            return;

        var bounds = WidgetPanel?.Bounds ?? default;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (!vm.UsesDefaultWidgetLayout)
        {
            _defaultLayoutApplied = true;
            return;
        }

        vm.ApplyDefaultWidgetLayout(bounds.Width, bounds.Height);
        _defaultLayoutApplied = true;
    }
}