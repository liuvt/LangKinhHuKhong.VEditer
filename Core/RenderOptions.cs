using System.Globalization;

namespace LangKinhHuKhong.Editer.Core;

public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum VideoEncoder
{
    X264,
    Nvenc
}

/// <summary>Playback speed belongs to each video; 0.4x means 2.5x longer.</summary>
public sealed record VideoClipOption(string FilePath, double PlaybackSpeed = 1.0);

public static class PlaybackSpeeds
{
    public static readonly double[] Allowed = [0.4, 0.5, 0.75, 1.0, 1.5, 2.0];

    public static bool IsSupported(double speed) =>
        double.IsFinite(speed) && Allowed.Any(x => Math.Abs(x - speed) < 0.000001);

    public static string Format(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    // FFmpeg's atempo is safely chained for the 0.4x preset.
    public static string AudioTempo(double speed) =>
        Math.Abs(speed - 0.4) < 0.000001 ? "atempo=0.5,atempo=0.8" :
        "atempo=" + speed.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed record OverlayOption(string ImagePath, Corner Corner, int WidthPercent = 20, int HeightPercent = 20, int OffsetXPercent = 1, int OffsetYPercent = 1);

public sealed record RenderOptions(
    IReadOnlyList<VideoClipOption> Videos,
    IReadOnlyList<string> Mp3Files,
    IReadOnlyList<OverlayOption> Overlays,
    string OutputDirectory,
    int Width,
    int Height,
    int FrameRate,
    bool MuteVideoAudio,
    VideoEncoder Encoder,
    SubtitleOptions? Subtitles = null)
{
    public string ResolutionName => Height switch
    {
        720 => "720p",
        1080 => "1080p",
        2160 => "4K",
        _ => $"{Width}x{Height}"
    };
}

public sealed record RenderProgress(string Stage, double? Percent = null, int? Current = null, int? Total = null);
