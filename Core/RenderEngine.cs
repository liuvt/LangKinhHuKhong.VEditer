using System.Globalization;
using System.Text;

namespace LangKinhHuKhong.Editer.Core;

/// <summary>
/// v4.3: ALL MP3 tracks => ONE continuous high-quality audio track (256kbps AAC);
/// ALL videos => normalized sequentially with static corner PNG overlays baked once per short clip;
/// Low-RAM / anti-lag CPU mode (BelowNormal priority, bounded threads & x264 lookahead buffers, CRF 18 quality).
/// Temporary files are created strictly on the target OutputDirectory drive and deleted immediately after each stage.
/// </summary>
public sealed class RenderEngine(FfmpegTools tools)
{
    private readonly MediaProbe _probe = new(tools);

    private static int GetSafeCpuThreadCount() =>
        Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

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
            var effectiveEncoder = await ResolveEncoderAsync(options.Encoder, cancellationToken, workdir);
            if (options != options with { Encoder = effectiveEncoder })
                options = options with { Encoder = effectiveEncoder };

            var cpuThreads = GetSafeCpuThreadCount().ToString(CultureInfo.InvariantCulture);
            var joinedAudio = Path.Combine(workdir, "joined_audio.m4a");
            var mp3Count = options.Mp3Files.Count;
            var normalizedAudioDurations = new double[mp3Count];

            if (mp3Count == 1)
            {
                // Chỉ có 1 MP3 => mã hóa thẳng sang joined_audio.m4a chất lượng cao (256kbps, 48kHz Stereo), nhẹ RAM.
                var input = options.Mp3Files[0];
                var seconds = await _probe.GetDurationSecondsAsync(input, cancellationToken);
                var title = $"Chuẩn hóa âm thanh chất lượng cao: {Path.GetFileName(input)}";
                progress.Report(new RenderProgress(title, 2));
                var singleAudioArgs = new List<string>
                {
                    "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                    "-threads", cpuThreads, "-filter_threads", "1",
                    "-stats_period", "1.0", "-progress", "pipe:1",
                    "-thread_queue_size", "32",
                    "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", "aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo",
                    "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2",
                    "-movflags", "+faststart", joinedAudio
                };
                await ProcessRunner.RunAsync(tools.Ffmpeg, singleAudioArgs,
                    line => ReportFfmpegProgress(line, seconds, p =>
                        progress.Report(new RenderProgress(title, 20.0 * p))),
                    cancellationToken, workdir);
                normalizedAudioDurations[0] = await _probe.GetDurationSecondsAsync(joinedAudio, cancellationToken);
            }
            else
            {
                // Nhiều MP3: xử lý TUẦN TỰ từng file sang .m4a (AAC 256kbps) để không ngốn RAM/CPU,
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
                    var basePct = 16.0 * i / mp3Count;
                    var stepPct = 16.0 / mp3Count;
                    progress.Report(new RenderProgress(title, basePct));

                    var args = new List<string>
                    {
                        "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                        "-threads", cpuThreads, "-filter_threads", "1",
                        "-stats_period", "1.0", "-progress", "pipe:1",
                        "-thread_queue_size", "32",
                        "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
                        "-af", "aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo",
                        "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2",
                        output
                    };
                    await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                        line => ReportFfmpegProgress(line, seconds, p =>
                            progress.Report(new RenderProgress(title, basePct + stepPct * p))),
                        cancellationToken, workdir);
                    audioClips[i] = output;
                    normalizedAudioDurations[i] = await _probe.GetDurationSecondsAsync(output, cancellationToken);
                }

                var estimatedAudioDuration = normalizedAudioDurations.Sum();
                var audioList = Path.Combine(workdir, "audio.ffconcat");
                await WriteConcatListAsync(audioList, "audio", audioClips, cancellationToken);
                const string joinTitle = "Ghép nối liền mạch tất cả MP3 chất lượng cao";
                progress.Report(new RenderProgress(joinTitle, 16));
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
                        progress.Report(new RenderProgress(joinTitle, 16.0 + 4.0 * p))),
                    cancellationToken, workdir);

                // Giải phóng ngay toàn bộ file âm thanh tạm trước khi sang bước xử lý video
                DeleteDirectorySafe(audioFolder);
                TryDeleteFile(audioList);
            }

            var audioDuration = await _probe.GetDurationSecondsAsync(joinedAudio, cancellationToken);

            // Subtitles follow the concatenated MP3 timeline, not repeated video clips.
            var mergedSubtitleFile = Path.Combine(workdir, "joined_subtitles.srt");
            if (options.Subtitles is { SrtFiles.Count: > 0 } sub)
            {
                progress.Report(new RenderProgress("Đồng bộ mốc SRT theo audio đã nối...", 20));
                var cues = SubtitleComposer.Merge(sub, normalizedAudioDurations, audioDuration);
                await SubtitleComposer.WriteSrtAsync(mergedSubtitleFile, cues, cancellationToken);
            }

            // Step 3: Chuẩn hóa TUẦN TỰ từng video (1 tiến trình duy nhất tại mỗi thời điểm để bảo vệ RAM & tránh lag),
            // đồng thời gộp luôn 0–4 ảnh góc PNG tĩnh vào clip chuẩn hóa để Bước 4 không phải lọc lại hàng triệu frame.
            Directory.CreateDirectory(clipsFolder);
            var videoCount = options.Videos.Count;
            var videoClips = new List<string>(videoCount);
            var clipDurations = new List<double>(videoCount);
            var accumulatedDuration = 0.0;

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
                var title = $"Chuẩn hóa video chất lượng cao {i + 1}/{videoCount} ({PlaybackSpeeds.Format(video.PlaybackSpeed)}): {Path.GetFileName(video.FilePath)}";
                var basePct = 20.0 + 35.0 * i / videoCount;
                var stepPct = 35.0 / videoCount;
                progress.Report(new RenderProgress(title, basePct));

                var hasAudio = !options.MuteVideoAudio &&
                    await _probe.HasAudioStreamAsync(video.FilePath, cancellationToken);
                var args = BuildNormalizeArguments(options, video, video.FilePath,
                    clipFile, expectedDuration, hasAudio);
                await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                    line => ReportFfmpegProgress(line, expectedDuration, p =>
                        progress.Report(new RenderProgress(title, basePct + stepPct * p))),
                    cancellationToken, workdir);

                var actualClipDuration = await _probe.GetDurationSecondsAsync(clipFile, cancellationToken);
                clipDurations.Add(actualClipDuration);
                videoClips.Add(clipFile);
                accumulatedDuration += actualClipDuration;
            }

            var playlistDuration = clipDurations.Sum();
            var videoList = Path.Combine(workdir, "playlist.ffconcat");
            await WriteConcatListAsync(videoList, "clips", videoClips, cancellationToken);

            // Step 4: Lặp danh sách video đã chuẩn hóa theo đúng tổng thời lượng audio MP3.
            var cycles = Math.Max(1, (int)Math.Ceiling(audioDuration / Math.Max(0.1, playlistDuration)));
            const double startPct = 55.0;
            const double spanPct = 45.0;
            var finalTitle = $"Xuất 1 MP4 | Audio {TimeSpan.FromSeconds(audioDuration).ToString("c", CultureInfo.InvariantCulture)} | " +
                             $"Playlist video {TimeSpan.FromSeconds(playlistDuration).ToString("c", CultureInfo.InvariantCulture)} | " +
                             $"{cycles} lượt phát";
            progress.Report(new RenderProgress(finalTitle, startPct));
            var outputFile = GetUniqueOutputFile(fullOutputDir, "LangKinhHuKhong_Full", options.ResolutionName,
                options.Subtitles is { SrtFiles.Count: > 0, SaveSidecarSrt: true });
            var temporaryOutput = Path.Combine(workdir, "final_render.mp4");
            var finalArgs = BuildOutputArguments(options, videoList, joinedAudio, temporaryOutput, audioDuration);
            await ProcessRunner.RunAsync(tools.Ffmpeg, finalArgs,
                line => ReportFfmpegProgress(line, audioDuration, p =>
                    progress.Report(new RenderProgress(finalTitle, startPct + spanPct * p))),
                cancellationToken, workdir);

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

    private static async Task WriteConcatListAsync(string file, string relativeDirectory,
        IReadOnlyList<string> clips, CancellationToken ct)
    {
        var content = new StringBuilder("ffconcat version 1.0\n");
        foreach (var clip in clips)
            content.Append("file '").Append(relativeDirectory).Append('/')
                .Append(Path.GetFileName(clip)).Append("'\n");
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

    private static string BuildNormalizeFilter(RenderOptions settings, double playbackSpeed) =>
        $"setpts=(PTS-STARTPTS)/{playbackSpeed.ToString("0.##", CultureInfo.InvariantCulture)}," +
        $"fps={settings.FrameRate}," +
        $"scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease:flags=lanczos," +
        $"pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2," +
        "setsar=1,format=yuv420p";

    private static List<string> BuildNormalizeArguments(
        RenderOptions settings, VideoClipOption video, string sourceFile, string clipFile,
        double expectedDuration, bool hasSourceAudio)
    {
        var cpuThreads = GetSafeCpuThreadCount().ToString(CultureInfo.InvariantCulture);
        var duration = expectedDuration.ToString("F6", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cpuThreads, "-filter_threads", "1", "-filter_complex_threads", "1",
            "-stats_period", "1.0", "-progress", "pipe:1",
            "-thread_queue_size", "32", "-i", sourceFile
        };
        if (!settings.MuteVideoAudio && !hasSourceAudio)
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);

        var overlayStartInputIndex = (!settings.MuteVideoAudio && !hasSourceAudio) ? 2 : 1;
        foreach (var overlay in settings.Overlays)
            args.AddRange(["-i", overlay.ImagePath]);

        var baseVideoFilter = BuildNormalizeFilter(settings, video.PlaybackSpeed) +
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

        // Chế độ ổn định (libx264): ưu tiên chất lượng hình ảnh cao (CRF 18, preset medium, profile high)
        // đồng thời giới hạn rc_lookahead / sync_lookahead để giữ mức RAM cực thấp, không gây lag máy.
        if (settings.Encoder == VideoEncoder.Nvenc)
        {
            args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0"]);
        }
        else
        {
            args.AddRange([
                "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-profile:v", "high",
                "-x264-params", $"threads={cpuThreads}:lookahead_threads=1:rc_lookahead=8:sync_lookahead=1:ref=2:bframes=2"
            ]);
        }

        args.AddRange([
            "-pix_fmt", "yuv420p", "-r", settings.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-video_track_timescale", "30000"
        ]);
        if (!settings.MuteVideoAudio)
            args.AddRange(["-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-t", duration, "-movflags", "+faststart", clipFile]);
        return args;
    }

    private static List<string> BuildOutputArguments(
        RenderOptions settings, string videoListPath, string joinedAudio, string outputFile,
        double audioDuration)
    {
        var cpuThreads = GetSafeCpuThreadCount().ToString(CultureInfo.InvariantCulture);
        var hasBurnInSubtitles = settings.Subtitles is { SrtFiles.Count: > 0, BurnIntoVideo: true };

        var args = new List<string>
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cpuThreads, "-filter_threads", "1", "-filter_complex_threads", "1",
            "-stats_period", "1.0", "-progress", "pipe:1",
            "-thread_queue_size", "16",
            "-stream_loop", "-1", // lặp danh sách clip đã chuẩn hóa theo đúng tổng thời lượng audio
            "-safe", "0", "-f", "concat", "-i", videoListPath,
            "-thread_queue_size", "16", "-i", joinedAudio
        };

        if (!hasBurnInSubtitles && settings.MuteVideoAudio)
        {
            // Không chèn phụ đề cứng & tắt âm gốc: các clip trong playlist đã có sẵn ảnh góc PNG và chuẩn H.264 CRF 18,
            // nên sao chép luồng trực tiếp (-c:v copy -c:a copy) => tốn < 30MB RAM, không tái mã hóa, giữ 100% chất lượng gốc!
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
            // Có chèn phụ đề cứng (BurnIntoVideo): phụ đề được vẽ lên lớp trên cùng (trên cả 4 ảnh góc PNG đã gộp ở Bước 3).
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

            if (settings.Encoder == VideoEncoder.Nvenc)
            {
                args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0"]);
            }
            else
            {
                args.AddRange([
                    "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-profile:v", "high",
                    "-x264-params", $"threads={cpuThreads}:lookahead_threads=1:rc_lookahead=8:sync_lookahead=1:ref=2:bframes=2"
                ]);
            }

            if (settings.MuteVideoAudio)
                args.AddRange(["-c:a", "copy"]);
            else
                args.AddRange(["-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"]);

            args.AddRange(["-pix_fmt", "yuv420p", "-r", settings.FrameRate.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange([
            "-t", audioDuration.ToString("F6", CultureInfo.InvariantCulture),
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-movflags", "+faststart", outputFile
        ]);
        return args;
    }

    private static void AppendOverlayStages(List<string> filters, RenderOptions settings, int startInputIndex)
    {
        for (var i = 0; i < settings.Overlays.Count; i++)
        {
            var overlay = settings.Overlays[i];
            var maxWidth = Math.Max(16, settings.Width * Math.Clamp(overlay.WidthPercent, 1, 100) / 100);
            var maxHeight = Math.Max(16, settings.Height * Math.Clamp(overlay.HeightPercent, 1, 100) / 100);
            var offsetX = Math.Max(0, settings.Width * Math.Clamp(overlay.OffsetXPercent, 0, 100) / 100);
            var offsetY = Math.Max(0, settings.Height * Math.Clamp(overlay.OffsetYPercent, 0, 100) / 100);
            // Escape filtergraph commas (\,) or FFmpeg treats min/max arguments as separate filters.
            // Clamp after scaling so overlays never leave the rendered image.
            var x = overlay.Corner is Corner.TopLeft or Corner.BottomLeft
                ? $"min({offsetX}\\,W-w)"
                : $"max(0\\,W-w-{offsetX})";
            var y = overlay.Corner is Corner.TopLeft or Corner.TopRight
                ? $"min({offsetY}\\,H-h)"
                : $"max(0\\,H-h-{offsetY})";
            filters.Add($"[{startInputIndex + i}:v]scale={maxWidth}:{maxHeight}:" +
                        "force_original_aspect_ratio=decrease:flags=lanczos," +
                        $"format=rgba[logo{i}]");
            filters.Add($"[v{i}][logo{i}]overlay=x={x}:y={y}:" +
                        $"eof_action=repeat:format=auto[v{i + 1}]");
        }
    }

    private async Task<VideoEncoder> ResolveEncoderAsync(VideoEncoder requested, CancellationToken cancellationToken, string workdir)
    {
        if (requested != VideoEncoder.Nvenc) return VideoEncoder.X264;
        try
        {
            var probeArgs = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=0.1",
                "-frames:v", "1", "-c:v", "h264_nvenc", "-f", "null", "-"
            };
            await ProcessRunner.RunAsync(tools.Ffmpeg, probeArgs, null, cancellationToken, workdir);
            return VideoEncoder.Nvenc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VideoEncoder.X264;
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
