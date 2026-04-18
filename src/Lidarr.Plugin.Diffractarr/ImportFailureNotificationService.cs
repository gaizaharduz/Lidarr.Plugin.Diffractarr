using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Lidarr.Plugin.Diffractarr
{
    public class ImportFailureNotificationService : IHandle<AlbumImportIncompleteEvent>
    {
        private readonly INotificationFactory _notificationFactory;
        private readonly INotificationStatusService _notificationStatusService;
        private readonly Logger _logger;

        public ImportFailureNotificationService(
            INotificationFactory notificationFactory,
            INotificationStatusService notificationStatusService,
            Logger logger)
        {
            _notificationFactory = notificationFactory;
            _notificationStatusService = notificationStatusService;
            _logger = logger;
        }

        public void Handle(AlbumImportIncompleteEvent message)
        {
            foreach (var notification in _notificationFactory.OnImportFailureEnabled())
            {
                if (notification is not DiffractarrNotification diffractarr)
                {
                    continue;
                }

                try
                {
                    if (ShouldHandleArtist(notification.Definition, message.TrackedDownload.RemoteAlbum.Artist))
                    {
                        diffractarr.ProcessImportFailure(message);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to process import failure: " + notification.Definition.Name);
                }
            }
        }

        private bool ShouldHandleArtist(ProviderDefinition definition, Artist artist)
        {
            if (definition.Tags.Empty())
            {
                return true;
            }

            if (definition.Tags.Intersect(artist.Tags).Any())
            {
                return true;
            }

            _logger.Debug(
                "{0} does not have any intersecting tags with {1}. Skipping.",
                definition.Name,
                artist.Name);
            return false;
        }
    }
}
