# Lidarr.Plugin.Diffractarr

Diffractarr is a Lidarr plugin that automatically splits multi-track cue sheet audio files into individual tracks.

## Installation

In Lidarr, go to System > Plugins and add:

```
https://github.com/gaizaharduz/Lidarr.Plugin.Diffractarr
```

Then go to Settings > Connect and add Diffractarr.

Diffractarr requires an FFmpeg binary.

If you are using the LinuxServer/Hotio Lidarr Docker container, the default setings will automatically download a statically linked binary for your platform into the `/config` folder.

If you are using a different configuration, or if you want to provide your own binary, you should change the "FFmpeg Path" and "Download FFmpeg" settings.

## What it does

When a download fails to import, Diffractarr will:

1. Check that FFmpeg is available, and if not, download it (if enabled)
2. Scan the download directory for `.cue` files
3. Parse the cue sheets (with automatic encoding detection)
4. Split multi-track audio files into individual tracks using FFmpeg
5. Temporarily rename original audio files to hide them from Lidarr
6. Re-trigger Lidarr's import process

## Settings

| Setting | Default | Description |
|-|-|-|
| FFmpeg Path | "/config/diffractarr/ffmpeg" | Path to `ffmpeg`/`ffmpeg.exe` (leave empty to use binary in system `PATH`) |
| Download FFmpeg | on | Auto-download FFmpeg to "FFmpeg Path" if not already present |
| Fast Copy | off | Use FFmpeg stream copy instead of re-encoding (faster, but not sample-accurate) |
| Delete Source | off | Delete original audio files and cue sheets after splitting |
| Clean on Failure | on | Delete partial split output on failure |
| Import | on | Trigger Lidarr import after splitting |

## Supported formats

| Extension | Handling |
|-|-|
| `.flac`, `.wav`, `.m4a`, `.wv` | Split directly (or stream copied when "Fast Copy" is enabled) |
| `.ape` | Transcoded to `.flac`, then split |

## Building

```bash
git clone --recurse-submodules https://github.com/gaizaharduz/Lidarr.Plugin.Diffractarr
cd Lidarr.Plugin.Diffractarr
dotnet build src/*.sln -c Release -f net8.0 -p:NuGetAudit=false
```

The merged plugin DLL will be at `_plugins/net8.0/Lidarr.Plugin.Diffractarr/Lidarr.Plugin.Diffractarr.dll`.

## Testing

```bash
dotnet test src/*.sln -c Release -f net8.0 -p:NuGetAudit=false
```

Requires `ffmpeg` and `ffprobe` in `PATH`.

## License

GPL-3.0 - see [LICENSE](LICENSE).
