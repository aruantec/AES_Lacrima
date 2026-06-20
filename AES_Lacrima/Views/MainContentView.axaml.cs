using System;
using Avalonia;
using Avalonia.Controls;
using AES_Lacrima.ViewModels;

namespace AES_Lacrima.Views;

public partial class MainContentView : UserControl
{
    private bool _defaultLayoutApplied;
    private bool _layoutReconciled;
    private Size _lastReconciledContainerSize;

    public MainContentView()
    {
        InitializeComponent();
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not MainContentViewModel vm)
            return;

        var bounds = WidgetPanel?.Bounds ?? default;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (!_defaultLayoutApplied)
        {
            if (vm.UsesDefaultWidgetLayout)
                vm.ApplyDefaultWidgetLayout(bounds.Width, bounds.Height);
            _defaultLayoutApplied = true;
        }

        if (Math.Abs(bounds.Width - _lastReconciledContainerSize.Width) > 0.5 ||
            Math.Abs(bounds.Height - _lastReconciledContainerSize.Height) > 0.5 ||
            !_layoutReconciled)
        {
            vm.ReconcileWidgetLayout(bounds.Width, bounds.Height);
            _lastReconciledContainerSize = bounds.Size;
            _layoutReconciled = true;
        }
    }
}