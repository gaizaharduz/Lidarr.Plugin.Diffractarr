using System.Diagnostics;
using System.Runtime.InteropServices;
using NLog;

namespace Lidarr.Plugin.Diffractarr
{
    public static class AudioService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly Dictionary<string, string[]> FfmpegUrls = new ()
        {
            ["linux-x64"] = new[]
            {
                "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz",
                "https://ffmpeg.martin-riedl.de/redirect/latest/linux/amd64/release/ffmpeg.zip",
                "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz",
            },
            ["linux-arm64"] = new[]
            {
                "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz",
                "https://ffmpeg.martin-riedl.de/redirect/latest/linux/arm64/release/ffmpeg.zip",
                "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz",
            },
            ["linux-arm"] = new[]
            {
                "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-armhf-static.tar.xz",
            },
            ["linux-musl-x64"] = new[]
            {
                "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz",
            },
            ["linux-musl-arm64"] = new[]
            {
                "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz",
            },
            ["linux-musl-arm"] = new[]
            {
                "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-armhf-static.tar.xz",
            },
            ["osx-arm64"] = new[]
            {
                "https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip",
            },
            ["osx-x64"] = new[]
            {
                "https://ffmpeg.martin-riedl.de/redirect/latest/macos/amd64/release/ffmpeg.zip",
            },
            ["windows-x64"] = new[]
            {
                "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
            },
            ["windows-x86"] = new[]
            {
                "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win32-gpl.zip",
            },
        };

        public static string FfmpegPath { get; set; } = "ffmpeg";

        public static void TranscodeToFlac(string src, string dst)
        {
            Logger.Info("Transcoding to .flac: {0}", src);
            RunFfmpeg($"-nostdin -hide_banner -loglevel warning -i \"{src}\" -y \"{dst}\"");
        }

        public static void ExtractTrack(
            string src, string dst, Track track, bool copy = false)
        {
            Logger.Info("Extracting track: {0}", dst);

            var args = $"-nostdin -hide_banner -loglevel warning " +
                       $"-ss {track.Start:F6} -i \"{src}\" ";

            if (track.Duration.HasValue)
            {
                args += $"-t {track.Duration.Value:F6} ";
            }

            if (copy)
            {
                args += "-c copy ";
            }

            args += $"-metadata \"track={track.Number}\" " +
                    $"-metadata \"title={track.Title}\" " +
                    $"-metadata \"artist={track.Artist}\" " +
                    $"-metadata \"album={track.Album}\" " +
                    $"-y \"{dst}\"";

            RunFfmpeg(args);
        }

        public static (int sampleRate, long totalSamples) ReadFlacSampleInfo(string path)
        {
            var header = ReadFlacHeader(path);

            // Sample rate: 20 bits at header[18..20]
            int sampleRate = (header[18] << 12) | (header[19] << 4) | (header[20] >> 4);

            // Total samples: 36 bits at header[21..25]
            // Byte 21 upper nibble = bps-1 low bits, lower nibble = total_samples[35:32]
            long totalSamples = ((long)(header[21] & 0x0F) << 32)
                | ((long)header[22] << 24)
                | ((long)header[23] << 16)
                | ((long)header[24] << 8)
                | header[25];

            return (sampleRate, totalSamples);
        }

        public static void FixFlacStreamInfo(string path, long totalSamples)
        {
            Logger.Debug("Fixing STREAMINFO: {0} (total_samples={1})", path, totalSamples);

            var header = ReadFlacHeader(path);

            header[21] = (byte)((header[21] & 0xF0) | (int)((totalSamples >> 32) & 0x0F));
            header[22] = (byte)((totalSamples >> 24) & 0xFF);
            header[23] = (byte)((totalSamples >> 16) & 0xFF);
            header[24] = (byte)((totalSamples >> 8) & 0xFF);
            header[25] = (byte)(totalSamples & 0xFF);

            Array.Clear(header, 26, 16);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
            fs.Write(header);
        }

        public static void EnsureFfmpeg(string? path, bool download)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (download && !File.Exists(path))
                {
                    DownloadFfmpeg(path);
                }

                FfmpegPath = path;
            }

            RunFfmpeg("-version");
        }

        private static string DetectPlatformKey()
        {
            string os;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                os = "windows";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                os = "linux";
                if (Directory.Exists("/lib")
                    && Directory.GetFiles("/lib", "ld-musl-*.so.1").Length > 0)
                {
                    os += "-musl";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                os = "osx";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            {
                os = "freebsd";
            }
            else
            {
                os = "unknown";
            }

            return os + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        }

        private static void DownloadFfmpeg(string path)
        {
            if (File.Exists(path))
            {
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var platformKey = DetectPlatformKey();
            if (!FfmpegUrls.TryGetValue(platformKey, out var urls))
            {
                throw new PlatformNotSupportedException($"FFMpeg download unavailable for: {platformKey}");
            }

            var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "ffmpeg.exe" : "ffmpeg";

            foreach (var url in urls)
            {
                try
                {
                    Logger.Info("Downloading FFMpeg from: {0}", url);
                    DownloadAndExtract(url, path, binaryName);
                    Logger.Info("Downloaded FFMpeg to: {0}", path);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to download FFMpeg from: {0}", url);
                }
            }

            throw new Exception($"Failed to download FFMpeg");
        }

        private static void DownloadAndExtract(string url, string path, string binaryName)
        {
            var tmpFile = Path.GetTempFileName();
            try
            {
                using (var http = new HttpClient())
                using (var src = http.GetStreamAsync(url).GetAwaiter().GetResult())
                using (var dst = File.Create(tmpFile))
                {
                    src.CopyTo(dst);
                }

                using var fileStream = File.OpenRead(tmpFile);
                using var reader = SharpCompress.Readers.ReaderFactory.Open(fileStream);
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory)
                    {
                        continue;
                    }

                    var name = Path.GetFileName(reader.Entry.Key ?? string.Empty);
                    if (!name.Equals(binaryName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var entryStream = reader.OpenEntryStream();
                    using var outFile = File.Create(path);
                    entryStream.CopyTo(outFile);

                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        File.SetUnixFileMode(
                            path,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }

                    Logger.Info("Downloaded ffmpeg to: {0}", path);

                    return;
                }

                throw new FileNotFoundException($"{binaryName} not found in: {url}");
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        private static byte[] ReadFlacHeader(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);

            var header = new byte[42];
            fs.ReadExactly(header);

            if (header[0] != 'f' || header[1] != 'L' ||
                header[2] != 'a' || header[3] != 'C')
            {
                throw new InvalidDataException($"Not a FLAC file: {path}");
            }

            return header;
        }

        private static void RunFfmpeg(string args)
        {
            var psi = new ProcessStartInfo(FfmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi) !;
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new Exception($"ffmpeg failed (exit {proc.ExitCode}): {err}");
            }
        }
    }
}
