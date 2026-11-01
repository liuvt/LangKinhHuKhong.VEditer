using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LangKinhHuKhong.Editer.Core;

public enum SubtitleImportMode
{
    GlobalTimeline,
    OneSrtPerMp3
}

/// <summary>Each SRT matches the source MP3 in the same list position (or one global SRT).</summary>
public sealed record SubtitleOptions(
    IReadOnlyList<string> SrtFiles,
    SubtitleImportMode ImportMode,
    bool BurnIntoVideo,
    bool SaveSidecarSrt,
    int OffsetMilliseconds,
    string FontName,
    int FontSize);

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

public static partial class SubtitleComposer
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static IReadOnlyList<SubtitleCue> Parse(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Không tìm thấy phụ đề: " + file, file);
        // UTF-8 (including BOM) and UTF-16 BOM are both decoded correctly by StreamReader.
        // TTSS should export UTF-8 for full Vietnamese font fidelity.
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string text;
        try { text = reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"SRT không phải UTF-8/UTF-16 hợp lệ: {Path.GetFileName(file)}. Hãy lưu lại ở UTF-8 để không lỗi dấu tiếng Việt.");
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\uFEFF', '\n', ' ', '\t');
        var cues = new List<SubtitleCue>();
        foreach (var block in Regex.Split(text, @"\n[ \t]*\n+"))
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, x => x.Contains("-->", StringComparison.Ordinal));
            if (timing < 0) continue;
            var match = TimeLineRegex().Match(lines[timing]);
            if (!match.Success) continue;
            if (!TryParseClock(match.Groups["start"].Value, out var start) ||
                !TryParseClock(match.Groups["end"].Value, out var end) || end <= start) continue;
            var body = string.Join("\n", lines.Skip(timing + 1)).Trim();
            if (body.Length > 0) cues.Add(new SubtitleCue(start, end, body));
        }
        if (cues.Count == 0)
            throw new InvalidDataException($"SRT không có dòng phụ đề hợp lệ: {Path.GetFileName(file)}.");
        return cues.OrderBy(x => x.Start).ToArray();
    }

    [GeneratedRegex(@"^\s*(?<start>\d{1,5}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>\d{1,5}:\d{2}:\d{2}[,.]\d{1,3})(?:\s+.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeLineRegex();

    private static bool TryParseClock(string input, out TimeSpan time)
    {
        time = default;
        var p = input.Replace(',', '.').Split(':');
        if (p.Length != 3 || !long.TryParse(p[0], out var hours) ||
            !int.TryParse(p[1], out var minutes) || minutes is < 0 or > 59) return false;
        var sec = p[2].Split('.');
        if (sec.Length != 2 || !int.TryParse(sec[0], out var seconds) || seconds is < 0 or > 59 ||
            sec[1].Length is < 1 or > 3 || !int.TryParse(sec[1].PadRight(3, '0'), out var millis)) return false;
        try { time = TimeSpan.FromTicks(checked((hours * 3600000L + minutes * 60000L + seconds * 1000L + millis) * TimeSpan.TicksPerMillisecond)); return true; }
        catch (OverflowException) { return false; }
    }

    /// <summary>
    /// In per-MP3 mode each SRT starts at zero relative to its own MP3.
    /// Offsets use measured durations AFTER MP3s are resampled, avoiding drift
    /// from VBR MP3 metadata and from different input sample rates.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Merge(
        SubtitleOptions settings, IReadOnlyList<double> normalizedTrackSeconds, double joinedAudioSeconds)
    {
        if (settings.SrtFiles.Count == 0) return [];
        if (settings.ImportMode == SubtitleImportMode.GlobalTimeline && settings.SrtFiles.Count != 1)
            throw new ArgumentException("Chế độ Toàn bộ audio chỉ nhận 1 file SRT.");
        if (settings.ImportMode == SubtitleImportMode.OneSrtPerMp3 &&
            settings.SrtFiles.Count != normalizedTrackSeconds.Count)
            throw new ArgumentException("Chế độ Theo từng MP3 yêu cầu số file SRT bằng số MP3 và đúng thứ tự.");
        var result = new List<SubtitleCue>();
        var cumulative = 0.0;
        var offsetSeconds = settings.OffsetMilliseconds / 1000.0;
        for (var i = 0; i < settings.SrtFiles.Count; i++)
        {
            var origin = settings.ImportMode == SubtitleImportMode.OneSrtPerMp3 ? cumulative : 0;
            var trackEnd = cumulative + normalizedTrackSeconds[i] + Math.Max(0, offsetSeconds);
            var maxEnd = settings.ImportMode == SubtitleImportMode.OneSrtPerMp3
                ? Math.Min(joinedAudioSeconds, trackEnd)
                : joinedAudioSeconds;
            foreach (var cue in Parse(settings.SrtFiles[i]))
            {
                var start = origin + cue.Start.TotalSeconds + offsetSeconds;
                var end = origin + cue.End.TotalSeconds + offsetSeconds;
                start = Math.Max(0, start);
                end = Math.Min(maxEnd, end);
                if (end <= start || start >= joinedAudioSeconds) continue;
                result.Add(new SubtitleCue(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), cue.Text));
            }
            if (settings.ImportMode == SubtitleImportMode.OneSrtPerMp3)
                cumulative += normalizedTrackSeconds[i];
        }
        if (result.Count == 0) throw new InvalidDataException("Không có phụ đề nào nằm trong thời lượng audio sau khi ghép.");
        return result.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
    }

    public static async Task WriteSrtAsync(string destination, IReadOnlyList<SubtitleCue> cues, CancellationToken token)
    {
        var result = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            result.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            result.Append(FormatTime(cues[i].Start)).Append(" --> ")
                .AppendLine(FormatTime(cues[i].End));
            result.AppendLine(cues[i].Text.Replace("\r", ""));
            result.AppendLine();
        }
        await File.WriteAllTextAsync(destination, result.ToString(), Utf8NoBom, token);
    }

    private static string FormatTime(TimeSpan t) =>
        $"{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    public static string BuildBurnInFilter(SubtitleOptions subtitle)
    {
        // Filename is relative to the FFmpeg process working directory, avoiding
        // Unicode and colon/backslash escaping in Windows profile paths.
        // Sanitize all FFmpeg filtergraph & ASS style delimiter characters to prevent filter injection.
        var rawFont = string.IsNullOrWhiteSpace(subtitle.FontName) ? "Arial" : subtitle.FontName;
        var safeFont = new string(rawFont.Where(c => !char.IsControl(c) && c is not ('\'' or '"' or ',' or ':' or ';' or '\\' or '/' or '[' or ']' or '=')).ToArray()).Trim();
        if (string.IsNullOrEmpty(safeFont)) safeFont = "Arial";
        var size = Math.Clamp(subtitle.FontSize, 14, 80);
        var style = $"FontName={safeFont},FontSize={size},PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2,Shadow=0,Alignment=2,MarginV=40";
        return $"subtitles=filename=joined_subtitles.srt:charenc=UTF-8:force_style='{style}'";
    }
}
