namespace Argus.Server.Capture;

public enum QualityLevel
{
    /// <summary>Dashboard tiles. Deliberately tiny - many of these run at once.</summary>
    Preview = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Resolution / frame rate / compression for one stream.</summary>
public sealed record QualityPreset(QualityLevel Level, int MaxWidth, int MaxHeight, int JpegQuality, int Fps)
{
    public static readonly QualityPreset Preview = new(QualityLevel.Preview, 400, 300, 50, 2);
    public static readonly QualityPreset Low = new(QualityLevel.Low, 854, 480, 45, 6);
    public static readonly QualityPreset Medium = new(QualityLevel.Medium, 1280, 720, 65, 15);
    public static readonly QualityPreset High = new(QualityLevel.High, 1920, 1080, 80, 25);

    public static QualityPreset For(QualityLevel level) => level switch
    {
        QualityLevel.Preview => Preview,
        QualityLevel.Low => Low,
        QualityLevel.Medium => Medium,
        QualityLevel.High => High,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown quality level"),
    };

    public static IReadOnlyList<QualityPreset> All { get; } = [Preview, Low, Medium, High];

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / Fps);

    /// <summary>
    /// Scales <paramref name="width"/> x <paramref name="height"/> down to fit the preset box,
    /// preserving aspect ratio. Never scales up - a 300px window stays 300px on High.
    /// </summary>
    public (int Width, int Height) Fit(int width, int height)
    {
        if (width <= 0 || height <= 0) return (1, 1);

        double scale = Math.Min((double)MaxWidth / width, (double)MaxHeight / height);
        if (scale >= 1.0) return (width, height);

        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }
}
