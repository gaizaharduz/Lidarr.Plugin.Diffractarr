using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.Diffractarr
{
    public class DiffractarrSettingsValidator : AbstractValidator<DiffractarrSettings>
    {
    }

    public class DiffractarrSettings : IProviderConfig
    {
        private static readonly DiffractarrSettingsValidator Validator = new ();

        [FieldDefinition(
            0,
            Label = "FFmpeg Path",
            HelpText = "Path to ffmpeg/ffmpeg.exe (leave empty to use binary in system PATH)",
            Type = FieldType.Path)]
        public string FfmpegPath { get; set; } = "/config/diffractarr/ffmpeg";

        [FieldDefinition(
            1,
            Label = "Download FFmpeg",
            HelpText = "Auto-download FFmpeg to \"FFmpeg Path\" if not already present",
            Type = FieldType.Checkbox)]
        public bool DownloadFfmpeg { get; set; } = true;

        [FieldDefinition(
            2,
            Label = "Fast Copy",
            HelpText = "Use FFmpeg stream copy instead of re-encoding (faster, but not sample-accurate)",
            Type = FieldType.Checkbox)]
        public bool FastCopy { get; set; }

        [FieldDefinition(
            3,
            Label = "Delete Source",
            HelpText = "Delete original audio files and cue sheets after splitting",
            Type = FieldType.Checkbox)]
        public bool DeleteSource { get; set; }

        [FieldDefinition(
            4,
            Label = "Clean on Error",
            HelpText = "Delete partial split output on error",
            Type = FieldType.Checkbox)]
        public bool CleanOnError { get; set; } = true;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
