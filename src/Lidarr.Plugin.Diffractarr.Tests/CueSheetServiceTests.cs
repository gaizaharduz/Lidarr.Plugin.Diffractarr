using System.Text;
using Xunit;

namespace Lidarr.Plugin.Diffractarr.Tests;

public class CueSheetServiceTests : IDisposable
{
    private readonly string _tmpDir;

    public CueSheetServiceTests()
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

    [Fact]
    public void Parse_MultiFile_ReturnsCorrectStructure()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "disc1.flac"), 6);
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "disc2.flac"), 6);

        var cuePath = WriteCue("""
            PERFORMER "Artist"
            TITLE "Album"
            FILE "disc1.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 03:00:00
            FILE "disc2.flac" WAVE
              TRACK 03 AUDIO
                TITLE "Three"
                INDEX 01 00:00:00
              TRACK 04 AUDIO
                TITLE "Four"
                INDEX 01 03:00:00
            """);

        var cs = CueSheetService.Parse(cuePath);

        Assert.Equal(2, cs.AudioFiles.Count);
        Assert.EndsWith("disc1.flac", cs.AudioFiles[0].Path);
        Assert.EndsWith("disc2.flac", cs.AudioFiles[1].Path);

        var tracks = cs.AudioFiles[0].Tracks.Concat(cs.AudioFiles[1].Tracks).ToList();
        Assert.Equal([1, 2, 3, 4], tracks.Select(t => t.Number));
        Assert.Equal(["One", "Two", "Three", "Four"], tracks.Select(t => t.Title));
        Assert.Equal([0.0, 180.0, 0.0, 180.0], tracks.Select(t => t.Start));
        Assert.Equal([180.0, null, 180.0, null], tracks.Select(t => t.Duration));

        foreach (var t in tracks)
        {
            Assert.Equal("Artist", t.Artist);
            Assert.Equal("Album", t.Album);
        }
    }

    [Fact]
    public void Parse_ResolvesOriginal_WhenExists()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "exists.flac"));
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "exists.wav"));
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var cuePath = WriteCue("""
            PERFORMER "Artist"
            TITLE "Album"
            FILE "exists.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
            """);

        var cs = CueSheetService.Parse(cuePath);
        Assert.EndsWith("exists.flac", cs.AudioFiles[0].Path);
    }

    [Fact]
    public void Parse_ResolvesExtension_WhenWrongExtension()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "wrong_ext.flac"));
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var cuePath = WriteCue("""
            PERFORMER "Artist"
            TITLE "Album"
            FILE "wrong_ext.wav" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
            """);

        var cs = CueSheetService.Parse(cuePath);
        Assert.EndsWith("wrong_ext.flac", cs.AudioFiles[0].Path);
    }

    [Fact]
    public void Parse_ResolvesCueStem_WhenWrongStem()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var cuePath = WriteCue("""
            PERFORMER "Artist"
            TITLE "Album"
            FILE "wrong_stem.wav" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
            """);

        var cs = CueSheetService.Parse(cuePath);
        Assert.EndsWith("album.flac", cs.AudioFiles[0].Path);
    }

    [Fact]
    public void Parse_ResolvesOriginal_WhenNoAudioFileExists()
    {
        var cuePath = WriteCue("""
            PERFORMER "Artist"
            TITLE "Album"
            FILE "nonexistent.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
            """);

        var cs = CueSheetService.Parse(cuePath);
        Assert.EndsWith("nonexistent.flac", cs.AudioFiles[0].Path);
    }

    [Fact]
    public void Parse_TrackPerformer_OverridesDiscPerformer()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var cuePath = WriteCue("""
            PERFORMER "Disc Artist"
            TITLE "Album"
            FILE "album.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                PERFORMER "Track Artist"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 03:00:00
            """);

        var cs = CueSheetService.Parse(cuePath);
        Assert.Equal("Track Artist", cs.AudioFiles[0].Tracks[0].Artist);
        Assert.Equal("Disc Artist", cs.AudioFiles[0].Tracks[1].Artist);
    }

    [Fact]
    public void Parse_IndexFrames_ConvertedToSecondsAccurately()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var cuePath = WriteCue("""
            PERFORMER "Artist"
            TITLE "Album"
            FILE "album.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 01:25:45
            """);

        var cs = CueSheetService.Parse(cuePath);

        // 60 + 25 + 45/75 = 85.6
        Assert.Equal(85.6, cs.AudioFiles[0].Tracks[0].Duration);
    }

    [Fact]
    public void Parse_Utf8Bom_ParsedCorrectly()
    {
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var content = "\uFEFF" + """
            PERFORMER "Artist"
            TITLE "Album"
            FILE "album.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 03:00:00
            """;

        var cuePath = Path.Combine(_tmpDir, "album.cue");
        File.WriteAllText(cuePath, content, new UTF8Encoding(true));

        var cs = CueSheetService.Parse(cuePath);
        Assert.Equal("Artist", cs.Artist);
        Assert.Equal("One", cs.AudioFiles[0].Tracks[0].Title);
        Assert.Equal("Two", cs.AudioFiles[0].Tracks[1].Title);
    }

    [Fact]
    public void Parse_Windows1251Encoding_ParsedCorrectly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        TestHelpers.MakeAudioFile(Path.Combine(_tmpDir, "album.flac"));

        var cuePath = Path.Combine(_tmpDir, "album.cue");
        File.WriteAllBytes(cuePath, Encoding.GetEncoding(1251).GetBytes("""
            PERFORMER "Артист"
            TITLE "Альбом"
            FILE "album.flac" WAVE
              TRACK 01 AUDIO
                TITLE "Один"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Два"
                INDEX 01 03:00:00
            """));

        var cs = CueSheetService.Parse(cuePath);
        Assert.Equal("Артист", cs.Artist);
        Assert.Equal("Альбом", cs.Album);
        Assert.Equal("Один", cs.AudioFiles[0].Tracks[0].Title);
        Assert.Equal("Два", cs.AudioFiles[0].Tracks[1].Title);
    }

    private string WriteCue(string content, string name = "album.cue")
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }
}
