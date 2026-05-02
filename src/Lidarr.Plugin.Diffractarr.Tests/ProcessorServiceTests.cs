using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.TrackImport;
using Xunit;

namespace Lidarr.Plugin.Diffractarr.Tests;

public class MockCompletedDownloadService : ICompletedDownloadService
{
    public List<TrackedDownload> ImportCalls { get; } = new ();
    public Action<TrackedDownload>? OnImport { get; set; }

    public void Check(TrackedDownload trackedDownload)
    {
    }

    public void Import(TrackedDownload trackedDownload)
    {
        ImportCalls.Add(trackedDownload);
        OnImport?.Invoke(trackedDownload);
    }

    public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults) => true;
}

public class ProcessorServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly MockCompletedDownloadService _mockService;
    private readonly ProcessorSettings _settings;
    private readonly ProcessorService _processor;

    public ProcessorServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "diffractarr-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _mockService = new MockCompletedDownloadService();
        _settings = new ProcessorSettings
        {
            FastCopy = false,
            DeleteSource = false,
            CleanOnError = true,
        };
        _processor = new ProcessorService(_mockService, _settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, true);
        }
    }

    [Fact]
    public void ProcessDownload_SplitsAndImports()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        TestHelpers.MakeDownload(dlDir);
        var td = MakeTrackedDownload(dlDir);

        _processor.ProcessDownload(td);

        var outputDir = Path.Combine(dlDir, "diffractarr", "Test Artist", "Test Album");
        Assert.True(File.Exists(Path.Combine(outputDir, "01. Track One.flac")));
        Assert.True(File.Exists(Path.Combine(outputDir, "02. Track Two.flac")));
        Assert.True(File.Exists(Path.Combine(outputDir, "03. Track Three.flac")));

        Assert.True(File.Exists(Path.Combine(dlDir, "album.flac")));
        Assert.True(File.Exists(Path.Combine(dlDir, "album.cue")));

        Assert.Single(_mockService.ImportCalls);
        Assert.Same(td, _mockService.ImportCalls[0]);
    }

    [Fact]
    public void ProcessDownload_DeleteSource_RemovesOriginals()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        TestHelpers.MakeDownload(dlDir);

        _settings.DeleteSource = true;
        _processor.ProcessDownload(MakeTrackedDownload(dlDir));
        _settings.DeleteSource = false;

        Assert.False(File.Exists(Path.Combine(dlDir, "album.flac")));
        Assert.False(File.Exists(Path.Combine(dlDir, "album.cue")));

        var outputDir = Path.Combine(dlDir, "diffractarr", "Test Artist", "Test Album");
        Assert.True(File.Exists(Path.Combine(outputDir, "01. Track One.flac")));
    }

    [Fact]
    public void ProcessDownload_NoCueSheets_ReturnsNotProcessed()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        Directory.CreateDirectory(dlDir);
        TestHelpers.MakeAudioFile(Path.Combine(dlDir, "album.flac"));

        _processor.ProcessDownload(MakeTrackedDownload(dlDir));

        Assert.Empty(_mockService.ImportCalls);
    }

    [Fact]
    public void ProcessDownload_SingleTrack_ReturnsNotProcessed()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        TestHelpers.MakeDownload(dlDir, tracks: new List<TrackEntry> { new TrackEntry(1, "Only Track", "00:00:00") });

        _processor.ProcessDownload(MakeTrackedDownload(dlDir));

        Assert.Empty(_mockService.ImportCalls);
    }

    [Fact]
    public void ProcessDownload_UnsupportedExtension_ReturnsNotProcessed()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        Directory.CreateDirectory(dlDir);
        TestHelpers.MakeAudioFile(Path.Combine(dlDir, "disc1.flac"));
        TestHelpers.MakeAudioFile(Path.Combine(dlDir, "disc2.flac"));
        File.Move(Path.Combine(dlDir, "disc1.flac"), Path.Combine(dlDir, "disc1.ogg"));

        var cueContent = "PERFORMER \"Test Artist\"\nTITLE \"Test Album\"\n" +
            "FILE \"disc1.ogg\" WAVE\n" +
            "  TRACK 01 AUDIO\n    TITLE \"Track One\"\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    TITLE \"Track Two\"\n    INDEX 01 00:03:00\n" +
            "FILE \"disc2.flac\" WAVE\n" +
            "  TRACK 03 AUDIO\n    TITLE \"Track Three\"\n    INDEX 01 00:00:00\n" +
            "  TRACK 04 AUDIO\n    TITLE \"Track Four\"\n    INDEX 01 00:03:00\n";

        File.WriteAllText(
            Path.Combine(dlDir, "album.cue"),
            cueContent);

        _processor.ProcessDownload(MakeTrackedDownload(dlDir));

        var outputDir = Path.Combine(dlDir, "diffractarr", "Test Artist", "Test Album");
        Assert.False(Directory.Exists(outputDir));

        Assert.Empty(_mockService.ImportCalls);
    }

    [Fact]
    public void ProcessDownload_CleanOnError_RemovesPartialOutput()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        TestHelpers.MakeDownload(dlDir);
        File.WriteAllBytes(Path.Combine(dlDir, "album.flac"), new byte[] { 0, 0, 0, 0 });

        _processor.ProcessDownload(MakeTrackedDownload(dlDir));

        var outputDir = Path.Combine(dlDir, "diffractarr", "Test Artist", "Test Album");
        Assert.False(Directory.Exists(outputDir));

        Assert.Empty(_mockService.ImportCalls);
    }

    [Fact]
    public void ProcessDownload_NoCleanOnError_KeepsPartialOutput()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        TestHelpers.MakeDownload(dlDir);
        File.WriteAllBytes(Path.Combine(dlDir, "album.flac"), new byte[] { 0, 0, 0, 0 });

        _settings.CleanOnError = false;
        _processor.ProcessDownload(MakeTrackedDownload(dlDir));
        _settings.CleanOnError = true;

        var outputDir = Path.Combine(dlDir, "diffractarr", "Test Artist", "Test Album");
        Assert.True(File.Exists(Path.Combine(outputDir, "01. Track One.flac")));

        Assert.Empty(_mockService.ImportCalls);
    }

    [Fact]
    public void SanitizeFilename_RemovesUnsafeCharacters()
    {
        Assert.Equal("Hello World", ProcessorService.SanitizeFilename("Hello: World"));
        Assert.Equal("AB", ProcessorService.SanitizeFilename("A/B"));
        Assert.Equal("test", ProcessorService.SanitizeFilename("  test  "));
        Assert.Equal("a b", ProcessorService.SanitizeFilename("a   b"));
    }

    [Fact]
    public void ProcessDownload_Import_HidesOriginalsAndRestoresAfterFailure()
    {
        var dlDir = Path.Combine(_tmpDir, "dl");
        TestHelpers.MakeDownload(dlDir);

        var audioPath = Path.Combine(dlDir, "album.flac");
        var backupPath = audioPath + ".bak";

        _mockService.OnImport = _ =>
        {
            // During import: original hidden, backup exists
            Assert.False(File.Exists(audioPath), "Original should be hidden during import");
            Assert.True(File.Exists(backupPath), "Backup should exist during import");
            throw new Exception("Import failed");
        };

        Assert.ThrowsAny<Exception>(() =>
            _processor.ProcessDownload(MakeTrackedDownload(dlDir)));

        // After failure: original restored, backup gone
        Assert.True(File.Exists(audioPath), "Original should be restored after import failure");
        Assert.False(File.Exists(backupPath), "Backup should be removed after import failure");
    }

    private TrackedDownload MakeTrackedDownload(string dlDir)
    {
        return new TrackedDownload
        {
            DownloadItem = new DownloadClientItem
            {
                OutputPath = new OsPath(dlDir),
            },
        };
    }
}
