using System.Diagnostics;
using System.Text;

namespace LangKinhHuKhong.Editer.Core;

public static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onStdout,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        bool highPerformance = false)
    {
        var effectiveWorkDir = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : AppContext.BaseDirectory;
        var info = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = effectiveWorkDir
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            info.Environment["TEMP"] = workingDirectory;
            info.Environment["TMP"] = workingDirectory;
        }
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException($"Không thể khởi động {executable}.");
        try
        {
            process.PriorityClass = highPerformance
                ? ProcessPriorityClass.AboveNormal
                : ProcessPriorityClass.BelowNormal;
        }
        catch { /* Một số môi trường hạn chế quyền đổi độ ưu tiên tiến trình */ }

        static void TryKillProcess(Process proc)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
        }

        using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));

        // Đọc song song để tránh kẹt pipe stdout/stderr khi FFmpeg xuất log dài.
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (onStdout is not null)
                {
                    try { onStdout(line); }
                    catch { /* Không để lỗi callback làm tắc pipe stdout của FFmpeg */ }
                }
                else if (stdout.Length < 256 * 1024)
                    stdout.AppendLine(line);
            }
        });
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                // Giới hạn log lỗi trong bộ nhớ.
                if (stderr.Length >= 64 * 1024)
                    stderr.Remove(0, Math.Min(stderr.Length, 8 * 1024));
                stderr.AppendLine(line);
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            // Đảm bảo FFmpeg đã thoát trước khi xóa thư mục làm việc.
            try { await process.WaitForExitAsync(); } catch { }
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} trả về mã {process.ExitCode}:\n" + stderr);

        return stdout.ToString();
    }
}
