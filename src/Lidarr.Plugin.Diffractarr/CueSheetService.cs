using System.Text;
using NLog;
using UtfUnknown;

namespace Lidarr.Plugin.Diffractarr
{
    public class Track
    {
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public double Start { get; set; }
        public double? Duration { get; set; }
    }

    public class AudioFile
    {
        public string Path { get; set; } = "";
        public List<Track> Tracks { get; set; } = new ();
    }

    public class CueSheet
    {
        public string Path { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public List<AudioFile> AudioFiles { get; set; } = new ();
    }

    public static class CueSheetService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly HashSet<string> SupportedExtensions = new (StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".wav", ".ape", ".m4a", ".wv",
        };

        public static CueSheet Parse(string cuePath)
        {
            var bytes = File.ReadAllBytes(cuePath);

            // Strip all leading UTF-8 BOM sequences before charset detection
            while (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }

            var detected = CharsetDetector.DetectFromBytes(bytes);
            var encoding = detected.Detected?.Encoding ?? Encoding.UTF8;
            Logger.Debug("Detected encoding {0} for {1}", encoding.EncodingName, cuePath);

            var text = encoding.GetString(bytes);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

            var cueDir = Path.GetDirectoryName(cuePath) ?? ".";
            var cueSheet = new CueSheet { Path = cuePath };
            AudioFile? currentFile = null;
            Track? currentTrack = null;
            Track? previousTrack = null;

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                {
                    var performer = Unquote(trimmed[10..]);
                    if (currentTrack != null)
                    {
                        currentTrack.Artist = performer;
                    }
                    else
                    {
                        cueSheet.Artist = performer;
                    }
                }
                else if (trimmed.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                {
                    var title = Unquote(trimmed[6..]);
                    if (currentTrack != null)
                    {
                        currentTrack.Title = title;
                    }
                    else
                    {
                        cueSheet.Album = title;
                    }
                }
                else if (trimmed.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    currentFile = null;
                    currentTrack = null;
                    previousTrack = null;

                    var path = Path.Combine(cueDir, ParseFile(trimmed));
                    currentFile = new AudioFile { Path = ResolveAudioPath(path, cuePath) };
                    cueSheet.AudioFiles.Add(currentFile);
                }
                else if (trimmed.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                {
                    previousTrack = currentTrack;
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    currentTrack = new Track
                    {
                        Number = int.TryParse(parts[1], out var n) ? n : 0,
                        Artist = cueSheet.Artist,
                        Album = cueSheet.Album,
                    };
                    currentFile?.Tracks.Add(currentTrack);
                }
                else if (trimmed.StartsWith("INDEX 01 ", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentTrack != null)
                    {
                        currentTrack.Start = ParseTimestamp(trimmed[9..]);
                        if (previousTrack != null)
                        {
                            previousTrack.Duration = currentTrack.Start - previousTrack.Start;
                        }
                    }
                }
            }

            return cueSheet;
        }

        private static string ResolveAudioPath(string path, string cuePath)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var dir = Path.GetDirectoryName(cuePath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(path);
            var cueStem = Path.GetFileNameWithoutExtension(cuePath);

            foreach (var ext in SupportedExtensions)
            {
                var candidate = Path.Combine(dir, stem + ext);
                if (File.Exists(candidate))
                {
                    Logger.Debug("Resolved audio file path: {0}", candidate);
                    return candidate;
                }
            }

            foreach (var ext in SupportedExtensions)
            {
                var candidate = Path.Combine(dir, cueStem + ext);
                if (File.Exists(candidate))
                {
                    Logger.Debug("Resolved audio file path: {0}", candidate);
                    return candidate;
                }
            }

            return path;
        }

        private static double ParseTimestamp(string timestamp)
        {
            var parts = timestamp.Trim().Split(':');
            if (parts.Length != 3)
            {
                return 0;
            }

            int.TryParse(parts[0], out var minutes);
            int.TryParse(parts[1], out var seconds);
            int.TryParse(parts[2], out var frames);
            return (minutes * 60) + seconds + (frames / 75.0);
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return value[1..^1];
            }

            return value;
        }

        private static string ParseFile(string line)
        {
            // FILE "filename.flac" WAVE
            var start = line.IndexOf('"');
            var end = start >= 0 ? line.IndexOf('"', start + 1) : -1;
            if (start >= 0 && end > start)
            {
                var from = start + 1;
                return line[from..end];
            }

            // Unquoted: FILE filename.flac WAVE
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : "";
        }
    }
}
