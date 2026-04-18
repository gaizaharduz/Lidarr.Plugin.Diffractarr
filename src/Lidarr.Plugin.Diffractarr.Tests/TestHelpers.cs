using System.Diagnostics;

namespace Lidarr.Plugin.Diffractarr.Tests;

public record struct TrackEntry(int num, string title, string index);

public static class TestHelpers
{
    private static readonly Dictionary<string, string> Codecs = new ()
    {
        { ".m4a", "alac" },
        { ".wv", "wavpack" },
    };

    public static string MakeAudioFile(string path, int duration = 9)
    {
        var args = $"-nostdin -hide_banner -loglevel error " +
                   $"-f lavfi -i sine=frequency=440:duration={duration} ";

        var ext = Path.GetExtension(path);
        if (Codecs.TryGetValue(ext, out var codec))
        {
            args += $"-c:a {codec} ";
        }

        args += $"-y \"{path}\"";
        RunFfmpeg(args);
        return path;
    }

    public static string MakeCueSheet(
        string path,
        string audioFile = "album.flac",
        List<TrackEntry>? tracks = null)
    {
        var entries = tracks ?? new List<TrackEntry>
        {
            new TrackEntry(1, "Track One", "00:00:00"),
            new TrackEntry(2, "Track Two", "00:03:00"),
            new TrackEntry(3, "Track Three", "00:06:00"),
        };

        var lines = new List<string>
        {
            "PERFORMER \"Test Artist\"",
            "TITLE \"Test Album\"",
            $"FILE \"{audioFile}\" WAVE",
        };

        foreach (var track in entries)
        {
            lines.Add($"  TRACK {track.num:D2} AUDIO");
            lines.Add($"    TITLE \"{track.title}\"");
            lines.Add($"    INDEX 01 {track.index}");
        }

        File.WriteAllText(path, string.Join("\n", lines));
        return path;
    }

    public static string MakeDownload(
        string dir,
        int duration = 9,
        List<TrackEntry>? tracks = null)
    {
        Directory.CreateDirectory(dir);
        MakeAudioFile(Path.Combine(dir, "album.flac"), duration);
        MakeCueSheet(Path.Combine(dir, "album.cue"), tracks: tracks);
        return dir;
    }

    public static long GetTotalSamples(string path)
    {
        var args = $"-v error -show_entries stream=duration_ts " +
                   $"-of default=noprint_wrappers=1:nokey=1 \"{path}\"";
        var psi = new ProcessStartInfo("ffprobe", args);
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        using var proc = Process.Start(psi) !;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return long.Parse(output);
    }

    private static void RunFfmpeg(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg", args);
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var proc = Process.Start(psi) !;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            throw new Exception($"ffmpeg failed: {err}");
        }
    }
}
