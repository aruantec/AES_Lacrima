using AES_Controls.Composition;
using AES_Lacrima.ViewModels;
using Avalonia;

namespace AES_Tests.AES_Lacrima;

public sealed class MainContentWidgetLayoutTests
{
    [Fact]
    public void Apply_centers_disc_on_panel_and_spans_full_width_player_info()
    {
        const double width = MainContentWidgetLayout.ReferenceContainerWidth;
        const double height = MainContentWidgetLayout.ReferenceContainerHeight;
        var vm = new MainContentViewModel();

        MainContentWidgetLayout.Apply(vm, width, height, MainContentViewModel.MainMenuHeight);

        Assert.Equal(0, vm.PlayerInfoLeft);
        Assert.Equal(width, vm.PlayerInfoWidth, 0.5);
        Assert.InRange(vm.PlayerInfoTop, height - MainContentViewModel.MainMenuHeight - vm.PlayerInfoHeight - 1, height);

        var discCenter = PlayerCompositionControl.GetDiscCenterInBounds(new Size(vm.PlayerWidth, vm.PlayerHeight));
        var centerX = vm.PlayerLeft + discCenter.X;
        var centerY = vm.PlayerTop + discCenter.Y;
        Assert.InRange(centerX, width * 0.5 - 1, width * 0.5 + 1);
        Assert.InRange(centerY, height * 0.5 - 1, height * 0.5 + 1);
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
}
