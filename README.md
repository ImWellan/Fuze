# Fuse Player Code V1.0.0

Fuse Player by ImWeLLaN is a Windows media player built around libmpv. It is
designed first for reliable playback, with a configurable Fuse interface.

Project: https://github.com/ImWellan/Fuse-Player  
Issues and feature requests: https://github.com/ImWellan/Fuse-Player/issues

## What is included

The code package contains the Fuse Player source, XAML interface files, build
scripts, the published native `libmpv-2.dll`, and the corresponding third-party
source material under `Third-Party Source`.

Fuse Player can:

- open files, folders and drives;
- maintain a media queue, playback history and M3U/M3U8 playlists;
- select audio and subtitle tracks and navigate chapters;
- configure the bottom bar, volume display, shortcuts and interface behavior;
- use mouse, wheel and keyboard controls for playback, seeking and speed;
- show media and track information and save screenshots;
- use full-screen playback on the selected display while preserving window state.

Fuse is a playback application first. Future additions such as simple video
trimming, richer playback tracking and other useful player features must not
interfere with playback and are not intended to turn Fuse into a recording or
conversion application.

## Native playback provenance

The DLL in `Native\\libmpv-2.dll` was produced by the native Windows build on
2026-08-31:

- mpv/libmpv: commit `2ee0b2a04b60d9a76ff2be053ba149f935c57855` (mpv
  `0.41.0-UNKNOWN`);
- FFmpeg: commit `818cecc6e1afab932cf4d40ef0d7b8cd40311a17`;
- target: `x86_64-w64-mingw32`;
- native DLL SHA-256:
  `F709C7CA8B183BEC76B8158BF0C45C53018C63366750729352612F228FF7BDEA`.

The matching FFmpeg configure report ends with `License: GPL version 3 or
later` and lists the enabled external libraries in
`THIRD-PARTY-SOURCE-MANIFEST.md`. The exact configure output is retained in the
local native-audit build records.

The source directories that produced this DLL are included in `Third-Party
Source`. The files named `mpv-7b8915bc1d-source.zip` and
`ffmpeg-1d7b14f61-source.zip` are older retained archives and are not the source
revisions used by the current DLL; use the checked-out `mpv` and `ffmpeg`
directories for this release.

## Build

Development requires the .NET 10 SDK on Windows:

```powershell
dotnet restore
dotnet build -c Debug
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:Version=1.0.0 -o .\\artifacts\\single
```

`BUILD-INFO.txt` records the observed SDK, publication command, hashes and
the native build evidence. The configure and target logs are preserved in
`Native Build Logs`. `SOURCE-CODE.md`,
`THIRD-PARTY-SOURCE-MANIFEST.md` and `RELEASE-CHECKLIST.md` describe the
corresponding-source set and redistribution steps.

## Feedback

Please report problems or suggest improvements at:

https://github.com/ImWellan/Fuse-Player/issues

## License and copyright

Fuse Player source code and project documentation are copyright (c) 2026
ImWeLLaN and are released under the GNU General Public License version 2 or
any later version. See `LICENSE.txt` and `COPYING.txt`.

The native libmpv DLL and every other third-party component keep their own
copyright and license terms. The effective native FFmpeg build is GPLv3-or-
later. Keep `THIRD-PARTY-NOTICES.txt`, the `Licences Open` folder and the
corresponding source directories together when redistributing the project.
