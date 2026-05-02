using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;

namespace Lidarr.Plugin.Diffractarr
{
    public class ProcessorSettings
    {
        public bool FastCopy { get; set; }
        public bool DeleteSource { get; set; }
        public bool CleanOnError { get; set; } = true;
    }

    public partial class ProcessorService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly SemaphoreSlim FfmpegLock = new (1);

        private static readonly HashSet<string> SupportedExtensions = new (StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".wav", ".ape", ".m4a", ".wv",
        };

        private static readonly HashSet<string> TranscodeExtensions = new (StringComparer.OrdinalIgnoreCase)
        {
            ".ape",
        };

        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly ProcessorSettings _settings;

        public ProcessorService(ICompletedDownloadService completedDownloadService, ProcessorSettings settings)
        {
            _completedDownloadService = completedDownloadService;
            _settings = settings;
        }

        public void ProcessDownload(TrackedDownload trackedDownload)
        {
            var downloadPath = trackedDownload.DownloadItem.OutputPath.FullPath;

            var cueSheets = new List<CueSheet>();
            var cueFiles = Directory.GetFiles(downloadPath, "*.cue", SearchOption.AllDirectories);
            foreach (var cuePath in cueFiles)
            {
                Logger.Info("Parsing cue sheet: {0}", cuePath);
                var cueSheet = CueSheetService.Parse(cuePath);
                cueSheets.Add(cueSheet);
            }

            var baseDir = Path.Combine(downloadPath, "diffractarr");

            bool import = false;
            foreach (var album in cueSheets.GroupBy(cs => (cs.Artist, cs.Album)))
            {
                import |= ProcessAlbum(album.ToList(), baseDir);
            }

            if (import)
            {
                // Hide originals so Lidarr doesn't try to import them again
                var hiddenFiles = new List<(string original, string backup)>();
                foreach (var cueSheet in cueSheets)
                {
                    foreach (var audioFile in cueSheet.AudioFiles)
                    {
                        var backup = audioFile.Path + ".bak";
                        if (File.Exists(audioFile.Path))
                        {
                            Logger.Debug("Backing up audio file: {0}", audioFile.Path);
                            File.Move(audioFile.Path, backup);
                            hiddenFiles.Add((audioFile.Path, backup));
                        }
                    }
                }

                try
                {
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    _completedDownloadService.Import(trackedDownload);
                }
                finally
                {
                    // Restore originals regardless of import success/failure
                    foreach (var (original, backup) in hiddenFiles)
                    {
                        if (File.Exists(backup))
                        {
                            Logger.Debug("Restoring audio file: {0}", original);
                            File.Move(backup, original);
                        }
                    }
                }
            }

            if (Directory.Exists(baseDir))
            {
                PruneEmptyDirs(baseDir);
            }
        }

        internal static bool SplitAudioFile(AudioFile file, string outputDir, bool copy)
        {
            var audioPath = file.Path;

            if (!File.Exists(audioPath))
            {
                Logger.Warn("Missing audio file: {0}", audioPath);
                return false;
            }

            if (!SupportedExtensions.Contains(Path.GetExtension(audioPath).ToLower()))
            {
                Logger.Warn("Unsupported extension: {0}", audioPath);
                return false;
            }

            Logger.Info("Splitting audio file: {0}", audioPath);

            var ext = Path.GetExtension(audioPath);
            string? transcodePath = null;

            try
            {
                if (TranscodeExtensions.Contains(ext))
                {
                    transcodePath = Path.Combine(
                        outputDir,
                        Path.GetFileNameWithoutExtension(audioPath) + ".flac");
                    AudioService.TranscodeToFlac(audioPath, transcodePath);
                    audioPath = transcodePath;
                    ext = ".flac";
                }

                int sampleRate = 0;
                long totalSamples = 0;
                if (copy && ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    (sampleRate, totalSamples) = AudioService.ReadFlacSampleInfo(audioPath);
                }

                foreach (var track in file.Tracks)
                {
                    var title = SanitizeFilename(track.Title);
                    var trackPath = Path.Combine(outputDir, $"{track.Number:D2}. {title}{ext}");

                    AudioService.ExtractTrack(audioPath, trackPath, track, copy: copy);

                    if (copy && ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                    {
                        var samples = track.Duration.HasValue
                            ? (long)Math.Round(track.Duration.Value * sampleRate)
                            : totalSamples - (long)Math.Round(track.Start * sampleRate);
                        AudioService.FixFlacStreamInfo(trackPath, samples);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to split audio file: {0}", file.Path);
                return false;
            }
            finally
            {
                if (transcodePath != null && File.Exists(transcodePath))
                {
                    Logger.Info("Deleting transcode: {0}", transcodePath);
                    File.Delete(transcodePath);
                }
            }

            return true;
        }

        internal static string SanitizeFilename(string name)
        {
            name = InvalidCharsRegex().Replace(name, "");
            name = WhitespaceRegex().Replace(name, " ").Trim();
            return name;
        }

        private bool ProcessAlbum(List<CueSheet> cueSheets, string baseDir)
        {
            var artist = cueSheets[0].Artist;
            var album = cueSheets[0].Album;
            if (!cueSheets.Any(cs => cs.AudioFiles.Any(af => af.Tracks.Count > 1)))
            {
                Logger.Info("No multitrack audio files for album: {0} - {1}", artist, album);
                return false;
            }

            var albumDir = Path.Combine(baseDir, SanitizeFilename(artist), SanitizeFilename(album));
            Directory.CreateDirectory(albumDir);
            foreach (var cueSheet in cueSheets)
            {
                foreach (var audioFile in cueSheet.AudioFiles)
                {
                    FfmpegLock.Wait();
                    try
                    {
                        if (!SplitAudioFile(audioFile, albumDir, _settings.FastCopy))
                        {
                            if (_settings.CleanOnError)
                            {
                                Logger.Info("Deleting album directory: {0}", albumDir);
                                Directory.Delete(albumDir, recursive: true);
                            }

                            return false;
                        }
                    }
                    finally
                    {
                        FfmpegLock.Release();
                    }
                }
            }

            if (_settings.DeleteSource)
            {
                foreach (var cueSheet in cueSheets)
                {
                    foreach (var file in cueSheet.AudioFiles)
                    {
                        Logger.Info("Deleting audio file: {0}", file.Path);
                        File.Delete(file.Path);
                    }

                    Logger.Info("Deleting cue sheet: {0}", cueSheet.Path);
                    File.Delete(cueSheet.Path);
                }
            }

            return true;
        }

        private static void PruneEmptyDirs(string path)
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                PruneEmptyDirs(dir);
            }

            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }

        [GeneratedRegex(@"[/\\:*?""<>|]")]
        private static partial Regex InvalidCharsRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
    }
}
