using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Lidarr.Plugin.Diffractarr
{
    public class DiffractarrNotification : NotificationBase<DiffractarrSettings>
    {
        private readonly Logger _logger;
        private readonly ICompletedDownloadService _completedDownloadService;

        public DiffractarrNotification(
            ICompletedDownloadService completedDownloadService,
            Logger logger)
        {
            _logger = logger;
            _completedDownloadService = completedDownloadService;
        }

        public override string Name => "Diffractarr";
        public override string Link => "https://github.com/gaizaharduz/Lidarr.Plugin.Diffractarr";

        public override ProviderMessage Message => new (
            "Diffractarr splits multi-track cue sheet audio files into individual tracks.",
            ProviderMessageType.Info);

        public override void OnImportFailure(AlbumDownloadMessage message)
        {
            base.OnImportFailure(message);
        }

        public override ValidationResult Test()
        {
            return new ValidationResult();
        }

        internal void ProcessImportFailure(AlbumImportIncompleteEvent message)
        {
            var trackedDownload = message.TrackedDownload;

            AudioService.EnsureFfmpeg(Settings.FfmpegPath, Settings.DownloadFfmpeg);

            var settings = new ProcessorSettings
            {
                FastCopy = Settings.FastCopy,
                DeleteSource = Settings.DeleteSource,
                CleanOnError = Settings.CleanOnError,
            };

            var processor = new ProcessorService(_completedDownloadService, settings);
            processor.ProcessDownload(trackedDownload);
        }
    }
}
