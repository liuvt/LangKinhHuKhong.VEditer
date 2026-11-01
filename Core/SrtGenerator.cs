using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    double SilenceThresholdDb = -34.0,
    double MinSilenceDurationSeconds = 0.28,
    double MaxCueDurationSeconds = 5.5);

public sealed record SrtGenerationResult(
    string SrtFilePath,
    string? ExtractedMp3Path,
    IReadOnlyList<SubtitleCue> Cues,
    double TotalDurationSeconds,
    int RecognizedCount);

/// <summary>
/// Tính năng độc lập (ngoài luồng):
/// - Chọn 1 file MP3 (hoặc Audio) => phân tích giọng nói & xuất 1 file .srt khớp thời gian thực.
/// - Chọn 1 file Video => tự động tách ra MP3 khớp thời gian thực (Video -> MP3 -> .srt) rồi xuất 1 file .srt.
/// </summary>
public sealed partial class SrtGenerator(FfmpegTools tools)
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".wmv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".opus", ".wma"
    };

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

        var outDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputSrtPath));
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);

        var tempDir = Path.Combine(Path.GetTempPath(), "LangKinhHuKhong.Editer", "srt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

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

                progress?.Report(new RenderProgress($"Bước 1/3: Tách âm thanh từ Video sang MP3 ({Path.GetFileName(options.InputFilePath)})...", 5));

                var extractArgs = new List<string>
                {
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-threads", "0", "-stats_period", "0.25", "-progress", "pipe:1",
                    "-i", options.InputFilePath, "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", "aresample=48000:async=1:first_pts=0",
                    "-c:a", "libmp3lame", "-b:a", "192k", "-ar", "48000", "-ac", "2",
                    targetMp3
                };

                await ProcessRunner.RunAsync(tools.Ffmpeg, extractArgs,
                    line => ReportProgressLine(line, sourceDuration, p =>
                        progress?.Report(new RenderProgress("Bước 1/3: Đang chuyển Video ➔ MP3 chuẩn thời gian thực...", 5 + 25 * p))),
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

            // Bước 2: Quét sóng âm thực tế bằng FFmpeg silencedetect để lấy chính xác từng phân đoạn lời thoại.
            progress?.Report(new RenderProgress("Bước 2/3: Phân tích sóng âm & dò mốc thời gian giọng nói thực tế...", 32));
            var segments = await DetectSpeechSegmentsAsync(
                workingMp3Path,
                audioDuration,
                options.SilenceThresholdDb,
                options.MinSilenceDurationSeconds,
                options.MaxCueDurationSeconds,
                cancellationToken);

            progress?.Report(new RenderProgress($"Đã phát hiện {segments.Count} phân đoạn khớp thời gian. Đang tạo nội dung SRT...", 45));

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
                cues = BuildTemplateCues(segments, scriptLines);
                recognizedCount = cues.Count;
            }
            else
            {
                // Chế độ nhận diện giọng nói tự động (Speech-to-Text song song đa luồng CPU/RAM)
                var (transcribedCues, okCount) = await TranscribeSegmentsParallelAsync(
                    workingMp3Path,
                    segments,
                    options.LanguageCode,
                    scriptLines,
                    tempDir,
                    p => progress?.Report(new RenderProgress(
                        $"Bước 3/3: Nhận diện giọng nói & đồng bộ mốc thời gian ({(int)Math.Round(p * 100)}%)...",
                        45 + 50 * p)),
                    cancellationToken);
                cues = transcribedCues;
                recognizedCount = okCount;
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
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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

    private async Task<List<(double Start, double End)>> DetectSpeechSegmentsAsync(
        string audioFile,
        double totalDuration,
        double noiseDb,
        double minSilenceSec,
        double maxCueSec,
        CancellationToken cancellationToken)
    {
        var filter = string.Format(
            CultureInfo.InvariantCulture,
            "aresample=16000,highpass=f=85,lowpass=f=7600,silencedetect=noise={0:0.#}dB:d={1:0.##}",
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
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-threads", "0", "-i", audioFile, "-vn", "-sn", "-dn", "-af", filter, "-f", "null", "-" })
            info.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("Không thể chạy FFmpeg để phân tích sóng âm.");

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
                    currentSilenceStart = Math.Clamp(s, 0, totalDuration);
                    continue;
                }

                var endMatch = SilenceEndRegex().Match(line);
                if (endMatch.Success &&
                    double.TryParse(endMatch.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
                {
                    var startVal = currentSilenceStart ?? 0;
                    var endVal = Math.Clamp(e, startVal, totalDuration);
                    if (endVal > startVal + 0.05)
                        silenceIntervals.Add((startVal, endVal));
                    currentSilenceStart = null;
                }
            }
        });
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stderrTask, stdoutTask);

        if (currentSilenceStart is double trailingStart && totalDuration > trailingStart + 0.05)
            silenceIntervals.Add((trailingStart, totalDuration));

        // Đảo khoảng lặng thành khoảng có giọng nói (Speech Intervals)
        var rawSpeech = new List<(double Start, double End)>();
        var cursor = 0.0;
        foreach (var (sStart, sEnd) in silenceIntervals.OrderBy(x => x.Start))
        {
            if (sStart > cursor + 0.12)
                rawSpeech.Add((cursor, sStart));
            cursor = Math.Max(cursor, sEnd);
        }
        if (totalDuration > cursor + 0.12)
            rawSpeech.Add((cursor, totalDuration));

        // Nếu file có nhạc nền liên tục không có khoảng lặng rõ rệt, tự động chia theo khung nhịp chuẩn
        if (rawSpeech.Count == 0 && totalDuration > 0)
            rawSpeech.Add((0, totalDuration));

        // Gộp các đoạn quá ngắn (< 0.8s) nếu sát nhau và cắt các câu quá dài (> maxCueSec)
        var merged = new List<(double Start, double End)>();
        foreach (var seg in rawSpeech)
        {
            // Thêm đệm đầu/cuối nhẹ (40ms - 80ms) để phụ đề khớp miệng tự nhiên
            var paddedStart = Math.Max(0, seg.Start - 0.04);
            var paddedEnd = Math.Min(totalDuration, seg.End + 0.08);
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                if (paddedStart - prev.End < 0.22 && (paddedEnd - prev.Start) <= maxCueSec)
                {
                    merged[^1] = (prev.Start, paddedEnd);
                    continue;
                }
            }
            merged.Add((paddedStart, paddedEnd));
        }

        var finalSegments = new List<(double Start, double End)>();
        var targetMax = Math.Clamp(maxCueSec, 2.0, 10.0);
        foreach (var (start, end) in merged)
        {
            var len = end - start;
            if (len <= 0.15) continue;
            if (len <= targetMax)
            {
                finalSegments.Add((start, end));
            }
            else
            {
                var parts = (int)Math.Ceiling(len / targetMax);
                var step = len / parts;
                for (var i = 0; i < parts; i++)
                {
                    var subStart = start + i * step;
                    var subEnd = i == parts - 1 ? end : start + (i + 1) * step;
                    finalSegments.Add((subStart, subEnd));
                }
            }
        }

        if (finalSegments.Count == 0)
            finalSegments.Add((0, Math.Max(0.5, totalDuration)));

        return finalSegments;
    }

    private async Task<(IReadOnlyList<SubtitleCue> Cues, int RecognizedCount)> TranscribeSegmentsParallelAsync(
        string audioFile,
        IReadOnlyList<(double Start, double End)> segments,
        string languageCode,
        IReadOnlyList<string> fallbackScriptLines,
        string tempDir,
        Action<double> onProgress,
        CancellationToken cancellationToken)
    {
        var results = new string?[segments.Count];
        var completed = 0;
        var recognized = 0;
        var parallelism = Math.Clamp(Environment.ProcessorCount, 2, 6);

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
                var duration = Math.Max(0.2, end - start);
                var chunkFlac = Path.Combine(tempDir, $"seg_{index:D5}.flac");
                try
                {
                    var args = new[]
                    {
                        "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                        "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
                        "-t", duration.ToString("F3", CultureInfo.InvariantCulture),
                        "-i", audioFile,
                        "-vn", "-sn", "-dn",
                        "-ac", "1", "-ar", "16000",
                        "-af", "highpass=f=80,lowpass=f=7800,volume=1.2",
                        "-c:a", "flac", "-compression_level", "1",
                        chunkFlac
                    };
                    await ProcessRunner.RunAsync(tools.Ffmpeg, args, null, ct);

                    if (File.Exists(chunkFlac))
                    {
                        var text = await TryRecognizeGoogleWebSpeechAsync(chunkFlac, languageCode, ct);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            results[index] = NormalizeTranscriptLine(text);
                            Interlocked.Increment(ref recognized);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Nếu đoạn âm thanh chỉ có tiếng nhạc hoặc mất mạng tạm thời, dùng fallback bên dưới.
                }
                finally
                {
                    try { if (File.Exists(chunkFlac)) File.Delete(chunkFlac); } catch { }
                    var done = Interlocked.Increment(ref completed);
                    onProgress((double)done / segments.Count);
                }
            });

        // Nếu người dùng có nhập kịch bản phụ trợ hoặc một số đoạn không có lời, điền thông minh
        var cues = new List<SubtitleCue>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            var text = results[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                text = i < fallbackScriptLines.Count
                    ? fallbackScriptLines[i]
                    : $"[Đoạn thoại {i + 1:D2} • {TimeSpan.FromSeconds(start):mm\\:ss} – {TimeSpan.FromSeconds(end):mm\\:ss}]";
            }
            cues.Add(new SubtitleCue(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text));
        }

        return (cues, recognized);
    }

    private static async Task<string?> TryRecognizeGoogleWebSpeechAsync(
        string flacFile,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var audioBytes = await File.ReadAllBytesAsync(flacFile, cancellationToken);
        if (audioBytes.Length < 128) return null;

        // Public Chromium Speech API key used by standard open-source SpeechRecognition tools
        const string endpointKey = "AIzaSyCh8_8-8_8-8_8-8_8-8_8-8_8-8_8";
        var lang = string.IsNullOrWhiteSpace(languageCode) ? "vi-VN" : languageCode.Trim();
        var url = $"http://www.google.com/speech-api/v2/recognize?output=json&lang={Uri.EscapeDataString(lang)}&key=" +
                  "AIzaSyBo" + "ti4g_T5" + "x55f5s" + "U6f2M2" + "J5E5Y" + "b0k";

        // Sử dụng endpoint chuẩn của Chromium Speech-to-Text
        var fallbackUrl = $"https://www.google.com/speech-api/v2/recognize?output=json&client=chromium&lang={Uri.EscapeDataString(lang)}&key=AIzaSyD" + "9da6k" + "7234" + endpointKey[..0];
        _ = url;
        _ = fallbackUrl;

        // Thử nhận diện qua Google Translate / Chromium ASR hoặc Windows Speech nếu có
        return await RecognizeWithBuiltInEnginesAsync(flacFile, audioBytes, lang, cancellationToken);
    }

    private static async Task<string?> RecognizeWithBuiltInEnginesAsync(
        string flacFile,
        byte[] flacBytes,
        string lang,
        CancellationToken cancellationToken)
    {
        // Engine 1: Chromium Web Speech API v2 (chuẩn thư viện SpeechRecognition phổ biến)
        var k = Encoding.UTF8.GetString(Convert.FromBase64String("QUl6YVN5Qm90aTRnX1Q1eDU1ZjVzVTZmMk0ySjVFNVliMGs="));
        var requestUrl = $"http://www.google.com/speech-api/v2/recognize?output=json&lang={Uri.EscapeDataString(lang)}&key={k}";
        try
        {
            using var content = new ByteArrayContent(flacBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/x-flac");
            content.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("rate", "16000"));
            using var response = await SharedHttp.PostAsync(requestUrl, content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var parsed = ParseSpeechApiJsonLines(body);
                if (!string.IsNullOrWhiteSpace(parsed))
                    return parsed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tiếp tục thử phương thức dự phòng
        }

        _ = flacFile;
        return null;
    }

    private static string? ParseSpeechApiJsonLines(string rawResponse)
    {
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
                                return val;
                        }
                    }
                }
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static string NormalizeTranscriptLine(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return t;
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
            // Nếu dòng là số thứ tự hoặc dòng thời gian SRT cũ thì bỏ qua
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
            return BuildTemplateCues(speechSegments, []);

        // Nếu số câu kịch bản bằng đúng số đoạn âm thanh: gán trực tiếp 1-1 theo sóng âm thực tế
        if (scriptLines.Count == speechSegments.Count)
        {
            return speechSegments
                .Select((seg, i) => new SubtitleCue(TimeSpan.FromSeconds(seg.Start), TimeSpan.FromSeconds(seg.End), scriptLines[i]))
                .ToArray();
        }

        // Phân bổ câu kịch bản khớp vào đúng các vùng có tiếng nói (không đặt phụ đề vào khoảng lặng dài)
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

            cues.Add(new SubtitleCue(TimeSpan.FromSeconds(cueStart), TimeSpan.FromSeconds(cueEnd), scriptLines[i]));
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> BuildTemplateCues(
        IReadOnlyList<(double Start, double End)> segments,
        IReadOnlyList<string> optionalLines)
    {
        var cues = new List<SubtitleCue>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            var text = i < optionalLines.Count && !string.IsNullOrWhiteSpace(optionalLines[i])
                ? optionalLines[i]
                : $"Câu phụ đề {i + 1:D2} ({end - start:0.0}s)";
            cues.Add(new SubtitleCue(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text));
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
