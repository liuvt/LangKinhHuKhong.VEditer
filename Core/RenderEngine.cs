using System.Globalization;
using System.Text;

namespace LangKinhHuKhong.Editer.Core;

/// <summary>
/// v1.2: ALL MP3 tracks => ONE continuous audio track; ALL videos => ONE playlist;
/// repeat the video playlist only as needed, then render EXACTLY ONE MP4.
/// No MP3 is played in parallel and no separate MP4 is created per MP3.
/// </summary>
public sealed class RenderEngine(FfmpegTools tools)
{
    private readonly MediaProbe _probe = new(tools);

    public async Task<string> RenderAsync(
        RenderOptions options,
        IProgress<RenderProgress> progress,
        CancellationToken cancellationToken)
    {
        Validate(options);
        Directory.CreateDirectory(options.OutputDirectory);

        var tempRoot = Path.Combine(Path.GetTempPath(), "LangKinhHuKhong.Editer");
        CleanupStaleTempDirectories(tempRoot);
        var workdir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var clipsFolder = Path.Combine(workdir, "clips");
        var audioFolder = Path.Combine(workdir, "audio");
        Directory.CreateDirectory(clipsFolder);
        Directory.CreateDirectory(audioFolder);

        try
        {
            var effectiveEncoder = await ResolveEncoderAsync(options.Encoder, cancellationToken);
            if (options != options with { Encoder = effectiveEncoder })
                options = options with { Encoder = effectiveEncoder };
            // Step 1: normalize every MP3 to a common, lossless intermediate format in parallel.
            // This makes concatenation safe with different sample rates, bitrates,
            // channel layouts, metadata and VBR MP3 files. MP3 order is strictly preserved.
            var mp3Count = options.Mp3Files.Count;
            var audioClips = new string[mp3Count];
            var normalizedAudioDurations = new double[mp3Count];
            var mp3Progress = new double[mp3Count];
            var audioParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);
            var progressLock = new object();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, mp3Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = audioParallelism,
                    CancellationToken = cancellationToken
                },
                async (i, ct) =>
                {
                    var input = options.Mp3Files[i];
                    var seconds = await _probe.GetDurationSecondsAsync(input, ct);
                    var output = Path.Combine(audioFolder, $"{i + 1:D5}.flac");
                    var title = $"Chuẩn hóa đa luồng MP3 {i + 1}/{mp3Count}: {Path.GetFileName(input)}";
                    var args = new List<string>
                    {
                        "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                        "-threads", "0", "-stats_period", "0.5", "-progress", "pipe:1",
                        "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
                        "-af", "aresample=48000:async=1:first_pts=0,aformat=sample_fmts=s16:channel_layouts=stereo",
                        "-c:a", "flac", "-compression_level", "1", output
                    };
                    await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                        line => ReportFfmpegProgress(line, seconds, p =>
                        {
                            double avg;
                            lock (progressLock)
                            {
                                mp3Progress[i] = p;
                                avg = mp3Progress.Sum() / mp3Count;
                            }
                            progress.Report(new RenderProgress(title, 15.0 * avg));
                        }), ct);
                    audioClips[i] = output;
                    normalizedAudioDurations[i] = await _probe.GetDurationSecondsAsync(output, ct);
                    lock (progressLock) mp3Progress[i] = 1.0;
                });

            var estimatedAudioDuration = normalizedAudioDurations.Sum();

            // Step 2: encode the concatenated audio just once. The final AUDIO
            // duration (rather than the sum of approximate MP3 metadata) is the
            // authoritative length used when rendering the one output video.
            var audioList = Path.Combine(workdir, "audio.ffconcat");
            await WriteConcatListAsync(audioList, "audio", audioClips, cancellationToken);
            var joinedAudio = Path.Combine(workdir, "joined_audio.m4a");
            const string joinTitle = "Ghép tất cả MP3 thành một audio liên tục";
            progress.Report(new RenderProgress(joinTitle, 15));
            var joinArgs = new List<string>
            {
                "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                "-threads", "0", "-stats_period", "0.5", "-progress", "pipe:1",
                "-safe", "0", "-f", "concat", "-i", audioList,
                "-map", "0:a:0", "-vn", "-sn", "-dn",
                "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart", joinedAudio
            };
            await ProcessRunner.RunAsync(tools.Ffmpeg, joinArgs,
                line => ReportFfmpegProgress(line, estimatedAudioDuration, p =>
                    progress.Report(new RenderProgress(joinTitle, 15.0 + 5.0 * p))),
                cancellationToken);
            var audioDuration = await _probe.GetDurationSecondsAsync(joinedAudio, cancellationToken);

            // Subtitles follow the concatenated MP3 timeline, not repeated video clips.
            // A global SRT stays absolute; per-MP3 SRTs are shifted by actual decoded
            // FLAC durations, with a user-configurable global +/- millisecond offset.
            var mergedSubtitleFile = Path.Combine(workdir, "joined_subtitles.srt");
            if (options.Subtitles is { SrtFiles.Count: > 0 } sub)
            {
                progress.Report(new RenderProgress("Đồng bộ mốc SRT theo audio đã nối...", 19));
                var cues = SubtitleComposer.Merge(sub, normalizedAudioDurations, audioDuration);
                await SubtitleComposer.WriteSrtAsync(mergedSubtitleFile, cues, cancellationToken);
            }


            // Step 3: normalize each video using ITS OWN playback speed and the
            // selected export resolution/FPS in parallel; retain original sequence.
            var videoCount = options.Videos.Count;
            var videoClips = new string[videoCount];
            var clipDurations = new double[videoCount];
            var videoProgress = new double[videoCount];
            var videoParallelism = options.Encoder == VideoEncoder.Nvenc
                ? Math.Min(2, Math.Max(1, videoCount))
                : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, videoCount),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = videoParallelism,
                    CancellationToken = cancellationToken
                },
                async (i, ct) =>
                {
                    var video = options.Videos[i];
                    var sourceDuration = await _probe.GetDurationSecondsAsync(video.FilePath, ct);
                    var expectedDuration = sourceDuration / video.PlaybackSpeed;
                    var clipFile = Path.Combine(clipsFolder, $"{i + 1:D5}.mp4");
                    var title = $"Chuẩn hóa video {i + 1}/{videoCount} ({PlaybackSpeeds.Format(video.PlaybackSpeed)}): {Path.GetFileName(video.FilePath)}";
                    var hasAudio = !options.MuteVideoAudio &&
                        await _probe.HasAudioStreamAsync(video.FilePath, ct);
                    var args = BuildNormalizeArguments(options, video, video.FilePath,
                        clipFile, expectedDuration, hasAudio);
                    await ProcessRunner.RunAsync(tools.Ffmpeg, args,
                        line => ReportFfmpegProgress(line, expectedDuration, p =>
                        {
                            double avg;
                            lock (progressLock)
                            {
                                videoProgress[i] = p;
                                avg = videoProgress.Sum() / videoCount;
                            }
                            progress.Report(new RenderProgress(title, 20.0 + 35.0 * avg));
                        }),
                        ct);
                    clipDurations[i] = await _probe.GetDurationSecondsAsync(clipFile, ct);
                    videoClips[i] = clipFile;
                    lock (progressLock) videoProgress[i] = 1.0;
                });

            var playlistDuration = clipDurations.Sum();

            // Step 4: loop the ENTIRE ordered video playlist when necessary.
            // Output always ends at the joined audio duration, never per input MP3.
            var videoList = Path.Combine(workdir, "playlist.ffconcat");
            await WriteConcatListAsync(videoList, "clips", videoClips, cancellationToken);
            var cycles = Math.Max(1, (int)Math.Ceiling(audioDuration / playlistDuration));
            var finalTitle = $"Xuất 1 MP4 | Audio {TimeSpan.FromSeconds(audioDuration).ToString("c", CultureInfo.InvariantCulture)} | " +
                             $"Playlist video {TimeSpan.FromSeconds(playlistDuration).ToString("c", CultureInfo.InvariantCulture)} | " +
                             $"{cycles} lượt phát (lượt cuối có thể bị cắt)";
            progress.Report(new RenderProgress(finalTitle, 55));
            var outputFile = GetUniqueOutputFile(options.OutputDirectory, "LangKinhHuKhong_Full", options.ResolutionName,
                options.Subtitles is { SrtFiles.Count: > 0, SaveSidecarSrt: true });
            var temporaryOutput = Path.Combine(workdir, "final_render.mp4");
            var finalArgs = BuildOutputArguments(options, videoList, joinedAudio, temporaryOutput, audioDuration);
            await ProcessRunner.RunAsync(tools.Ffmpeg, finalArgs,
                line => ReportFfmpegProgress(line, audioDuration, p =>
                    progress.Report(new RenderProgress(finalTitle, 55.0 + 45.0 * p))),
                cancellationToken, workdir);

            // A failed/canceled job must not leave a partial MP4 in output folder.
            cancellationToken.ThrowIfCancellationRequested();
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
                    try { File.Delete(sidecar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            progress.Report(new RenderProgress("Hoàn thành 1 video MP4 duy nhất.", 100));
            return outputFile;
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
        var duration = expectedDuration.ToString("F6", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-threads", "0", "-filter_threads", "0",
            "-stats_period", "0.5", "-progress", "pipe:1",
            "-thread_queue_size", "4096", "-i", sourceFile
        };
        if (!settings.MuteVideoAudio && !hasSourceAudio)
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);

        var videoFilter = BuildNormalizeFilter(settings, video.PlaybackSpeed) +
            ",tpad=stop_mode=clone:stop_duration=0.10," +
            $"trim=duration={duration},setpts=PTS-STARTPTS";
        if (settings.MuteVideoAudio)
            args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", videoFilter]);
        else
        {
            var audioStart = hasSourceAudio
                ? $"[0:a:0]{PlaybackSpeeds.AudioTempo(video.PlaybackSpeed)},aresample=48000:async=1:first_pts=0,"
                : "[1:a]aresample=48000:async=1:first_pts=0,";
            var graph = $"[0:v]{videoFilter}[vnorm];" + audioStart +
                        $"aformat=channel_layouts=stereo,apad,atrim=duration={duration},asetpts=PTS-STARTPTS[anorm]";
            args.AddRange(["-filter_complex", graph, "-map", "[vnorm]", "-map", "[anorm]"]);
        }
        if (settings.Encoder == VideoEncoder.Nvenc)
            args.AddRange(["-c:v", "h264_nvenc", "-preset", "p2", "-rc", "vbr", "-cq", "19", "-b:v", "0"]);
        else
            args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "18"]);
        args.AddRange([
            "-pix_fmt", "yuv420p", "-r", settings.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-video_track_timescale", "30000"
        ]);
        if (!settings.MuteVideoAudio)
            args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-t", duration, "-movflags", "+faststart", clipFile]);
        return args;
    }

    private static List<string> BuildOutputArguments(
        RenderOptions settings, string playlistPath, string joinedAudio, string outputFile,
        double audioDuration)
    {
        var args = new List<string>
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-threads", "0", "-filter_threads", "0", "-filter_complex_threads", "0",
            "-stats_period", "0.5", "-progress", "pipe:1",
            "-thread_queue_size", "4096",
            "-stream_loop", "-1", // always repeat the entire playlist if audio is longer
            "-safe", "0", "-f", "concat", "-i", playlistPath,
            "-thread_queue_size", "4096", "-i", joinedAudio
        };
        // Nạp mỗi ảnh tĩnh 1 frame duy nhất; bộ lọc overlay với eof_action=repeat sẽ tự giữ frame RGBA
        // đã scale bằng Lanczos xuyên suốt video thay vì phải giải mã & scale lại 30 lần/giây.
        foreach (var overlay in settings.Overlays)
            args.AddRange(["-i", overlay.ImagePath]);

        var filter = BuildOverlayFilter(settings);
        if (!settings.MuteVideoAudio)
        {
            filter += ";[0:a:0]aresample=async=1:first_pts=0,volume=0.20[gameaudio]" +
                      ";[1:a:0]aresample=async=1:first_pts=0,apad[voice]" +
                      ";[voice][gameaudio]amix=inputs=2:duration=longest:" +
                      "dropout_transition=0:normalize=0,aresample=async=1[aout]";
        }
        args.AddRange(["-filter_complex", filter, "-map", "[vout]", "-map",
            settings.MuteVideoAudio ? "1:a:0" : "[aout]"]);

        if (settings.Encoder == VideoEncoder.Nvenc)
            args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "21", "-b:v", "0"]);
        else
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20"]);

        // Audio already joined/encoded once; directly copy it when source is muted,
        // preventing unnecessary generational loss and MP3 transition artifacts.
        if (settings.MuteVideoAudio)
            args.AddRange(["-c:a", "copy"]);
        else
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000"]);

        args.AddRange([
            "-pix_fmt", "yuv420p", "-r", settings.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-t", audioDuration.ToString("F6", CultureInfo.InvariantCulture),
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-movflags", "+faststart", outputFile
        ]);
        return args;
    }

    private static string BuildOverlayFilter(RenderOptions settings)
    {
        var initial = "[0:v]setpts=PTS-STARTPTS";
        var filters = new List<string> { initial + "[v0]" };
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
            filters.Add($"[{i + 2}:v]scale={maxWidth}:{maxHeight}:" +
                        "force_original_aspect_ratio=decrease:flags=lanczos," +
                        $"format=rgba[logo{i}]");
            filters.Add($"[v{i}][logo{i}]overlay=x={x}:y={y}:" +
                        $"eof_action=repeat:format=auto[v{i + 1}]");
        }
        var lastVideo = $"[v{settings.Overlays.Count}]";
        // v2: subtitles/text must be the topmost layer, above all PNG overlays.
        if (settings.Subtitles is { SrtFiles.Count: > 0, BurnIntoVideo: true } sub)
        {
            var escaped = SubtitleComposer.BuildBurnInFilter(sub);
            filters.Add($"{lastVideo}{escaped}[vsub]");
            lastVideo = "[vsub]";
        }
        filters.Add($"{lastVideo}format=yuv420p[vout]");
        return string.Join(';', filters);
    }

    private async Task<VideoEncoder> ResolveEncoderAsync(VideoEncoder requested, CancellationToken cancellationToken)
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
            await ProcessRunner.RunAsync(tools.Ffmpeg, probeArgs, null, cancellationToken);
            return VideoEncoder.Nvenc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tự động chuyển về libx264 (CPU) nếu máy không có GPU NVIDIA hoặc driver chưa hỗ trợ NVENC.
            return VideoEncoder.X264;
        }
    }

    private static void CleanupStaleTempDirectories(string tempRoot)
    {
        try
        {
            if (!Directory.Exists(tempRoot)) return;
            var cutoff = DateTime.UtcNow.AddHours(-12);
            foreach (var dir in Directory.EnumerateDirectories(tempRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < cutoff && Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ReportFfmpegProgress(string line, double totalSeconds, Action<double> callback)
    {
        if (totalSeconds <= 0) return;
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            if (long.TryParse(line.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                callback(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 1));
            return;
        }
        if (!line.StartsWith("out_time=", StringComparison.Ordinal)) return;
        var raw = line.AsSpan(9).Trim();
        if (raw.Length > 0 && raw[0] != '-' && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var time))
            callback(Math.Clamp(time.TotalSeconds / totalSeconds, 0, 1));
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
