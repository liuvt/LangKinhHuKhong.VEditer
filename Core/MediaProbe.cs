using System.Globalization;

namespace LangKinhHuKhong.Editer.Core;

public sealed class MediaProbe(FfmpegTools tools)
{
    public async Task<double> GetDurationSecondsAsync(string file, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "error", "-show_entries", "format=duration:stream=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", file
        };
        var output = await ProcessRunner.RunAsync(tools.Ffprobe, args, null, cancellationToken);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && double.IsFinite(seconds) && seconds > 0)
                return seconds;
        }
        throw new InvalidOperationException("Không đọc được thời lượng: " + file);
    }

    /// <summary>Some source videos have no audio; use silence instead of failing the render.</summary>
    public async Task<bool> HasAudioStreamAsync(string file, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=index",
            "-of", "csv=p=0", file
        };
        var output = await ProcessRunner.RunAsync(tools.Ffprobe, args, null, cancellationToken);
        return !string.IsNullOrWhiteSpace(output);
    }

    public async Task<string> GetVideoCodecNameAsync(string file, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name",
            "-of", "default=noprint_wrappers=1:nokey=1", file
        };
        var output = await ProcessRunner.RunAsync(tools.Ffprobe, args, null, cancellationToken);
        return output.Trim().ToLowerInvariant();
    }

    public async Task<(string Codec, int Width, int Height)> GetVideoStreamInfoAsync(string file, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height",
            "-of", "csv=p=0", file
        };
        var output = await ProcessRunner.RunAsync(tools.Ffprobe, args, null, cancellationToken);
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
            w > 0 && h > 0)
        {
            return (parts[0].ToLowerInvariant(), w, h);
        }
        return (parts.FirstOrDefault()?.ToLowerInvariant() ?? "", 0, 0);
    }

}
