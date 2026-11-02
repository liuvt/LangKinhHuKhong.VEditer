namespace LangKinhHuKhong.Editer.Core;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

public static class FfmpegLocator
{
    public static FfmpegTools Find()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Runtime"),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "bin"),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "ffmpeg"),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "ffmpeg", "bin"),
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin"),
            Path.Combine(AppContext.BaseDirectory, "tools", "bin")
        };

        foreach (var directory in paths)
        {
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return new FfmpegTools(ffmpeg, ffprobe);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = path.Trim().Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
                var ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                    return new FfmpegTools(Path.GetFullPath(ffmpeg), Path.GetFullPath(ffprobe));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { }
        }
        throw new FileNotFoundException(
            "Không tìm thấy FFmpeg và FFprobe. Hãy đặt ffmpeg.exe và ffprobe.exe " +
            "vào thư mục Runtime (hoặc cạnh file .exe của ứng dụng), hoặc thêm vào PATH của Windows.");
    }
}
