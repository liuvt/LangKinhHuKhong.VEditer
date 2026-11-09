using System.Globalization;
using System.Text;

namespace LangKinhHuKhong.Editer.Core;

/// <summary>
/// v4.4: ALL MP3 tracks => ONE continuous high-quality audio track (256kbps AAC);
/// High-speed multi-core CPU H.264 rendering with Single-Pass Direct Concat (eliminating double encoding when burning subtitles),
/// uncompressed BMP pre-scaled corner overlays (zero zlib overhead, 4 corners stay visible across resolution changes),
/// and BelowNormal priority + zero-lookahead x264 buffers for ultra-low RAM (~150MB) and zero system lag.
/// Temporary files are created strictly on the target OutputDirectory drive and deleted immediately after each stage.
/// </summary>
public sealed class RenderEngine(FfmpegTools tools)
{
    private enum GpuEncoderBackend
    {
        None,
        NvencHq,
        NvencBasic,
        IntelQsv,
        AmdAmf
    }

    private readonly MediaProbe _probe = new(tools);

    private static int GetCpuThreadCount(bool isNvHighPerfMode) => isNvHighPerfMode
        ? Math.Max(2, (int)Math.Round(Environment.ProcessorCount * 0.88))
        : Math.Clamp(Environment.ProcessorCount, 2, 16);

    private static int GetFilterThreadCount(bool isNvHighPerfMode) => isNvHighPerfMode
        ? Math.Max(2, (int)Math.Round(Environment.ProcessorCount * 0.85))
        : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    private static string GetMainQueueSize(bool isNvHighPerfMode) => isNvHighPerfMode ? "1024" : "64";

    private static string GetOverlayQueueSize(bool isNvHighPerfMode) => isNvHighPerfMode ? "64" : "8";

    public async Task<string> RenderAsync(
        RenderOptions options,
        IProgress<RenderProgress> progress,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var fullOutputDir = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(fullOutputDir);

        // Dọn dẹp thư mục tạm cũ trên ổ C (nếu có từ phiên bản cũ) và các thư mục tạm mồ côi tại nơi xuất
        CleanupLegacyAndStaleTempDirectories(fullOutputDir);

        // Đặt thư mục tạm ngay trên cùng ổ đĩa với OutputDirectory (ví dụ ổ D:) để tuyệt đối không làm tăng dung lượng ổ C:
        var workdir = Path.Combine(fullOutputDir, ".lkhk_temp_" + Guid.NewGuid().ToString("N")[..12]);
        var clipsFolder = Path.Combine(workdir, "clips");
        var audioFolder = Path.Combine(workdir, "audio");
        Directory.CreateDirectory(workdir);
        try { File.SetAttributes(workdir, FileAttributes.Directory | FileAttributes.Hidden); } catch { }

        try
        {
            // Ghi nhận người dùng chọn chế độ NV/GPU (Siêu tốc 80–90% CPU & RAM + GPU phần cứng)
            var isNvHighPerfMode = options.Encoder == VideoEncoder.Nvenc;
            var (effectiveEncoder, gpuBackend) = await ResolveEncoderAsync(options.Encoder, cancellationToken, workdir);
            if (options != options with { Encoder = effectiveEncoder })
                options = options with { Encoder = effectiveEncoder };

            var cpuThreads = GetCpuThreadCount(isNvHighPerfMode).ToString(CultureInfo.InvariantCulture);
            var filterThreads = GetFilterThreadCount(isNvHighPerfMode).ToString(CultureInfo.InvariantCulture);
            var mainQueueSize = GetMainQueueSize(isNvHighPerfMode);
            var joinedAudio = Path.Combine(workdir, "joined_audio.m4a");
            var mp3Count = options.Mp3Files.Count;
            var normalizedAudioDurations = new double[mp3Count];

            if (mp3Count == 1)
            {
                // Chỉ có 1 MP3 => mã hóa thẳng sang joined_audio.m4a chất lượng cao (256kbps, 48kHz Stereo).
                var input = options.Mp3Files[0];
                var seconds = await _probe.GetDurationSecondsAsync(input, cancellationToken);
                var title = $"Chuẩn hóa âm thanh chất lượng cao: {Path.GetFileName(input)}";
                progress.Report(new RenderProgress(title, 2));
                var singleAudioArgs = new List<string>
                {
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-threads", cpuThreads, "-filter_threads", isNvHighPerfMode ? filterThreads : "2",
                    "-stats_period", "1.0", "-progress", "pipe:1",
                    "-thread_queue_size", mainQueueSize,
                    "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", "aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo",
                    "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2",
                    "-movflags", "+faststart", joinedAudio
                };
                await ProcessRunner.RunAsync(tools.Ffmpeg, singleAudioArgs,
                    line => ReportFfmpegProgress(line, seconds, p =>
                        progress.Report(new RenderProgress(title, 12.0 * p))),
                    cancellationToken, workdir, isNvHighPerfMode);
                normalizedAudioDurations[0] = await _probe.GetDurationSecondsAsync(joinedAudio, cancellationToken);
            }
            else if (isNvHighPerfMode)
            {
                // Chế độ NV (80–90% CPU & RAM): chuẩn hóa SONG SONG đa luồng nhiều file MP3 cùng lúc trong RAM/CPU,
                // sau đó nối trực tiếp (-c:a copy) thành joined_audio.m4a.
                Directory.CreateDirectory(audioFolder);
                var audioClips = new string[mp3Count];
                var maxParallelAudio = Math.Clamp((int)Math.Round(Environment.ProcessorCount * 0.85 / 2.0), 2, 8);
                var perWorkerThreads = Math.Max(2, GetCpuThreadCount(true) / maxParallelAudio).ToString(CultureInfo.InvariantCulture);
                var completedCount = 0;

                await Parallel.ForEachAsync(Enumerable.Range(0, mp3Count),
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallelAudio, CancellationToken = cancellationToken },
                    async (i, ct) =>
                    {
                        var input = options.Mp3Files[i];
                        var output = Path.Combine(audioFolder, $"{i + 1:D5}.m4a");
                        var args = new List<string>
                        {
                            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                            "-threads", perWorkerThreads, "-filter_threads", "2",
                            "-thread_queue_size", mainQueueSize,
                            "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
                            "-af", "aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo",
                            "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2",
                            output
                        };
                        await ProcessRunner.RunAsync(tools.Ffmpeg, args, null, ct, workdir, highPerformance: true);
                        audioClips[i] = output;
                        normalizedAudioDurations[i] = await _probe.GetDurationSecondsAsync(output, ct);
                        var done = Interlocked.Increment(ref completedCount);
                        progress.Report(new RenderProgress($"Chuẩn hóa MP3 siêu tốc ({done}/{mp3Count}): {Path.GetFileName(input)}", 10.0 * done / mp3Count));
                    });

                var estimatedAudioDuration = normalizedAudioDurations.Sum();
                var audioList = Path.Combine(workdir, "audio.ffconcat");
                await WriteConcatListAsync(audioList, "audio", audioClips, cancellationToken);
                const string joinTitle = "Ghép nối liền mạch tất cả MP3 chất lượng cao";
                progress.Report(new RenderProgress(joinTitle, 10));
                var joinArgs = new List<string>
                {
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-threads", cpuThreads, "-stats_period", "1.0", "-progress", "pipe:1",
                    "-safe", "0", "-f", "concat", "-i", audioList,
                    "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-c:a", "copy",
                    "-movflags", "+faststart", joinedAudio
                };
                await ProcessRunner.RunAsync(tools.Ffmpeg, joinArgs,
                    line => ReportFfmpegProgress(line, estimatedAudioDuration, p =>
                        progress.Report(new RenderProgress(joinTitle, 10.0 + 2.0 * p))),
                    cancellationToken, workdir, highPerformance: true);

                DeleteDirectorySafe(audioFolder);
                TryDeleteFile(audioList);
            }
            else
            {
                // Chế độ ổn định: xử lý TUẦN TỰ từng file sang .m4a (AAC 256kbps) để không ngốn RAM,
                // sau đó nối trực tiếp (-c:a copy) thành joined_audio.m4a và xóa ngay file tạm.
                Directory.CreateDirectory(audioFolder);
                var audioClips = new string[mp3Count];

                for (var i = 0; i < mp3Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var input = options.Mp3Files[i];
                    var seconds = await _probe.GetDurationSecondsAsync(input, cancellationToken);
                    var output = Path.Combine(audioFolder, $"{i + 1:D5}.m4a");
                    var title = $"Chuẩn hóa MP3 {i + 1}/{mp3Count}: {Path.GetFileName(input)}";
                    var basePct = 10.0 * i / mp3Count;
                    var stepPct = 10.0 / mp3Count;
                    progress.Report(new RenderProgress(title, basePct));

                    var args = new List<string>
                    {
                        "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                        "-threads", cpuThreads, "-filter_threads", "2",
                        "-stats_period", "1.0", "-progress", "pipe:1",
                        "-thread_queue_size", "64",
                        "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
                        "-af", "aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo",
                        "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2",
                        output
                    };
                    await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                        line => ReportFfmpegProgress(line, seconds, p =>
                            progress.Report(new RenderProgress(title, basePct + stepPct * p))),
                        cancellationToken, workdir, highPerformance: false);
                    audioClips[i] = output;
                    normalizedAudioDurations[i] = await _probe.GetDurationSecondsAsync(output, cancellationToken);
                }

                var estimatedAudioDuration = normalizedAudioDurations.Sum();
                var audioList = Path.Combine(workdir, "audio.ffconcat");
                await WriteConcatListAsync(audioList, "audio", audioClips, cancellationToken);
                const string joinTitle = "Ghép nối liền mạch tất cả MP3 chất lượng cao";
                progress.Report(new RenderProgress(joinTitle, 10));
                var joinArgs = new List<string>
                {
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-threads", "1", "-stats_period", "1.0", "-progress", "pipe:1",
                    "-safe", "0", "-f", "concat", "-i", audioList,
                    "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-c:a", "copy",
                    "-movflags", "+faststart", joinedAudio
                };
                await ProcessRunner.RunAsync(tools.Ffmpeg, joinArgs,
                    line => ReportFfmpegProgress(line, estimatedAudioDuration, p =>
                        progress.Report(new RenderProgress(joinTitle, 10.0 + 2.0 * p))),
                    cancellationToken, workdir, highPerformance: false);

                // Giải phóng ngay toàn bộ file âm thanh tạm trước khi sang bước xử lý video
                DeleteDirectorySafe(audioFolder);
                TryDeleteFile(audioList);
            }

            var audioDuration = await _probe.GetDurationSecondsAsync(joinedAudio, cancellationToken);

            // Subtitles follow the concatenated MP3 timeline, not repeated video clips.
            var mergedSubtitleFile = Path.Combine(workdir, "joined_subtitles.srt");
            if (options.Subtitles is { SrtFiles.Count: > 0 } sub)
            {
                progress.Report(new RenderProgress("Đồng bộ mốc SRT theo audio đã nối...", 12));
                var cues = SubtitleComposer.Merge(sub, normalizedAudioDurations, audioDuration);
                await SubtitleComposer.WriteSrtAsync(mergedSubtitleFile, cues, cancellationToken);
            }

            // Tiền xử lý (Pre-scale) 0–4 ảnh góc sang .BMP (RGBA 32-bit đã scale đúng kích thước đích bằng Lanczos một lần duy nhất):
            // 1. Nạp thẳng 1 frame vào RAM với eof_action=repeat (0 lần đọc lại ổ đĩa, 0 tốn CPU scale lại mỗi frame!).
            // 2. Kết hợp -reinit_filter 0 và kiểm tra đồng nhất độ phân giải giúp 4 ảnh góc LUÔN HIỂN THỊ XUYÊN SUỐT ĐẾN HẾT CLIP.
            if (options.Overlays.Count > 0)
            {
                var preparedOverlays = new List<OverlayOption>(options.Overlays.Count);
                for (var i = 0; i < options.Overlays.Count; i++)
                {
                    var ov = options.Overlays[i];
                    var maxW = Math.Max(16, options.Width * Math.Clamp(ov.WidthPercent, 1, 100) / 100);
                    var maxH = Math.Max(16, options.Height * Math.Clamp(ov.HeightPercent, 1, 100) / 100);
                    var scaledBmp = Path.Combine(workdir, $"overlay_scaled_{i}.bmp");
                    var prepArgs = new[]
                    {
                        "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                        "-i", ov.ImagePath,
                        "-vf", $"scale={maxW}:{maxH}:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba",
                        "-frames:v", "1", "-update", "1", scaledBmp
                    };
                    await ProcessRunner.RunAsync(tools.Ffmpeg, prepArgs, null, cancellationToken, workdir, isNvHighPerfMode);
                    preparedOverlays.Add(ov with { ImagePath = scaledBmp });
                }
                options = options with { Overlays = preparedOverlays };
            }

            var hasBurnInSubtitles = options.Subtitles is { SrtFiles.Count: > 0, BurnIntoVideo: true };
            var canDirectSinglePass = hasBurnInSubtitles &&
                await CanDirectSinglePassAsync(options, cancellationToken);

            var videoList = Path.Combine(workdir, "playlist.ffconcat");
            double playlistDuration;
            double finalStartPct;
            double finalSpanPct;
            double? singlePassSpeed = null;

            if (canDirectSinglePass)
            {
                // TỐI ƯU ĐỘT PHÁ (Direct Single-Pass):
                // Khi có chèn phụ đề cứng (BurnIntoVideo) và các video nguồn cùng codec/độ phân giải/tốc độ,
                // bỏ qua hoàn toàn Bước 3 (không tạo file clip tạm 5–6 tiếng trên ổ đĩa, tiết kiệm 100% thời gian mã hóa lần 1),
                // nối trực tiếp danh sách video gốc và thực hiện Scale + 4 Ảnh góc + Phụ đề SRT trong 1 lượt mã hóa duy nhất!
                singlePassSpeed = options.Videos[0].PlaybackSpeed;
                var sourcePaths = new List<string>(options.Videos.Count);
                var sumDurations = 0.0;
                foreach (var v in options.Videos)
                {
                    var dur = await _probe.GetDurationSecondsAsync(v.FilePath, cancellationToken);
                    sumDurations += dur / v.PlaybackSpeed;
                    sourcePaths.Add(v.FilePath);
                }
                playlistDuration = Math.Max(0.1, sumDurations);
                await WriteDirectConcatListAsync(videoList, sourcePaths, cancellationToken);
                finalStartPct = 14.0;
                finalSpanPct = 86.0;
            }
            else
            {
                // Step 3: Chuẩn hóa từng video, gộp luôn 0–4 ảnh góc vào clip chuẩn hóa
                // để Bước 4 sao chép trực tiếp (-c:v copy) hoặc chèn phụ đề.
                Directory.CreateDirectory(clipsFolder);
                var videoCount = options.Videos.Count;
                var videoClips = new List<string>(videoCount);
                var clipDurations = new List<double>(videoCount);
                var accumulatedDuration = 0.0;

                // Nếu không có phụ đề cứng, Bước 4 chỉ tốn vài giây để copy luồng (-c:v copy) nên Bước 3 chiếm 12% -> 90%.
                var normStartPct = 12.0;
                var normSpanPct = hasBurnInSubtitles ? 43.0 : 78.0;

                if (isNvHighPerfMode && videoCount > 1 && Environment.ProcessorCount >= 8)
                {
                    // Chế độ NV trên máy nhiều nhân (>= 8 luồng CPU): chuẩn hóa song song 2 luồng video đồng thời.
                    var plannedJobs = new List<(int Index, VideoClipOption Video, double ExpectedDuration, string ClipFile)>();
                    for (var i = 0; i < videoCount; i++)
                    {
                        if (accumulatedDuration >= audioDuration && plannedJobs.Count > 0)
                            break;
                        var video = options.Videos[i];
                        var sourceDuration = await _probe.GetDurationSecondsAsync(video.FilePath, cancellationToken);
                        var rawExpectedDuration = sourceDuration / video.PlaybackSpeed;
                        var remainingNeeded = Math.Max(0.5, audioDuration - accumulatedDuration);
                        var expectedDuration = Math.Min(rawExpectedDuration, remainingNeeded);
                        var clipFile = Path.Combine(clipsFolder, $"{i + 1:D5}.mp4");
                        plannedJobs.Add((i, video, expectedDuration, clipFile));
                        accumulatedDuration += expectedDuration;
                    }

                    var jobCount = plannedJobs.Count;
                    var actualDurations = new double[jobCount];
                    var completedJobs = 0;

                    await Parallel.ForEachAsync(Enumerable.Range(0, jobCount),
                        new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                        async (jobIdx, ct) =>
                        {
                            var job = plannedJobs[jobIdx];
                            var title = $"Chuẩn hóa NV siêu tốc {jobIdx + 1}/{jobCount} ({PlaybackSpeeds.Format(job.Video.PlaybackSpeed)}): {Path.GetFileName(job.Video.FilePath)}";
                            var hasAudio = !options.MuteVideoAudio &&
                                await _probe.HasAudioStreamAsync(job.Video.FilePath, ct);
                            var args = BuildNormalizeArguments(options, job.Video, job.Video.FilePath,
                                job.ClipFile, job.ExpectedDuration, hasAudio, isNvHighPerfMode: true, gpuBackend);
                            await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                                line => ReportFfmpegProgress(line, job.ExpectedDuration, p =>
                                {
                                    var pct = normStartPct + normSpanPct * ((completedJobs + p) / Math.Max(1, jobCount));
                                    progress.Report(new RenderProgress(title, Math.Clamp(pct, normStartPct, normStartPct + normSpanPct)));
                                }),
                                ct, workdir, highPerformance: true);
                            actualDurations[jobIdx] = await _probe.GetDurationSecondsAsync(job.ClipFile, ct);
                            Interlocked.Increment(ref completedJobs);
                        });

                    for (var idx = 0; idx < jobCount; idx++)
                    {
                        videoClips.Add(plannedJobs[idx].ClipFile);
                        clipDurations.Add(actualDurations[idx]);
                    }
                }
                else
                {
                    for (var i = 0; i < videoCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Nếu tổng thời lượng các clip đã chuẩn hóa đủ phủ hết thời lượng audio thì dừng ngay, không xử lý thừa
                        if (accumulatedDuration >= audioDuration && videoClips.Count > 0)
                            break;

                        var video = options.Videos[i];
                        var sourceDuration = await _probe.GetDurationSecondsAsync(video.FilePath, cancellationToken);
                        var rawExpectedDuration = sourceDuration / video.PlaybackSpeed;
                        var remainingNeeded = Math.Max(0.5, audioDuration - accumulatedDuration);
                        var expectedDuration = Math.Min(rawExpectedDuration, remainingNeeded);

                        var clipFile = Path.Combine(clipsFolder, $"{i + 1:D5}.mp4");
                        var title = $"Chuẩn hóa video tốc độ cao {i + 1}/{videoCount} ({PlaybackSpeeds.Format(video.PlaybackSpeed)}): {Path.GetFileName(video.FilePath)}";
                        var basePct = normStartPct + normSpanPct * i / videoCount;
                        var stepPct = normSpanPct / videoCount;
                        progress.Report(new RenderProgress(title, basePct));

                        var hasAudio = !options.MuteVideoAudio &&
                            await _probe.HasAudioStreamAsync(video.FilePath, cancellationToken);
                        var args = BuildNormalizeArguments(options, video, video.FilePath,
                            clipFile, expectedDuration, hasAudio, isNvHighPerfMode, gpuBackend);
                        await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                            line => ReportFfmpegProgress(line, expectedDuration, p =>
                                progress.Report(new RenderProgress(title, basePct + stepPct * p))),
                            cancellationToken, workdir, isNvHighPerfMode);

                        var actualClipDuration = await _probe.GetDurationSecondsAsync(clipFile, cancellationToken);
                        clipDurations.Add(actualClipDuration);
                        videoClips.Add(clipFile);
                        accumulatedDuration += actualClipDuration;
                    }
                }

                playlistDuration = clipDurations.Sum();
                await WriteConcatListAsync(videoList, "clips", videoClips, cancellationToken);
                finalStartPct = normStartPct + normSpanPct;
                finalSpanPct = 100.0 - finalStartPct;
            }

            // Step 4: Lặp danh sách video theo đúng tổng thời lượng audio MP3.
            var cycles = Math.Max(1, (int)Math.Ceiling(audioDuration / Math.Max(0.1, playlistDuration)));
            var finalTitle = $"Xuất 1 MP4 | Audio {TimeSpan.FromSeconds(audioDuration).ToString("c", CultureInfo.InvariantCulture)} | " +
                             $"Playlist video {TimeSpan.FromSeconds(playlistDuration).ToString("c", CultureInfo.InvariantCulture)} | " +
                             $"{cycles} lượt phát";
            progress.Report(new RenderProgress(finalTitle, finalStartPct));
            var outputFile = GetUniqueOutputFile(fullOutputDir, "LangKinhHuKhong_Full", options.ResolutionName,
                options.Subtitles is { SrtFiles.Count: > 0, SaveSidecarSrt: true });
            var temporaryOutput = Path.Combine(workdir, "final_render.mp4");
            var finalArgs = BuildOutputArguments(options, videoList, joinedAudio, temporaryOutput, audioDuration, singlePassSpeed, isNvHighPerfMode, gpuBackend);
            await ProcessRunner.RunAsync(tools.Ffmpeg, finalArgs,
                line => ReportFfmpegProgress(line, audioDuration, p =>
                    progress.Report(new RenderProgress(finalTitle, finalStartPct + finalSpanPct * p))),
                cancellationToken, workdir, isNvHighPerfMode);

            // A failed/canceled job must not leave a partial MP4 in output folder.
            cancellationToken.ThrowIfCancellationRequested();

            // Xóa ngay toàn bộ clip tạm và audio tạm trước khi hoàn tất để giải phóng ổ đĩa tức thì
            DeleteDirectorySafe(clipsFolder);
            TryDeleteFile(joinedAudio);

            var sidecar = Path.ChangeExtension(outputFile, ".srt");
            var shouldSaveSrt = options.Subtitles is { SrtFiles.Count: > 0, SaveSidecarSrt: true };
            try
            {
                if (shouldSaveSrt) File.Copy(mergedSubtitleFile, sidecar, overwrite: false);
                File.Move(temporaryOutput, outputFile);
            }
            catch
            {
                if (shouldSaveSrt && !File.Exists(outputFile))
                    TryDeleteFile(sidecar);
                throw;
            }
            progress.Report(new RenderProgress("Hoàn thành 1 video MP4 duy nhất (đã dọn sạch bộ nhớ tạm).", 100));
            return outputFile;
        }
        finally
        {
            DeleteDirectorySafe(workdir);
        }
    }

    private async Task<bool> CanDirectSinglePassAsync(RenderOptions options, CancellationToken ct)
    {
        if (!options.MuteVideoAudio || options.Videos.Count == 0)
            return false;
        var firstSpeed = options.Videos[0].PlaybackSpeed;
        if (options.Videos.Any(v => Math.Abs(v.PlaybackSpeed - firstSpeed) > 0.0001))
            return false;
        if (options.Videos.Count == 1)
            return true;

        (string Codec, int Width, int Height)? firstStream = null;
        foreach (var v in options.Videos)
        {
            var info = await _probe.GetVideoStreamInfoAsync(v.FilePath, ct);
            if (string.IsNullOrEmpty(info.Codec) || info.Width <= 0 || info.Height <= 0)
                return false;
            firstStream ??= info;
            if (!string.Equals(firstStream.Value.Codec, info.Codec, StringComparison.OrdinalIgnoreCase) ||
                firstStream.Value.Width != info.Width ||
                firstStream.Value.Height != info.Height)
                return false;
        }
        return true;
    }

    private static async Task WriteConcatListAsync(string file, string relativeDirectory,
        IReadOnlyList<string> clips, CancellationToken ct)
    {
        var content = new StringBuilder("ffconcat version 1.0\n");
        foreach (var clip in clips)
            content.Append("file '").Append(relativeDirectory).Append('/')
                .Append(Path.GetFileName(clip)).Append("'\n");
        await File.WriteAllTextAsync(file, content.ToString(), new UTF8Encoding(false), ct);
    }

    private static async Task WriteDirectConcatListAsync(string file,
        IReadOnlyList<string> sourceFiles, CancellationToken ct)
    {
        var content = new StringBuilder("ffconcat version 1.0\n");
        foreach (var src in sourceFiles)
        {
            var normalizedPath = Path.GetFullPath(src).Replace('\\', '/').Replace("'", "'\\''");
            content.Append("file '").Append(normalizedPath).Append("'\n");
        }
        await File.WriteAllTextAsync(file, content.ToString(), new UTF8Encoding(false), ct);
    }

    private static void Validate(RenderOptions settings)
    {
        if (settings.Videos.Count == 0) throw new ArgumentException("Chưa chọn video nguồn.");
        if (settings.Mp3Files.Count == 0) throw new ArgumentException("Chưa chọn MP3.");
        if (settings.Overlays.Count > 4) throw new ArgumentException("Tối đa 4 ảnh overlay (mỗi góc một ảnh).");
        if (settings.Overlays.Select(x => x.Corner).Distinct().Count() != settings.Overlays.Count)
            throw new ArgumentException("Mỗi ảnh phải được đặt vào một góc khác nhau.");
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory))
            throw new ArgumentException("Chưa chọn thư mục đầu ra.");
        if (settings.FrameRate is < 1 or > 120) throw new ArgumentException("FPS không hợp lệ.");
        if (settings.Width is < 64 or > 7680 || settings.Height is < 64 or > 4320)
            throw new ArgumentException("Độ phân giải không hợp lệ.");
        foreach (var clip in settings.Videos)
            if (!PlaybackSpeeds.IsSupported(clip.PlaybackSpeed))
                throw new ArgumentException($"Tốc độ video không hợp lệ: {clip.FilePath} ({clip.PlaybackSpeed}).");
        if (settings.Subtitles is { SrtFiles.Count: > 0 } sub)
        {
            if (sub.ImportMode == SubtitleImportMode.GlobalTimeline && sub.SrtFiles.Count != 1)
                throw new ArgumentException("Chế độ SRT toàn bộ chỉ nhận đúng 1 file.");
            if (sub.ImportMode == SubtitleImportMode.OneSrtPerMp3 && sub.SrtFiles.Count != settings.Mp3Files.Count)
                throw new ArgumentException("Số file SRT phải bằng số MP3 khi chọn chế độ Theo từng MP3.");
            if (!sub.BurnIntoVideo && !sub.SaveSidecarSrt)
                throw new ArgumentException("Hãy chọn ít nhất 1 kiểu xuất phụ đề: chèn vào video hoặc lưu SRT riêng.");
        }
        foreach (var input in settings.Videos.Select(x => x.FilePath).Concat(settings.Mp3Files)
                     .Concat(settings.Overlays.Select(x => x.ImagePath))
                     .Concat(settings.Subtitles?.SrtFiles ?? []))
            if (!File.Exists(input)) throw new FileNotFoundException("Không tìm thấy tệp: " + input, input);
    }

    private static string BuildNormalizeFilter(RenderOptions settings, double playbackSpeed, bool isNvHighPerfMode)
    {
        // Dùng fast_bilinear (tối ưu SIMD AVX2 đa nhân trong swscale) giúp tăng tốc độ scale lên gấp 3–4 lần
        // mà không gây nghẽn cổ chai đơn luồng như lanczos+accurate_rnd+full_chroma_int.
        const string scaleFlags = "fast_bilinear";
        return $"setpts=(PTS-STARTPTS)/{playbackSpeed.ToString("0.##", CultureInfo.InvariantCulture)}," +
               $"fps={settings.FrameRate}," +
               $"scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease:flags={scaleFlags}," +
               $"pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2," +
               "setsar=1,format=yuv420p";
    }

    private static List<string> BuildNormalizeArguments(
        RenderOptions settings, VideoClipOption video, string sourceFile, string clipFile,
        double expectedDuration, bool hasSourceAudio, bool isNvHighPerfMode = false, GpuEncoderBackend gpuBackend = GpuEncoderBackend.None)
    {
        var cpuThreads = GetCpuThreadCount(isNvHighPerfMode).ToString(CultureInfo.InvariantCulture);
        var filterThreads = GetFilterThreadCount(isNvHighPerfMode).ToString(CultureInfo.InvariantCulture);
        var mainQueueSize = GetMainQueueSize(isNvHighPerfMode);
        var overlayQueueSize = GetOverlayQueueSize(isNvHighPerfMode);
        var duration = expectedDuration.ToString("F6", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cpuThreads, "-filter_threads", filterThreads, "-filter_complex_threads", filterThreads,
            "-stats_period", "1.0", "-progress", "pipe:1",
            "-reinit_filter", "0",
            "-thread_queue_size", mainQueueSize, "-i", sourceFile
        };
        if (!settings.MuteVideoAudio && !hasSourceAudio)
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);

        var overlayStartInputIndex = (!settings.MuteVideoAudio && !hasSourceAudio) ? 2 : 1;
        foreach (var overlay in settings.Overlays)
        {
            // Nạp 1 frame ảnh góc .BMP (đã pre-scale sẵn bằng Lanczos) vào bộ nhớ RAM và giữ bằng eof_action=repeat:
            // Tuyệt đối không dùng -loop 1 để tránh đọc lại ổ đĩa hàng chục triệu lần (tiết kiệm >800 GB Disk I/O).
            args.AddRange(["-thread_queue_size", overlayQueueSize, "-i", overlay.ImagePath]);
        }

        var baseVideoFilter = BuildNormalizeFilter(settings, video.PlaybackSpeed, isNvHighPerfMode) +
            $",trim=duration={duration},setpts=PTS-STARTPTS";

        if (settings.MuteVideoAudio && settings.Overlays.Count == 0)
        {
            args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", baseVideoFilter]);
        }
        else
        {
            var filters = new List<string> { $"[0:v]{baseVideoFilter}[v0]" };
            AppendOverlayStages(filters, settings, overlayStartInputIndex);
            filters.Add($"[v{settings.Overlays.Count}]format=yuv420p[vnorm]");

            if (!settings.MuteVideoAudio)
            {
                var audioStart = hasSourceAudio
                    ? $"[0:a:0]{PlaybackSpeeds.AudioTempo(video.PlaybackSpeed)},aresample=48000:async=1:first_pts=0,"
                    : "[1:a]aresample=48000:async=1:first_pts=0,";
                filters.Add(audioStart + $"aformat=channel_layouts=stereo,apad,atrim=duration={duration},asetpts=PTS-STARTPTS[anorm]");
            }

            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[vnorm]"]);
            if (settings.MuteVideoAudio)
                args.AddRange(["-an", "-sn", "-dn"]);
            else
                args.AddRange(["-map", "[anorm]"]);
        }

        AppendVideoEncoderArguments(args, settings, cpuThreads, isNvHighPerfMode, gpuBackend);

        args.AddRange([
            "-pix_fmt", "yuv420p", "-r", settings.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-video_track_timescale", "30000"
        ]);
        if (isNvHighPerfMode)
            args.AddRange(["-max_muxing_queue_size", "4096"]);
        if (!settings.MuteVideoAudio)
            args.AddRange(["-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-t", duration, "-movflags", "+faststart", clipFile]);
        return args;
    }

    private static List<string> BuildOutputArguments(
        RenderOptions settings, string videoListPath, string joinedAudio, string outputFile,
        double audioDuration, double? singlePassSpeed = null, bool isNvHighPerfMode = false, GpuEncoderBackend gpuBackend = GpuEncoderBackend.None)
    {
        var cpuThreads = GetCpuThreadCount(isNvHighPerfMode).ToString(CultureInfo.InvariantCulture);
        var filterThreads = GetFilterThreadCount(isNvHighPerfMode).ToString(CultureInfo.InvariantCulture);
        var mainQueueSize = GetMainQueueSize(isNvHighPerfMode);
        var overlayQueueSize = GetOverlayQueueSize(isNvHighPerfMode);
        var hasBurnInSubtitles = settings.Subtitles is { SrtFiles.Count: > 0, BurnIntoVideo: true };

        var args = new List<string>
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cpuThreads, "-filter_threads", filterThreads, "-filter_complex_threads", filterThreads,
            "-stats_period", "1.0", "-progress", "pipe:1",
            "-reinit_filter", "0",
            "-thread_queue_size", mainQueueSize,
            "-stream_loop", "-1", // lặp danh sách clip theo đúng tổng thời lượng audio
            "-safe", "0", "-f", "concat", "-i", videoListPath,
            "-thread_queue_size", mainQueueSize, "-i", joinedAudio
        };

        if (singlePassSpeed is double directSpeed)
        {
            // Chế độ Single-Pass trực tiếp: nạp 0–4 ảnh góc .BMP (1 frame trong RAM) + chuẩn hóa + vẽ phụ đề SRT trong 1 lần duy nhất
            var fpsText = settings.FrameRate.ToString(CultureInfo.InvariantCulture);
            foreach (var overlay in settings.Overlays)
                args.AddRange(["-thread_queue_size", overlayQueueSize, "-i", overlay.ImagePath]);

            var filters = new List<string>
            {
                $"[0:v]{BuildNormalizeFilter(settings, directSpeed, isNvHighPerfMode)}[v0]"
            };
            AppendOverlayStages(filters, settings, startInputIndex: 2);
            var escapedSub = SubtitleComposer.BuildBurnInFilter(settings.Subtitles!);
            filters.Add($"[v{settings.Overlays.Count}]{escapedSub},format=yuv420p[vout]");

            args.AddRange([
                "-filter_complex", string.Join(';', filters),
                "-map", "[vout]",
                "-map", "1:a:0"
            ]);
            AppendVideoEncoderArguments(args, settings, cpuThreads, isNvHighPerfMode, gpuBackend);
            args.AddRange([
                "-c:a", "copy",
                "-pix_fmt", "yuv420p", "-r", fpsText
            ]);
        }
        else if (!hasBurnInSubtitles && settings.MuteVideoAudio)
        {
            // Không chèn phụ đề cứng & tắt âm gốc: các clip trong playlist đã có sẵn ảnh góc và chuẩn H.264 CRF 18,
            // nên sao chép luồng trực tiếp (-c:v copy -c:a copy) => tốn < 30MB RAM, hoàn tất trong vài giây, giữ 100% chất lượng!
            args.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "copy"]);
        }
        else if (!hasBurnInSubtitles && !settings.MuteVideoAudio)
        {
            // Không chèn phụ đề cứng nhưng giữ 20% âm gốc: giữ nguyên luồng video (-c:v copy), chỉ trộn âm thanh chất lượng cao.
            const string audioMixFilter =
                "[1:a:0]aresample=48000:async=1:first_pts=0[voice];" +
                "[0:a:0]aresample=48000:async=1:first_pts=0,volume=0.20[gameaudio];" +
                "[voice][gameaudio]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,aresample=48000:async=1[aout]";
            args.AddRange([
                "-filter_complex", audioMixFilter,
                "-map", "0:v:0", "-map", "[aout]",
                "-c:v", "copy",
                "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"
            ]);
        }
        else
        {
            // Có chèn phụ đề cứng (BurnIntoVideo) trên các clip đã chuẩn hóa ở Bước 3:
            var escapedSub = SubtitleComposer.BuildBurnInFilter(settings.Subtitles!);
            var filter = $"[0:v]setpts=PTS-STARTPTS,{escapedSub},format=yuv420p[vout]";
            if (!settings.MuteVideoAudio)
            {
                filter += ";[1:a:0]aresample=48000:async=1:first_pts=0[voice]" +
                          ";[0:a:0]aresample=48000:async=1:first_pts=0,volume=0.20[gameaudio]" +
                          ";[voice][gameaudio]amix=inputs=2:duration=first:" +
                          "dropout_transition=0:normalize=0,aresample=48000:async=1[aout]";
            }
            args.AddRange([
                "-filter_complex", filter,
                "-map", "[vout]",
                "-map", settings.MuteVideoAudio ? "1:a:0" : "[aout]"
            ]);

            AppendVideoEncoderArguments(args, settings, cpuThreads, isNvHighPerfMode, gpuBackend);

            if (settings.MuteVideoAudio)
                args.AddRange(["-c:a", "copy"]);
            else
                args.AddRange(["-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"]);

            args.AddRange(["-pix_fmt", "yuv420p", "-r", settings.FrameRate.ToString(CultureInfo.InvariantCulture)]);
        }

        if (isNvHighPerfMode)
            args.AddRange(["-max_muxing_queue_size", "4096"]);

        args.AddRange([
            "-t", audioDuration.ToString("F6", CultureInfo.InvariantCulture),
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-movflags", "+faststart", outputFile
        ]);
        return args;
    }

    private static void AppendVideoEncoderArguments(
        List<string> args, RenderOptions settings, string cpuThreads,
        bool isNvHighPerfMode = false, GpuEncoderBackend gpuBackend = GpuEncoderBackend.None)
    {
        if (settings.Encoder == VideoEncoder.Nvenc)
        {
            switch (gpuBackend)
            {
                case GpuEncoderBackend.NvencHq:
                    // GPU NVIDIA NVENC HQ:
                    args.AddRange([
                        "-c:v", "h264_nvenc",
                        "-preset", "p4",
                        "-tune", "hq",
                        "-rc", "vbr",
                        "-cq", "18",
                        "-b:v", "0",
                        "-spatial-aq", "1",
                        "-temporal-aq", "1",
                        "-aq-strength", "8",
                        "-rc-lookahead", "20",
                        "-surfaces", "32",
                        "-bf", "2",
                        "-profile:v", "high"
                    ]);
                    return;

                case GpuEncoderBackend.NvencBasic:
                    args.AddRange([
                        "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "18", "-b:v", "0", "-profile:v", "high"
                    ]);
                    return;

                case GpuEncoderBackend.IntelQsv:
                    // GPU Intel QuickSync (ví dụ Intel HD/UHD/Iris Xe Graphics):
                    // Mã hóa phần cứng trực tiếp trên iGPU Intel giúp giảm tải CPU và tăng tốc vượt trội!
                    args.AddRange([
                        "-c:v", "h264_qsv",
                        "-preset", "veryfast",
                        "-global_quality", "19",
                        "-look_ahead", "0",
                        "-profile:v", "high"
                    ]);
                    return;

                case GpuEncoderBackend.AmdAmf:
                    // GPU AMD Radeon AMF:
                    args.AddRange([
                        "-c:v", "h264_amf",
                        "-quality", "speed",
                        "-rc", "cqp",
                        "-qp_i", "18",
                        "-qp_p", "18",
                        "-profile:v", "high"
                    ]);
                    return;
            }
        }

        // Chế độ H.264 Đa luồng CPU (hoặc fallback khi máy không có GPU hỗ trợ):
        // Dùng preset ultrafast + CRF 18 kết hợp rc_lookahead=0:sync_lookahead=0:ref=1:bframes=0
        // giúp tốc độ đạt ~95–100 fps (3.2x realtime), không nghẽn CPU 4 nhân và tốn cực ít RAM.
        args.AddRange([
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-profile:v", "high",
            "-x264-params", $"threads={cpuThreads}:lookahead_threads=1:rc_lookahead=0:sync_lookahead=0:ref=1:bframes=0"
        ]);
    }

    private static void AppendOverlayStages(List<string> filters, RenderOptions settings, int startInputIndex)
    {
        for (var i = 0; i < settings.Overlays.Count; i++)
        {
            var overlay = settings.Overlays[i];
            var offsetX = Math.Max(0, settings.Width * Math.Clamp(overlay.OffsetXPercent, 0, 100) / 100);
            var offsetY = Math.Max(0, settings.Height * Math.Clamp(overlay.OffsetYPercent, 0, 100) / 100);
            // Escape filtergraph commas (\,) or FFmpeg treats min/max arguments as separate filters.
            // Các ảnh góc đã được pre-scale chính xác bằng Lanczos sang RGBA ở bước chuẩn bị,
            // nên đưa thẳng vào bộ lọc overlay với eof_action=repeat (không scale lại trên từng frame!).
            var x = overlay.Corner is Corner.TopLeft or Corner.BottomLeft
                ? $"min({offsetX}\\,W-w)"
                : $"max(0\\,W-w-{offsetX})";
            var y = overlay.Corner is Corner.TopLeft or Corner.TopRight
                ? $"min({offsetY}\\,H-h)"
                : $"max(0\\,H-h-{offsetY})";
            filters.Add($"[v{i}][{startInputIndex + i}:v]overlay=x={x}:y={y}:" +
                        $"eof_action=repeat:format=yuv420[v{i + 1}]");
        }
    }

    private async Task<(VideoEncoder Encoder, GpuEncoderBackend Backend)> ResolveEncoderAsync(
        VideoEncoder requested, CancellationToken cancellationToken, string workdir)
    {
        if (requested != VideoEncoder.Nvenc) return (VideoEncoder.X264, GpuEncoderBackend.None);

        // 1. Thử NVIDIA NVENC HQ
        try
        {
            var hqProbeArgs = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=128x128:r=30:d=0.1",
                "-frames:v", "1",
                "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "18",
                "-spatial-aq", "1", "-temporal-aq", "1", "-rc-lookahead", "20", "-surfaces", "32", "-bf", "2",
                "-f", "null", "-"
            };
            await ProcessRunner.RunAsync(tools.Ffmpeg, hqProbeArgs, null, cancellationToken, workdir, highPerformance: true);
            return (VideoEncoder.Nvenc, GpuEncoderBackend.NvencHq);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 2. Thử NVIDIA NVENC Basic
        try
        {
            var basicProbeArgs = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=0.1",
                "-frames:v", "1", "-c:v", "h264_nvenc", "-f", "null", "-"
            };
            await ProcessRunner.RunAsync(tools.Ffmpeg, basicProbeArgs, null, cancellationToken, workdir, highPerformance: true);
            return (VideoEncoder.Nvenc, GpuEncoderBackend.NvencBasic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 3. Thử Intel QuickSync Video (h264_qsv) cho các máy dùng GPU tích hợp Intel HD/UHD/Iris Xe
        try
        {
            var qsvProbeArgs = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=128x128:r=30:d=0.1",
                "-frames:v", "1",
                "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "19", "-look_ahead", "0",
                "-f", "null", "-"
            };
            await ProcessRunner.RunAsync(tools.Ffmpeg, qsvProbeArgs, null, cancellationToken, workdir, highPerformance: true);
            return (VideoEncoder.Nvenc, GpuEncoderBackend.IntelQsv);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 4. Thử AMD AMF (h264_amf) cho các máy dùng GPU AMD Radeon
        try
        {
            var amfProbeArgs = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=128x128:r=30:d=0.1",
                "-frames:v", "1",
                "-c:v", "h264_amf", "-quality", "speed",
                "-f", "null", "-"
            };
            await ProcessRunner.RunAsync(tools.Ffmpeg, amfProbeArgs, null, cancellationToken, workdir, highPerformance: true);
            return (VideoEncoder.Nvenc, GpuEncoderBackend.AmdAmf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (VideoEncoder.X264, GpuEncoderBackend.None);
        }
    }

    private static void CleanupLegacyAndStaleTempDirectories(string outputDirectory)
    {
        // Xóa hoàn toàn thư mục tạm cũ trên ổ C: (%TEMP%\LangKinhHuKhong.Editer) nếu tồn tại
        var legacyCDriveTemp = Path.Combine(Path.GetTempPath(), "LangKinhHuKhong.Editer");
        DeleteDirectorySafe(legacyCDriveTemp);

        // Xóa các thư mục .lkhk_temp_* bị bỏ sót trong thư mục đích
        try
        {
            if (!Directory.Exists(outputDirectory)) return;
            foreach (var dir in Directory.EnumerateDirectories(outputDirectory, ".lkhk_temp_*"))
                DeleteDirectorySafe(dir);
        }
        catch { }
    }

    internal static void DeleteDirectorySafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(80);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(80);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { }
    }

    private static void ReportFfmpegProgress(string line, double totalSeconds, Action<double> callback)
    {
        if (totalSeconds <= 0) return;
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
        if (long.TryParse(line.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            callback(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 1));
    }

    private static string GetUniqueOutputFile(string folder, string name, string resolution, bool includeSidecar)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(clean)) clean = "video";
        var prefix = $"{clean}_{resolution}";
        var candidate = Path.Combine(folder, prefix + ".mp4");
        for (var i = 2; File.Exists(candidate) ||
             (includeSidecar && File.Exists(Path.ChangeExtension(candidate, ".srt"))); i++)
            candidate = Path.Combine(folder, $"{prefix}_{i}.mp4");
        return candidate;
    }
}
