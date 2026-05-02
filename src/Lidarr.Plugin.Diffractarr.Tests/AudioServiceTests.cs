using Xunit;

namespace Lidarr.Plugin.Diffractarr.Tests;

public class AudioServiceTests : IDisposable
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures");

    private readonly string _tmpDir;

    public AudioServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "diffractarr-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, true);
        }
    }

    [Theory]
    [InlineData("flac", false)]
    [InlineData("flac", true)]
    [InlineData("wav", false)]
    [InlineData("wav", true)]
    [InlineData("m4a", false)]
    [InlineData("m4a", true)]
    [InlineData("wv", false)]
    [InlineData("wv", true)]
    [InlineData("ape", false)]
    [InlineData("ape", true)]
    public void SplitAudioFile_ProducesCorrectTracks(string ext, bool fastCopy)
    {
        string audioPath;
        if (ext == "ape")
        {
            audioPath = Path.Combine(_tmpDir, "album.ape");
            File.Copy(Path.Combine(FixturesDir, "album.ape"), audioPath);
        }
        else
        {
            audioPath = TestHelpers.MakeAudioFile(
                Path.Combine(_tmpDir, $"album.{ext}"));
        }

        var inputSamples = TestHelpers.GetTotalSamples(audioPath);
        var tracks = MakeTracks();
        var outputDir = Path.Combine(_tmpDir, "out");
        Directory.CreateDirectory(outputDir);

        var file = new AudioFile
        {
            Path = audioPath,
            Tracks = tracks.ToList(),
        };

        ProcessorService.SplitAudioFile(file, outputDir, fastCopy);

        var outExt = ext == "ape" ? ".flac" : $".{ext}";
        var expectedSamples = inputSamples / 3;
        foreach (var track in tracks)
        {
            var title = track.Title;
            var trackPath = Path.Combine(outputDir, $"{track.Number:D2}. {title}{outExt}");
            Assert.True(File.Exists(trackPath), $"Missing: {trackPath}");

            var samples = TestHelpers.GetTotalSamples(trackPath);
            if (fastCopy)
            {
                Assert.InRange(samples, expectedSamples, expectedSamples + 4096);
            }
            else
            {
                Assert.Equal(expectedSamples, samples);
            }
        }
    }

    [Fact]
    public void TranscodeToFlac_ProducesValidFlac()
    {
        var srcPath = TestHelpers.MakeAudioFile(
            Path.Combine(_tmpDir, "album.wav"));
        var dstPath = Path.Combine(_tmpDir, "album.flac");

        AudioService.TranscodeToFlac(srcPath, dstPath);

        Assert.True(File.Exists(dstPath));
        var samples = TestHelpers.GetTotalSamples(dstPath);
        Assert.True(samples > 0);
    }

    [Fact]
    public void ReadFlacSampleInfo_ReturnsValidValues()
    {
        var flacPath = TestHelpers.MakeAudioFile(
            Path.Combine(_tmpDir, "album.flac"), duration: 5);

        var (sampleRate, totalSamples) = AudioService.ReadFlacSampleInfo(flacPath);

        Assert.True(sampleRate > 0);
        Assert.True(totalSamples > 0);
    }

    [Fact]
    public void FixFlacStreamInfo_UpdatesTotalSamples()
    {
        var flacPath = TestHelpers.MakeAudioFile(
            Path.Combine(_tmpDir, "album.flac"), duration: 5);

        var (_, originalSamples) = AudioService.ReadFlacSampleInfo(flacPath);

        long newSamples = originalSamples / 2;
        AudioService.FixFlacStreamInfo(flacPath, newSamples);

        var (_, updatedSamples) = AudioService.ReadFlacSampleInfo(flacPath);
        Assert.Equal(newSamples, updatedSamples);
    }

    [Fact]
    public void EnsureFfmpeg_DownloadsAutomatically()
    {
        var originalPath = AudioService.FfmpegPath;
        var ffmpegPath = Path.Combine(_tmpDir, "ffmpeg");

        try
        {
            AudioService.EnsureFfmpeg(ffmpegPath, true);

            Assert.True(File.Exists(ffmpegPath));
            Assert.True(new FileInfo(ffmpegPath).Length > 0);
        }
        finally
        {
            AudioService.FfmpegPath = originalPath;
        }
    }

    private static Track[] MakeTracks(int duration = 9, int count = 3)
    {
        var segment = (double)duration / count;
        return Enumerable.Range(0, count).Select(i => new Track
        {
            Number = i + 1,
            Title = $"Track {i + 1}",
            Album = "Test Album",
            Artist = "Test Artist",
            Start = i * segment,
            Duration = i < count - 1 ? segment : null,
        }).ToArray();
    }
}
