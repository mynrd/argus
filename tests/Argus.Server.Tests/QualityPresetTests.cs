using Argus.Server.Capture;

namespace Argus.Server.Tests;

public class QualityPresetTests
{
    [Theory]
    [InlineData(QualityLevel.Preview, 400, 300, 50, 2)]
    [InlineData(QualityLevel.Low, 854, 480, 45, 6)]
    [InlineData(QualityLevel.Medium, 1280, 720, 65, 15)]
    [InlineData(QualityLevel.High, 1920, 1080, 80, 25)]
    public void Presets_expose_the_documented_resolution_quality_and_rate(
        QualityLevel level, int maxWidth, int maxHeight, int jpegQuality, int fps)
    {
        var preset = QualityPreset.For(level);

        Assert.Equal(maxWidth, preset.MaxWidth);
        Assert.Equal(maxHeight, preset.MaxHeight);
        Assert.Equal(jpegQuality, preset.JpegQuality);
        Assert.Equal(fps, preset.Fps);
    }

    [Fact]
    public void For_rejects_an_unknown_level()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityPreset.For((QualityLevel)99));
    }

    [Fact]
    public void Fit_scales_down_preserving_aspect_ratio()
    {
        var (width, height) = QualityPreset.Medium.Fit(1920, 1080);

        Assert.Equal(1280, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void Fit_never_scales_up()
    {
        // A small window stays small on High - upscaling would cost bandwidth for no detail.
        var (width, height) = QualityPreset.High.Fit(300, 200);

        Assert.Equal(300, width);
        Assert.Equal(200, height);
    }

    [Fact]
    public void Fit_constrains_by_height_for_tall_windows()
    {
        var (width, height) = QualityPreset.Preview.Fit(1000, 2000);

        Assert.True(height <= QualityPreset.Preview.MaxHeight);
        Assert.True(width <= QualityPreset.Preview.MaxWidth);
        Assert.Equal(150, width);
        Assert.Equal(300, height);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, -5)]
    public void Fit_degrades_safely_on_nonsense_input(int width, int height)
    {
        var (w, h) = QualityPreset.Medium.Fit(width, height);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
    }

    [Fact]
    public void Fit_never_returns_a_zero_dimension_for_extreme_aspect_ratios()
    {
        var (width, height) = QualityPreset.Preview.Fit(4000, 3);

        Assert.True(width >= 1);
        Assert.True(height >= 1);
    }

    [Fact]
    public void Frame_interval_matches_the_frame_rate()
    {
        Assert.Equal(500, QualityPreset.Preview.FrameInterval.TotalMilliseconds, 3);
        Assert.Equal(40, QualityPreset.High.FrameInterval.TotalMilliseconds, 3);
    }

    [Fact]
    public void Preview_is_cheaper_than_high_on_every_axis()
    {
        // The dashboard runs many Preview streams at once; it must stay the cheapest tier.
        Assert.True(QualityPreset.Preview.MaxWidth < QualityPreset.High.MaxWidth);
        Assert.True(QualityPreset.Preview.JpegQuality < QualityPreset.High.JpegQuality);
        Assert.True(QualityPreset.Preview.Fps < QualityPreset.High.Fps);
    }
}
