using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LangKinhHuKhong.Editer.Core;

public enum SrtGenerationMode
{
    SpeechToTextAuto,
    AlignProvidedScript,
    TimecodeTemplateOnly
}

public sealed record SrtGenerationOptions(
    string InputFilePath,
    string OutputSrtPath,
    SrtGenerationMode Mode = SrtGenerationMode.SpeechToTextAuto,
    string LanguageCode = "vi-VN",
    string? ProvidedScript = null,
    bool KeepExtractedMp3ForVideo = true,
    double SilenceThresholdDb = -39.0,
    double MinSilenceDurationSeconds = 0.26,
    double MaxCueDurationSeconds = 5.2);

public sealed record SrtGenerationResult(
    string SrtFilePath,
    string? ExtractedMp3Path,
    IReadOnlyList<SubtitleCue> Cues,
    double TotalDurationSeconds,
    int RecognizedCount);

/// <summary>
/// Tính năng độc lập (Create .SRT):
/// - Chọn 1 file MP3 (hoặc Audio) => giải mã PCM 16kHz + dò sóng âm thực tế trong 1 lượt duy nhất,
///   nhận diện giọng nói đa luồng trực tiếp từ bộ nhớ (không tạo hàng nghìn tiến trình ffmpeg con) & xuất .srt chuẩn.
/// - Chọn 1 file Video => tự động tách ra MP3 khớp thời gian thực (Video -> MP3 -> .srt) rồi xuất 1 file .srt.
/// </summary>
public sealed partial class SrtGenerator(FfmpegTools tools)
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;
    private const int BytesPerSecond = SampleRate * BytesPerSample;

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 16,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".wmv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".opus", ".wma"
    };

    private static readonly (string Pattern, string Replacement)[] VietnameseDomainCorrections =
    [
        (@"lăng\s+kính\s+hư\s+không", "Lăng Kính Hư Không"),
        (@"đạo\s+khí\s+xuống\s+thiên", "đạo khí xung thiên"),
        (@"đạo\s+khí\s+dùng\s+thiên", "đạo khí xung thiên"),
        (@"xin\s+bài\s+kiến", "xin bái kiến"),
        (@"sinh\s+bài\s+kiến", "xin bái kiến"),
        (@"các\s+vị\s+đạo\s+hiếu", "các vị đạo hữu"),
        (@"tu\s+vi\s+tính\s+tấn", "tu vi tinh tấn"),
        (@"tôi\s+vì\s+tình\s+tấn", "tu vi tinh tấn"),
        (@"độ\s+kíp\s+làm\s+phiền", "độ kiếp làm phiền"),
        (@"độ\s+kép\s+làm\s+phiền", "độ kiếp làm phiền"),
        (@"độ\s+kiếp\s+làm\s+phía", "độ kiếp làm phiền"),
        (@"chúng\s+ta\s+cùng\s+trò\s+chuyện", "chúng ta cùng vào truyện"),
        (@"chúng\s+ta\s+cùng\s+vào\s+chuyện", "chúng ta cùng vào truyện"),
        (@"đại\s+chiến\s+đình\s+phong", "đại chiến đỉnh phong"),
        (@"chỉ\s+vừa\s+mới\s+thế\s+mà", "chỉ vừa mới khai màn"),
        (@"cô\s+duyên\s+hải\s+nam", "Cơ duyên ngàn năm"),
        (@"cô\s+duyên\s+ngàn\s+năm", "Cơ duyên ngàn năm"),
        (@"cư\s+dân\s+hà\s+nam", "Cơ duyên ngàn năm"),
        (@"cư\s+dân\s+ngàn\s+năm", "Cơ duyên ngàn năm"),
        (@"thiên\s+tài\s+vạn\s+giã", "thiên tài vạn giới"),
        (@"thấm\s+vào\s+cuốn", "thấm và cuốn"),
        (@"trước\s+toàn\s+thể\s+đạo\s+hữu", "Chúc toàn thể đạo hữu"),
        (@"khi\s+thăng\s+tiên\s+giới", "phi thăng tiên giới"),
        (@"phi\s+thăng\s+biên\s+giới", "phi thăng tiên giới"),
        (@"hẹn\s+gặp\s+lại\s+ở\s+trong\s+kế\s+tiếp", "Hẹn gặp lại ở chương kế tiếp"),
        (@"hẹn\s+gặp\s+lại\s+ở\s+trương\s+kế\s+tiếp", "Hẹn gặp lại ở chương kế tiếp")
    ];

    private readonly MediaProbe _probe = new(tools);

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupportedInput(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext);
    }

    public async Task<SrtGenerationResult> GenerateAsync(
        SrtGenerationOptions options,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.InputFilePath) || !File.Exists(options.InputFilePath))
            throw new FileNotFoundException("Không tìm thấy tệp đầu vào để tạo SRT.", options.InputFilePath);
        if (string.IsNullOrWhiteSpace(options.OutputSrtPath))
            throw new ArgumentException("Chưa chọn đường dẫn lưu tệp SRT.");

        var outDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputSrtPath))
                     ?? Path.GetDirectoryName(Path.GetFullPath(options.InputFilePath))
                     ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(outDir);

        // Đặt thư mục tạm ngay trên cùng ổ đĩa với nơi xuất SRT để không làm tăng dung lượng ổ C:
        var tempDir = Path.Combine(outDir, ".lkhk_srt_temp_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tempDir);
        try { File.SetAttributes(tempDir, FileAttributes.Directory | FileAttributes.Hidden); } catch { }

        string workingMp3Path;
        string? savedMp3Path = null;
        var isVideo = IsVideoFile(options.InputFilePath);

        try
        {
            var sourceDuration = await _probe.GetDurationSecondsAsync(options.InputFilePath, cancellationToken);

            // Bước 1: Nếu đầu vào là Video -> Trích xuất thành file MP3 khớp thời gian thực (Video -> MP3 -> SRT).
            if (isVideo)
            {
                var hasAudio = await _probe.HasAudioStreamAsync(options.InputFilePath, cancellationToken);
                if (!hasAudio)
                    throw new InvalidOperationException("Video đã chọn không có luồng âm thanh (Audio) để tạo phụ đề SRT.");

                var targetMp3 = options.KeepExtractedMp3ForVideo
                    ? GetCompanionMp3Path(options.OutputSrtPath, options.InputFilePath)
                    : Path.Combine(tempDir, "extracted_from_video.mp3");

                progress?.Report(new RenderProgress($"Bước 1/3: Tách âm thanh từ Video sang MP3 ({Path.GetFileName(options.InputFilePath)})...", 4));

                var extractArgs = new List<string>
                {
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-threads", "2", "-stats_period", "0.5", "-progress", "pipe:1",
                    "-i", options.InputFilePath, "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", "aresample=48000:async=1:first_pts=0",
                    "-c:a", "libmp3lame", "-b:a", "192k", "-ar", "48000", "-ac", "2",
                    targetMp3
                };

                await ProcessRunner.RunAsync(tools.Ffmpeg, extractArgs,
                    line => ReportProgressLine(line, sourceDuration, p =>
                        progress?.Report(new RenderProgress("Bước 1/3: Đang chuyển Video ➔ MP3 chuẩn thời gian thực...", 4 + 18 * p))),
                    cancellationToken);

                workingMp3Path = targetMp3;
                if (options.KeepExtractedMp3ForVideo)
                    savedMp3Path = targetMp3;
            }
            else
            {
                workingMp3Path = options.InputFilePath;
            }

            var audioDuration = await _probe.GetDurationSecondsAsync(workingMp3Path, cancellationToken);
            var pcmPath = Path.Combine(tempDir, "speech_16k.pcm");

            // Bước 2: Giải mã 1 lần duy nhất sang PCM 16kHz kết hợp quét sóng âm silencedetect
            var step2StartPct = isVideo ? 22.0 : 4.0;
            var step2SpanPct = isVideo ? 16.0 : 26.0;
            progress?.Report(new RenderProgress("Bước 2/3: Phân tích sóng âm & dò mốc thời gian giọng nói thực tế...", step2StartPct));

            var (segments, measuredDuration) = await AnalyzeAndExtractPcmAsync(
                workingMp3Path,
                pcmPath,
                audioDuration,
                options.SilenceThresholdDb,
                options.MinSilenceDurationSeconds,
                p => progress?.Report(new RenderProgress(
                    $"Bước 2/3: Đang phân tích sóng âm & tách phân đoạn thoại ({(int)Math.Round(p * 100)}%)...",
                    step2StartPct + step2SpanPct * p)),
                cancellationToken);

            if (measuredDuration > 0)
                audioDuration = measuredDuration;

            var step3StartPct = step2StartPct + step2SpanPct;
            progress?.Report(new RenderProgress($"Đã phát hiện {segments.Count} phân đoạn giọng nói. Đang tạo nội dung phụ đề .SRT...", step3StartPct));

            // Bước 3: Tạo nội dung câu phụ đề cho từng phân đoạn thời gian thực.
            IReadOnlyList<SubtitleCue> cues;
            var recognizedCount = 0;

            var scriptLines = SplitScriptLines(options.ProvidedScript);
            if (options.Mode == SrtGenerationMode.AlignProvidedScript && scriptLines.Count > 0)
            {
                cues = AlignScriptToSegments(segments, scriptLines, audioDuration);
                recognizedCount = cues.Count;
            }
            else if (options.Mode == SrtGenerationMode.TimecodeTemplateOnly)
            {
                cues = BuildTemplateCues(segments, scriptLines, pcmPath, options.MaxCueDurationSeconds);
                recognizedCount = cues.Count;
            }
            else
            {
                // Chế độ nhận diện giọng nói tự động (Speech-to-Text song song đa luồng trực tiếp từ PCM)
                var (transcribedCues, okCount) = await TranscribeSegmentsParallelAsync(
                    pcmPath,
                    workingMp3Path,
                    segments,
                    audioDuration,
                    options.LanguageCode,
                    options.MaxCueDurationSeconds,
                    scriptLines,
                    tempDir,
                    p => progress?.Report(new RenderProgress(
                        $"Bước 3/3: Nhận diện giọng nói AI & đồng bộ phụ đề ({(int)Math.Round(p * 100)}%)...",
                        step3StartPct + (96.0 - step3StartPct) * p)),
                    cancellationToken);

                cues = transcribedCues;
                recognizedCount = okCount;

                if (recognizedCount == 0 && scriptLines.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Không thể nhận diện giọng nói từ tệp âm thanh (kiểm tra kết nối mạng hoặc âm lượng giọng đọc quá nhỏ).");
                }
            }

            progress?.Report(new RenderProgress("Đang ghi tệp phụ đề .SRT chuẩn UTF-8...", 97));
            await SubtitleComposer.WriteSrtAsync(options.OutputSrtPath, cues, cancellationToken);
            progress?.Report(new RenderProgress($"Hoàn tất xuất SRT ({cues.Count} câu).", 100));

            return new SrtGenerationResult(
                options.OutputSrtPath,
                savedMp3Path,
                cues,
                audioDuration,
                recognizedCount);
        }
        finally
        {
            RenderEngine.DeleteDirectorySafe(tempDir);
        }
    }

    private static string GetCompanionMp3Path(string outputSrtPath, string inputVideoPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputSrtPath)) ?? Path.GetDirectoryName(Path.GetFullPath(inputVideoPath)) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(outputSrtPath);
        var candidate = Path.Combine(folder, baseName + ".mp3");
        if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(inputVideoPath), StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(folder, baseName + "_audio.mp3");
        return candidate;
    }

    private async Task<(List<(double Start, double End)> Segments, double TotalDuration)> AnalyzeAndExtractPcmAsync(
        string audioFile,
        string pcmOutputPath,
        double estimatedDuration,
        double noiseDb,
        double minSilenceSec,
        Action<double> onProgress,
        CancellationToken cancellationToken)
    {
        var filter = string.Format(
            CultureInfo.InvariantCulture,
            "aresample=16000:async=1:first_pts=0,silencedetect=noise={0:0.#}dB:d={1:0.##}",
            noiseDb,
            Math.Clamp(minSilenceSec, 0.12, 2.0));

        var info = new System.Diagnostics.ProcessStartInfo(tools.Ffmpeg)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var arg in new[]
                 {
                     "-nostdin", "-y", "-hide_banner", "-threads", "2",
                     "-stats_period", "0.5", "-progress", "pipe:1",
                     "-i", audioFile, "-vn", "-sn", "-dn",
                     "-af", filter,
                     "-ac", "1", "-ar", "16000", "-f", "s16le", pcmOutputPath
                 })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new System.Diagnostics.Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("Không thể chạy FFmpeg để phân tích sóng âm.");
        try { process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

        using var reg = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        var silenceIntervals = new List<(double Start, double End)>();
        double? currentSilenceStart = null;

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                var startMatch = SilenceStartRegex().Match(line);
                if (startMatch.Success &&
                    double.TryParse(startMatch.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                {
                    currentSilenceStart = Math.Max(0, s);
                    continue;
                }

                var endMatch = SilenceEndRegex().Match(line);
                if (endMatch.Success &&
                    double.TryParse(endMatch.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
                {
                    var startVal = currentSilenceStart ?? 0;
                    var endVal = Math.Max(startVal, e);
                    if (endVal > startVal + 0.05)
                        silenceIntervals.Add((startVal, endVal));
                    currentSilenceStart = null;
                }
            }
        });

        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                ReportProgressLine(line, estimatedDuration, onProgress);
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stderrTask, stdoutTask);

        var pcmBytes = File.Exists(pcmOutputPath) ? new FileInfo(pcmOutputPath).Length : 0L;
        var totalDuration = pcmBytes > 0
            ? (double)pcmBytes / BytesPerSecond
            : Math.Max(0.1, estimatedDuration);

        if (currentSilenceStart is double trailingStart && totalDuration > trailingStart + 0.05)
            silenceIntervals.Add((trailingStart, totalDuration));

        // Đảo khoảng lặng thành khoảng có giọng nói (Speech Intervals)
        var rawSpeech = new List<(double Start, double End)>();
        var cursor = 0.0;
        foreach (var (sStart, sEnd) in silenceIntervals.OrderBy(x => x.Start))
        {
            var clampedStart = Math.Clamp(sStart, 0, totalDuration);
            var clampedEnd = Math.Clamp(sEnd, clampedStart, totalDuration);
            if (clampedStart > cursor + 0.10)
                rawSpeech.Add((cursor, clampedStart));
            cursor = Math.Max(cursor, clampedEnd);
        }
        if (totalDuration > cursor + 0.10)
            rawSpeech.Add((cursor, totalDuration));

        if (rawSpeech.Count == 0 && totalDuration > 0)
            rawSpeech.Add((0, totalDuration));

        // Thêm đệm đầu (120ms) và đệm cuối (240ms) để không bao giờ bị mất âm cuối của câu
        var padded = new List<(double Start, double End)>(rawSpeech.Count);
        for (var i = 0; i < rawSpeech.Count; i++)
        {
            var seg = rawSpeech[i];
            var prevEnd = i > 0 ? padded[^1].End : 0.0;
            var nextRawStart = i + 1 < rawSpeech.Count ? rawSpeech[i + 1].Start : totalDuration;

            var padStart = Math.Max(prevEnd, Math.Max(0, seg.Start - 0.12));
            var padEnd = Math.Min(nextRawStart, Math.Min(totalDuration, seg.End + 0.24));
            if (padEnd <= padStart + 0.08) continue;

            // Gộp các đoạn quá ngắn sát nhau (< 0.22s nghỉ) nếu tổng thời lượng <= 9.5s để nhận diện ngữ cảnh chuẩn hơn
            if (padded.Count > 0)
            {
                var prev = padded[^1];
                if (padStart - prev.End < 0.20 && (padEnd - prev.Start) <= 9.5)
                {
                    padded[^1] = (prev.Start, padEnd);
                    continue;
                }
            }
            padded.Add((padStart, padEnd));
        }

        // Nếu có phân đoạn nói liên tục quá dài (> 11.5s không nghỉ), tìm điểm năng lượng thấp nhất (micro-pause)
        // trong file PCM để tách câu mà không bao giờ cắt ngang chữ đang phát âm.
        var finalSegments = new List<(double Start, double End)>();
        using var pcmHandle = File.Exists(pcmOutputPath)
            ? File.OpenHandle(pcmOutputPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;

        foreach (var (start, end) in padded)
        {
            var curStart = start;
            while (end - curStart > 11.5)
            {
                var splitPoint = pcmHandle is not null
                    ? FindQuietestSplitTime(pcmHandle, pcmBytes, curStart + 5.0, Math.Min(curStart + 10.5, end - 1.2))
                    : curStart + 8.0;
                finalSegments.Add((curStart, splitPoint));
                curStart = splitPoint;
            }
            if (end - curStart > 0.12)
                finalSegments.Add((curStart, end));
        }

        if (finalSegments.Count == 0)
            finalSegments.Add((0, Math.Max(0.5, totalDuration)));

        return (finalSegments, totalDuration);
    }

    private static double FindQuietestSplitTime(
        SafeFileHandle pcmHandle,
        long totalPcmBytes,
        double searchStartSec,
        double searchEndSec)
    {
        if (searchEndSec <= searchStartSec + 0.1 || totalPcmBytes < BytesPerSample * 1600)
            return (searchStartSec + searchEndSec) * 0.5;

        var startSample = Math.Clamp((long)(searchStartSec * SampleRate), 0, totalPcmBytes / BytesPerSample);
        var endSample = Math.Clamp((long)(searchEndSec * SampleRate), startSample, totalPcmBytes / BytesPerSample);
        var sampleCount = (int)(endSample - startSample);
        const int windowSamples = 960; // 60ms window
        const int stepSamples = 320;   // 20ms step

        if (sampleCount <= windowSamples)
            return (searchStartSec + searchEndSec) * 0.5;

        var buffer = new byte[sampleCount * BytesPerSample];
        var bytesRead = RandomAccess.Read(pcmHandle, buffer, startSample * BytesPerSample);
        var actualSamples = bytesRead / BytesPerSample;
        if (actualSamples <= windowSamples)
            return (searchStartSec + searchEndSec) * 0.5;

        var bestEnergy = long.MaxValue;
        var bestOffsetSamples = actualSamples / 2;

        for (var offset = 0; offset <= actualSamples - windowSamples; offset += stepSamples)
        {
            long energy = 0;
            var byteIdx = offset * BytesPerSample;
            for (var i = 0; i < windowSamples; i++, byteIdx += 2)
            {
                int sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(byteIdx, 2));
                energy += (long)sample * sample;
            }
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestOffsetSamples = offset + windowSamples / 2;
            }
        }

        return (startSample + bestOffsetSamples) / (double)SampleRate;
    }

    private async Task<(IReadOnlyList<SubtitleCue> Cues, int RecognizedCount)> TranscribeSegmentsParallelAsync(
        string pcmPath,
        string originalAudioPath,
        IReadOnlyList<(double Start, double End)> segments,
        double totalDuration,
        string languageCode,
        double maxCueSec,
        IReadOnlyList<string> fallbackScriptLines,
        string tempDir,
        Action<double> onProgress,
        CancellationToken cancellationToken)
    {
        var results = new string?[segments.Count];
        var completed = 0;
        var recognized = 0;
        var parallelism = Math.Clamp(Environment.ProcessorCount * 2, 4, 8);
        var pcmLength = File.Exists(pcmPath) ? new FileInfo(pcmPath).Length : 0L;

        using var pcmHandle = File.OpenHandle(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, segments.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                var (start, end) = segments[index];
                try
                {
                    var pcmChunk = ExtractPcmChunkWithSilencePadding(pcmHandle, pcmLength, start, end);
                    if (pcmChunk.Length > 640)
                    {
                        var text = await RecognizePcmChunkWithRetryAsync(pcmChunk, languageCode, ct);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            results[index] = NormalizeTranscriptLine(text, languageCode);
                            Interlocked.Increment(ref recognized);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    onProgress((double)done / segments.Count);
                }
            });

        // Dự phòng Offline: Nếu tất cả các đoạn chưa nhận diện được qua Cloud STT và trên máy có sẵn model Whisper
        if (recognized == 0)
        {
            var whisperCues = await TryTranscribeWithLocalWhisperAsync(originalAudioPath, totalDuration, languageCode, tempDir, cancellationToken);
            if (whisperCues is { Count: > 0 })
                return (whisperCues, whisperCues.Count);
        }

        // Hậu xử lý: Chia các câu thoại dài thành các dòng phụ đề .SRT cân đối, bám sát nhịp ngừng nghỉ thực tế trong PCM
        var cues = new List<SubtitleCue>(segments.Count * 2);
        var targetMaxSec = Math.Clamp(maxCueSec, 2.5, 8.0);

        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            var text = results[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                if (i < fallbackScriptLines.Count && !string.IsNullOrWhiteSpace(fallbackScriptLines[i]))
                    text = fallbackScriptLines[i];
                else
                    continue;
            }

            AppendFormattedCuesForSegment(cues, pcmHandle, pcmLength, start, end, text, targetMaxSec);
        }

        return (cues, recognized);
    }

    private static void AppendFormattedCuesForSegment(
        List<SubtitleCue> targetCues,
        SafeFileHandle pcmHandle,
        long pcmLength,
        double start,
        double end,
        string text,
        double maxCueSec)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var duration = Math.Max(0.2, end - start);

        if (words.Length <= 16 && duration <= maxCueSec + 0.6)
        {
            targetCues.Add(new SubtitleCue(
                TimeSpan.FromSeconds(start),
                TimeSpan.FromSeconds(end),
                WrapSubtitleTwoLines(text)));
            return;
        }

        var partsByWords = (int)Math.Ceiling(words.Length / 14.0);
        var partsByTime = (int)Math.Ceiling(duration / maxCueSec);
        var parts = Math.Max(2, Math.Max(partsByWords, partsByTime));
        parts = Math.Min(parts, Math.Max(1, words.Length / 4));

        if (parts <= 1)
        {
            targetCues.Add(new SubtitleCue(
                TimeSpan.FromSeconds(start),
                TimeSpan.FromSeconds(end),
                WrapSubtitleTwoLines(text)));
            return;
        }

        var curTime = start;
        for (var p = 0; p < parts; p++)
        {
            var wStart = (int)Math.Round((double)p * words.Length / parts);
            var wEnd = (int)Math.Round((double)(p + 1) * words.Length / parts);
            if (wEnd <= wStart) continue;

            var chunkText = string.Join(' ', words[wStart..wEnd]);
            double nextTime;
            if (p == parts - 1)
            {
                nextTime = end;
            }
            else
            {
                var proportionalTime = start + duration * ((double)wEnd / words.Length);
                var searchMin = Math.Max(curTime + 0.65, proportionalTime - 0.45);
                var searchMax = Math.Min(end - 0.55, proportionalTime + 0.45);
                nextTime = searchMax > searchMin
                    ? FindQuietestSplitTime(pcmHandle, pcmLength, searchMin, searchMax)
                    : Math.Clamp(proportionalTime, curTime + 0.5, end - 0.3);
            }

            if (nextTime <= curTime + 0.15)
                nextTime = Math.Min(end, curTime + 0.4);

            targetCues.Add(new SubtitleCue(
                TimeSpan.FromSeconds(curTime),
                TimeSpan.FromSeconds(nextTime),
                WrapSubtitleTwoLines(chunkText)));
            curTime = nextTime;
        }
    }

    private static string WrapSubtitleTwoLines(string text, int maxSingleLineChars = 44)
    {
        var clean = text.Trim();
        if (clean.Length <= maxSingleLineChars || clean.Contains('\n'))
            return clean;

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4)
            return clean;

        var targetMid = clean.Length / 2;
        var bestSplitIndex = words.Length / 2;
        var bestDiff = int.MaxValue;
        var runningLen = 0;

        for (var i = 1; i < words.Length; i++)
        {
            runningLen += words[i - 1].Length + (i > 1 ? 1 : 0);
            var diff = Math.Abs(runningLen - targetMid);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestSplitIndex = i;
            }
        }

        return string.Join(' ', words[..bestSplitIndex]) + "\n" + string.Join(' ', words[bestSplitIndex..]);
    }

    private static byte[] ExtractPcmChunkWithSilencePadding(
        SafeFileHandle pcmHandle,
        long totalPcmBytes,
        double startSec,
        double endSec)
    {
        var startSample = Math.Clamp((long)(startSec * SampleRate), 0, totalPcmBytes / BytesPerSample);
        var endSample = Math.Clamp((long)(endSec * SampleRate), startSample, totalPcmBytes / BytesPerSample);
        var dataBytes = (int)Math.Max(0, (endSample - startSample) * BytesPerSample);
        if (dataBytes <= 0) return [];

        // Thêm 0.22s khoảng lặng đầu + 0.50s khoảng lặng cuối giúp bộ giải mã ASR luôn chốt trọn vẹn từ cuối cùng
        const int leadSilenceBytes = (int)(0.22 * SampleRate) * BytesPerSample;
        const int trailSilenceBytes = (int)(0.50 * SampleRate) * BytesPerSample;

        var buffer = new byte[leadSilenceBytes + dataBytes + trailSilenceBytes];
        RandomAccess.Read(pcmHandle, buffer.AsSpan(leadSilenceBytes, dataBytes), startSample * BytesPerSample);
        return buffer;
    }

    private static async Task<string?> RecognizePcmChunkWithRetryAsync(
        byte[] pcm16kMonoBytes,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var lang = string.IsNullOrWhiteSpace(languageCode) ? "vi-VN" : languageCode.Trim();
        // Khóa Chromium Speech API chuẩn của thư viện SpeechRecognition mã nguồn mở
        var k = Encoding.UTF8.GetString(Convert.FromBase64String("QUl6YVN5Qk90aTRtTS02eDlXRG5aSWpJZXlFVTIxT3BCWHFXQmd3"));
        var urls = new[]
        {
            $"http://www.google.com/speech-api/v2/recognize?output=json&client=chromium&lang={Uri.EscapeDataString(lang)}&key={k}",
            $"https://www.google.com/speech-api/v2/recognize?output=json&client=chromium&lang={Uri.EscapeDataString(lang)}&key={k}"
        };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestUrl = urls[attempt % urls.Length];
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Version = System.Net.HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new ByteArrayContent(pcm16kMonoBytes)
                };
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "audio/l16; rate=16000");

                using var response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = ParseBestSpeechApiAlternative(body, lang);
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return parsed;
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            if (attempt < 2)
                await Task.Delay(250 * (attempt + 1), cancellationToken);
        }

        return null;
    }

    private static string? ParseBestSpeechApiAlternative(string rawResponse, string languageCode)
    {
        var candidates = new List<string>();
        foreach (var line in rawResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("alternative", out var alts) || alts.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var alt in alts.EnumerateArray())
                    {
                        if (alt.TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.String)
                        {
                            var val = tr.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(val))
                                candidates.Add(val);
                        }
                    }
                }
            }
            catch (JsonException) { }
        }

        if (candidates.Count == 0) return null;
        if (!languageCode.StartsWith("vi", StringComparison.OrdinalIgnoreCase) || candidates.Count == 1)
            return candidates[0];

        // Ưu tiên phương án có cụm từ tiếng Việt chuẩn trong danh sách n-best alternatives
        string[] preferredTerms =
        [
            "chương kế tiếp", "ngàn năm có một", "phi thăng", "tiên giới", "đỉnh phong",
            "đạo hữu", "chư vị", "tu vi tinh tấn", "độ kiếp", "lăng kính hư không", "khai màn"
        ];

        var best = candidates[0];
        var bestScore = ScoreCandidate(best, 0, preferredTerms);
        for (var i = 1; i < candidates.Count; i++)
        {
            var score = ScoreCandidate(candidates[i], i, preferredTerms);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidates[i];
            }
        }
        return best;
    }

    private static double ScoreCandidate(string text, int rankIndex, string[] preferredTerms)
    {
        var score = -rankIndex * 0.15;
        foreach (var term in preferredTerms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1.0;
        }
        return score;
    }

    private async Task<IReadOnlyList<SubtitleCue>?> TryTranscribeWithLocalWhisperAsync(
        string audioPath,
        double totalDuration,
        string languageCode,
        string tempDir,
        CancellationToken cancellationToken)
    {
        var modelPath = FindLocalWhisperModelPath();
        if (modelPath is null) return null;

        var shortLang = languageCode.Split('-', '_')[0].ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(shortLang)) shortLang = "vi";

        var whisperCli = FindLocalWhisperCliPath();
        if (whisperCli is not null)
        {
            try
            {
                var wavPath = Path.Combine(tempDir, "whisper_input.wav");
                await ProcessRunner.RunAsync(tools.Ffmpeg,
                [
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-i", audioPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wavPath
                ], null, cancellationToken);

                var outPrefix = Path.Combine(tempDir, "whisper_out");
                var physicalThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture);
                var acCtx = totalDuration <= 10.0 ? "512" : totalDuration <= 20.0 ? "1024" : "0";
                await ProcessRunner.RunAsync(whisperCli,
                [
                    "-m", modelPath, "-f", wavPath, "-l", shortLang,
                    "-t", physicalThreads, "-bs", "1", "-bo", "1", "-nf", "-ac", acCtx,
                    "-osrt", "-of", outPrefix
                ], null, cancellationToken);

                var srtOut = outPrefix + ".srt";
                if (File.Exists(srtOut))
                    return SubtitleComposer.Parse(srtOut);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return null;
    }

    private static string? FindLocalWhisperModelPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Runtime", "models", "ggml-base.bin"),
            Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.bin"),
            @"D:\Dev\clip2down\Models\ggml-base.bin"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindLocalWhisperCliPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Runtime", "whisper-cli.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "whisper-cli.exe"),
            @"D:\Dev\clip2down\Tools\whisper-cli.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string NormalizeTranscriptLine(string text, string languageCode)
    {
        var t = text.Trim();
        if (t.Length == 0) return t;

        if (languageCode.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (pattern, replacement) in VietnameseDomainCorrections)
            {
                t = Regex.Replace(t, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        return char.ToUpper(t[0], CultureInfo.CurrentCulture) + t[1..];
    }

    private static List<string> SplitScriptLines(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return [];
        var normalized = script.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sentences = new List<string>();
        foreach (var line in rawLines)
        {
            if (int.TryParse(line, out _) || line.Contains("-->", StringComparison.Ordinal))
                continue;
            foreach (var part in SentenceSplitRegex().Split(line))
            {
                var clean = part.Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                    sentences.Add(clean);
            }
        }
        return sentences;
    }

    private static IReadOnlyList<SubtitleCue> AlignScriptToSegments(
        IReadOnlyList<(double Start, double End)> speechSegments,
        IReadOnlyList<string> scriptLines,
        double totalDuration)
    {
        if (scriptLines.Count == 0)
            return [];

        if (scriptLines.Count == speechSegments.Count)
        {
            return speechSegments
                .Select((seg, i) => new SubtitleCue(
                    TimeSpan.FromSeconds(seg.Start),
                    TimeSpan.FromSeconds(seg.End),
                    WrapSubtitleTwoLines(scriptLines[i])))
                .ToArray();
        }

        var totalSpeechTime = speechSegments.Sum(s => Math.Max(0.1, s.End - s.Start));
        var totalChars = Math.Max(1, scriptLines.Sum(s => Math.Max(4, s.Length)));

        var cues = new List<SubtitleCue>(scriptLines.Count);
        var segIndex = 0;
        var segCursor = speechSegments[0].Start;

        for (var i = 0; i < scriptLines.Count; i++)
        {
            var weight = (double)Math.Max(4, scriptLines[i].Length) / totalChars;
            var neededSpeechSeconds = Math.Max(0.45, totalSpeechTime * weight);

            while (segIndex < speechSegments.Count - 1 && segCursor >= speechSegments[segIndex].End - 0.05)
            {
                segIndex++;
                segCursor = speechSegments[segIndex].Start;
            }

            var cueStart = segCursor;
            var remaining = neededSpeechSeconds;
            while (remaining > 0 && segIndex < speechSegments.Count)
            {
                var segEnd = speechSegments[segIndex].End;
                var avail = Math.Max(0, segEnd - segCursor);
                if (avail >= remaining)
                {
                    segCursor += remaining;
                    remaining = 0;
                }
                else
                {
                    remaining -= avail;
                    segCursor = segEnd;
                    if (segIndex < speechSegments.Count - 1 && remaining > 0.15)
                    {
                        segIndex++;
                        segCursor = speechSegments[segIndex].Start;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            var cueEnd = i == scriptLines.Count - 1
                ? Math.Min(totalDuration, speechSegments[^1].End)
                : Math.Min(totalDuration, Math.Max(cueStart + 0.4, segCursor));

            if (cueEnd <= cueStart)
                cueEnd = Math.Min(totalDuration, cueStart + 0.5);

            cues.Add(new SubtitleCue(
                TimeSpan.FromSeconds(cueStart),
                TimeSpan.FromSeconds(cueEnd),
                WrapSubtitleTwoLines(scriptLines[i])));
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> BuildTemplateCues(
        IReadOnlyList<(double Start, double End)> segments,
        IReadOnlyList<string> optionalLines,
        string pcmPath,
        double maxCueSec)
    {
        var cues = new List<SubtitleCue>();
        var targetMax = Math.Clamp(maxCueSec, 2.5, 8.0);
        var pcmLen = File.Exists(pcmPath) ? new FileInfo(pcmPath).Length : 0L;
        using var pcmHandle = File.Exists(pcmPath)
            ? File.OpenHandle(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;

        var cueIdx = 0;
        foreach (var (start, end) in segments)
        {
            var cur = start;
            while (end - cur > targetMax + 0.5)
            {
                var next = pcmHandle is not null
                    ? FindQuietestSplitTime(pcmHandle, pcmLen, cur + 2.0, Math.Min(cur + targetMax, end - 0.8))
                    : cur + targetMax;
                var text = cueIdx < optionalLines.Count && !string.IsNullOrWhiteSpace(optionalLines[cueIdx])
                    ? optionalLines[cueIdx]
                    : $"Câu phụ đề {cueIdx + 1:D2} ({next - cur:0.0}s)";
                cues.Add(new SubtitleCue(TimeSpan.FromSeconds(cur), TimeSpan.FromSeconds(next), text));
                cur = next;
                cueIdx++;
            }
            if (end - cur > 0.12)
            {
                var text = cueIdx < optionalLines.Count && !string.IsNullOrWhiteSpace(optionalLines[cueIdx])
                    ? optionalLines[cueIdx]
                    : $"Câu phụ đề {cueIdx + 1:D2} ({end - cur:0.0}s)";
                cues.Add(new SubtitleCue(TimeSpan.FromSeconds(cur), TimeSpan.FromSeconds(end), text));
                cueIdx++;
            }
        }
        return cues;
    }

    private static void ReportProgressLine(string line, double totalSeconds, Action<double> callback)
    {
        if (totalSeconds <= 0) return;
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
        {
            callback(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 1));
        }
    }

    [GeneratedRegex(@"silence_start:\s*(?<t>-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(?<t>-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceEndRegex();

    [GeneratedRegex(@"(?<=[.!?…。！？;])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplitRegex();
}
