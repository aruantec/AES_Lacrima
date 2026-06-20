using AES_Controls.Composition;
using AES_Controls.Widgets;
using AES_Lacrima.ViewModels;
using Avalonia;

namespace AES_Tests.AES_Lacrima;

public sealed class MainContentWidgetLayoutTests
{
    [Fact]
    public void Apply_aligns_disc_with_shader_origin_and_spans_full_width_player_info()
    {
        const double width = MainContentWidgetLayout.ReferenceContainerWidth;
        const double height = MainContentWidgetLayout.ReferenceContainerHeight;
        var vm = new MainContentViewModel();

        MainContentWidgetLayout.Apply(vm, width, height, MainContentViewModel.MainMenuHeight);

        Assert.Equal(0, vm.PlayerInfoLeft);
        Assert.Equal(width, vm.PlayerInfoWidth, 0.5);
        Assert.InRange(vm.PlayerInfoTop, height - MainContentViewModel.MainMenuHeight - vm.PlayerInfoHeight - 1, height);

        Assert.Equal(width * MainContentWidgetLayout.PlayerWidthRatio, vm.PlayerWidth, 0.5);
        Assert.Equal(height * MainContentWidgetLayout.PlayerHeightRatio, vm.PlayerHeight, 0.5);

        var discCenter = PlayerCompositionControl.GetDiscCenterInBounds(new Size(vm.PlayerWidth, vm.PlayerHeight));
        var centerX = vm.PlayerLeft + discCenter.X;
        var centerY = vm.PlayerTop + discCenter.Y;
        Assert.InRange(centerX, MainContentWidgetLayout.GetShaderToyOriginX(width) - 0.5, MainContentWidgetLayout.GetShaderToyOriginX(width) + 0.5);
        Assert.InRange(centerY, MainContentWidgetLayout.GetShaderToyOriginY(height) - 0.5, MainContentWidgetLayout.GetShaderToyOriginY(height) + 0.5);

        Assert.Equal(0, vm.ClockLeft);
        Assert.Equal(0, vm.ClockTop);
    }

    [Fact]
    public void NormalizeLegacyAbsoluteValues_divides_render_scaled_settings()
    {
        double playerInfoLeft = 0;
        double playerInfoTop = 984;
        double playerInfoWidth = 2181.33;
        double playerInfoHeight = 233.87;
        double clockLeft = 12;
        double clockTop = 12;
        double clockWidth = 369.69;
        double clockHeight = 368.85;
        double playerLeft = 807.67;
        double playerTop = 322.67;
        double playerWidth = 709.03;
        double playerHeight = 744.08;

        MainContentWidgetLayout.NormalizeLegacyAbsoluteValues(
            ref playerInfoLeft,
            ref playerInfoTop,
            ref playerInfoWidth,
            ref playerInfoHeight,
            ref clockLeft,
            ref clockTop,
            ref clockWidth,
            ref clockHeight,
            ref playerLeft,
            ref playerTop,
            ref playerWidth,
            ref playerHeight,
            scaleFactor: 2,
            windowWidth: 1092);

        Assert.Equal(1090.66, playerInfoWidth, 0.5);
        Assert.Equal(354.51, playerWidth, 0.5);
    }

    [Fact]
    public void InferWidgetLayoutCustomizedFromPersistedLayout_marks_stale_factory_flag_as_customized()
    {
        var vm = new MainContentViewModel
        {
            PlayerLeft = 401,
            PlayerTop = 197,
            PlayerWidth = 354,
            PlayerHeight = 330,
            ClockLeft = 12,
            ClockTop = 12,
            ClockWidth = 184,
            ClockHeight = 184,
        };

        vm.InferWidgetLayoutCustomizedFromPersistedLayout(
            vm.PlayerLeft,
            vm.PlayerTop,
            vm.PlayerWidth,
            vm.PlayerHeight,
            vm.ClockLeft,
            vm.ClockTop,
            vm.ClockWidth,
            vm.ClockHeight);

        Assert.True(vm.WidgetLayoutUserCustomized);

        vm.ReconcileWidgetLayout(
            MainContentWidgetLayout.ReferenceContainerWidth,
            MainContentWidgetLayout.ReferenceContainerHeight);

        Assert.Equal(401, vm.PlayerLeft, 0.5);
        Assert.Equal(197, vm.PlayerTop, 0.5);
        Assert.Equal(354, vm.PlayerWidth, 0.5);
        Assert.Equal(330, vm.PlayerHeight, 0.5);
    }

    [Fact]
    public void ReconcileWidgetLayout_preserves_custom_player_position_after_save()
    {
        var vm = new MainContentViewModel
        {
            ClockLeft = 12,
            ClockTop = 12,
            PlayerInfoLeft = 0,
            PlayerInfoTop = 500,
            PlayerLeft = 401,
            PlayerTop = 180,
            PlayerWidth = 354,
            PlayerHeight = 372,
        };

        vm.SaveWidgetSettingsCommand.Execute(new WidgetMoveResizeEndedArgs(
            "Player",
            new MoveResizeResult(401, 180, 354, 372)));

        Assert.True(vm.WidgetLayoutUserCustomized);

        vm.ReconcileWidgetLayout(
            MainContentWidgetLayout.ReferenceContainerWidth,
            MainContentWidgetLayout.ReferenceContainerHeight);

        Assert.Equal(401, vm.PlayerLeft, 0.5);
        Assert.Equal(180, vm.PlayerTop, 0.5);
        Assert.Equal(354, vm.PlayerWidth, 0.5);
        Assert.Equal(372, vm.PlayerHeight, 0.5);
    }

    [Fact]
    public void ReconcileWidgetLayout_rescales_factory_player_layout_on_resize()
    {
        var vm = new MainContentViewModel();
        MainContentWidgetLayout.Apply(
            vm,
            MainContentWidgetLayout.ReferenceContainerWidth,
            MainContentWidgetLayout.ReferenceContainerHeight,
            MainContentViewModel.MainMenuHeight);

        Assert.False(vm.WidgetLayoutUserCustomized);

        const double resizedWidth = 1200;
        const double resizedHeight = 820;
        vm.ReconcileWidgetLayout(resizedWidth, resizedHeight);

        Assert.Equal(resizedWidth * MainContentWidgetLayout.PlayerWidthRatio, vm.PlayerWidth, 0.5);
        Assert.Equal(resizedHeight * MainContentWidgetLayout.PlayerHeightRatio, vm.PlayerHeight, 0.5);

        var discCenter = PlayerCompositionControl.GetDiscCenterInBounds(new Size(vm.PlayerWidth, vm.PlayerHeight));
        var centerX = vm.PlayerLeft + discCenter.X;
        var centerY = vm.PlayerTop + discCenter.Y;
        Assert.InRange(centerX, MainContentWidgetLayout.GetShaderToyOriginX(resizedWidth) - 0.5, MainContentWidgetLayout.GetShaderToyOriginX(resizedWidth) + 0.5);
        Assert.InRange(centerY, MainContentWidgetLayout.GetShaderToyOriginY(resizedHeight) - 0.5, MainContentWidgetLayout.GetShaderToyOriginY(resizedHeight) + 0.5);
    }
}
