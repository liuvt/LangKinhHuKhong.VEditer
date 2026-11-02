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
    private static void Main()
    {
        ConfigureRuntimeDirectory();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ConfigureRuntimeDirectory()
    {
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
