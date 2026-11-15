using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using LangKinhHuKhong.Editer.UI;

namespace LangKinhHuKhong.Editer;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [STAThread]
    private static int Main(string[] args)
    {
        ConfigureRuntimeDirectory();
        if (args.Length >= 2 && string.Equals(args[0], "--generate-srt", StringComparison.OrdinalIgnoreCase))
        {
            var input = Path.GetFullPath(args[1]);
            var output = args.Length >= 3 ? Path.GetFullPath(args[2]) : Path.ChangeExtension(input, ".srt");
            var lang = args.Length >= 4 ? args[3] : "vi-VN";
            var tools = Core.FfmpegLocator.Find();
            var gen = new Core.SrtGenerator(tools);
            var res = gen.GenerateAsync(new Core.SrtGenerationOptions(input, output, Core.SrtGenerationMode.SpeechToTextAuto, lang), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            return res.Cues.Count > 0 ? 0 : 1;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static void ConfigureRuntimeDirectory()
    {
        try
        {
            var legacyTemp = Path.Combine(Path.GetTempPath(), "LangKinhHuKhong.Editer");
            if (Directory.Exists(legacyTemp))
                Directory.Delete(legacyTemp, recursive: true);
        }
        catch { }

        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "Runtime");
        try
        {
            Directory.CreateDirectory(runtimeDir);
            SetDllDirectory(runtimeDir);

            var runtimeBin = Path.Combine(runtimeDir, "bin");
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var additions = Directory.Exists(runtimeBin)
                ? $"{runtimeDir}{Path.PathSeparator}{runtimeBin}"
                : runtimeDir;
            Environment.SetEnvironmentVariable("PATH", $"{additions}{Path.PathSeparator}{currentPath}");
        }
        catch
        {
            // Không chặn khởi động nếu thư mục cài đặt ở chế độ chỉ đọc.
        }

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            if (string.IsNullOrWhiteSpace(assemblyName.Name)) return null;
            var candidate = Path.Combine(runtimeDir, assemblyName.Name + ".dll");
            return File.Exists(candidate)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
                : null;
        };
    }
}
