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

        public ProcessorService(ICompletedDownloadService completedDownloadService)
        {
            _completedDownloadService = completedDownloadService;
        }

        public void ProcessDownload(TrackedDownload trackedDownload, ProcessorSettings settings)
        {
            var downloadPath = trackedDownload.DownloadItem.OutputPath.FullPath;

            var hasMultitrack = false;
            var cueSheets = new List<CueSheet>();
            var cueFiles = Directory.GetFiles(downloadPath, "*.cue", SearchOption.AllDirectories);
            foreach (var cuePath in cueFiles)
            {
                Logger.Info("Parsing cue sheet: {0}", cuePath);
                var cueSheet = CueSheetService.Parse(cuePath);
                cueSheets.Add(cueSheet);

                foreach (var file in cueSheet.AudioFiles)
                {
                    var ext = Path.GetExtension(file.Path);
                    if (!SupportedExtensions.Contains(ext))
                    {
                        Logger.Warn("Unsupported extension: {0}", file.Path);
                        return;
                    }

                    if (file.Tracks.Count > 1)
                    {
                        hasMultitrack = true;
                    }

                    Logger.Debug("  Audio file: {0}", file.Path);
                    foreach (var track in file.Tracks)
                    {
                        Logger.Debug("    Track: {0}", track);
                    }
                }
            }

            if (!hasMultitrack)
            {
                Logger.Info("No multitrack audio files in folder: {0}", downloadPath);
                return;
            }

            var baseDir = Path.Combine(downloadPath, "diffractarr");

            foreach (var cueSheet in cueSheets)
            {
                var artist = SanitizeFilename(cueSheet.Artist);
                var album = SanitizeFilename(cueSheet.Album);
                var outputDir = Path.Combine(baseDir, artist, album);
                var success = true;

                foreach (var file in cueSheet.AudioFiles)
                {
                    FfmpegLock.Wait();
                    try
                    {
                        success &= SplitAudioFile(file, outputDir, settings);
                    }
                    finally
                    {
                        FfmpegLock.Release();
                    }
                }

                if (settings.DeleteSource && success)
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

            // Hide originals so Lidarr doesn't re-import the unsplit files
            var hiddenFiles = new List<(string original, string backup)>();
            if (!settings.DeleteSource)
            {
                foreach (var cueSheet in cueSheets)
                {
                    foreach (var file in cueSheet.AudioFiles)
                    {
                        var backup = file.Path + ".bak";
                        Logger.Debug("Backing up file: {0}", file.Path);
                        File.Move(file.Path, backup);
                        hiddenFiles.Add((file.Path, backup));
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
                        Logger.Debug("Restoring file: {0}", original);
                        File.Move(backup, original);
                    }
                }
            }

            if (Directory.Exists(baseDir))
            {
                PruneEmptyDirs(baseDir);
            }
        }

        internal static bool SplitAudioFile(AudioFile file, string outputDir, ProcessorSettings settings)
        {
            var audioPath = file.Path;
            Logger.Info("Splitting audio file: {0}", audioPath);

            var ext = Path.GetExtension(audioPath);
            string? transcodePath = null;

            Directory.CreateDirectory(outputDir);

            var trackPaths = new List<string>();
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
                if (settings.FastCopy && ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    (sampleRate, totalSamples) = AudioService.ReadFlacSampleInfo(audioPath);
                }

                foreach (var track in file.Tracks)
                {
                    var title = SanitizeFilename(track.Title);
                    var trackPath = Path.Combine(outputDir, $"{track.Number:D2}. {title}{ext}");

                    trackPaths.Add(trackPath);
                    AudioService.ExtractTrack(audioPath, trackPath, track, copy: settings.FastCopy);

                    if (settings.FastCopy && ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
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
                if (settings.CleanOnError)
                {
                    foreach (var trackPath in trackPaths)
                    {
                        if (File.Exists(trackPath))
                        {
                            Logger.Info("Deleting track: {0}", trackPath);
                            File.Delete(trackPath);
                        }
                    }
                }

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
