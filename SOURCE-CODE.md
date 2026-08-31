# Fuse Player V1.0.0 - Corresponding source

This package contains the Fuse Player source and the source materials needed
to reproduce the native playback component shipped in `Native/libmpv-2.dll`.

## Fuse source

The C#, XAML, project and build files at the package root are the source for
the Fuse Player application. Fuse Player source and documentation are
copyright (c) 2026 ImWeLLaN and are released under the GNU General Public
License version 2 or any later version. See `LICENSE.txt` and `COPYING.txt`.

## Native playback component

The DLL shipped with Fuse Player was built on 2026-08-31 for
`x86_64-w64-mingw32` from these exact source revisions:

- mpv/libmpv: `2ee0b2a04b60d9a76ff2be053ba149f935c57855` (mpv
  `0.41.0-UNKNOWN`);
- FFmpeg: `818cecc6e1afab932cf4d40ef0d7b8cd40311a17`;
- build recipes: `mpv-winbuild-cmake` tag `20260814`.

The checked-out `Third-Party Source/mpv` and `Third-Party Source/ffmpeg`
directories are the authoritative corresponding sources for this DLL. The
older files `mpv-7b8915bc1d-source.zip` and
`ffmpeg-1d7b14f61-source.zip` are retained historical archives; they are not
the revisions used for the current DLL and must not be substituted for the
checked-out trees when reproducing this release.

The `mpv-master` directory and `mpv-local-changes.patch` are also retained
historical build artifacts. They are not part of the current DLL provenance;
use `Third-Party Source/mpv` and the revision recorded above.

The native library SHA-256 is:

```text
F709C7CA8B183BEC76B8158BF0C45C53018C63366750729352612F228FF7BDEA
```

The mpv source is configured as `libmpv` with FFmpeg, libplacebo, libass,
libcurl, libarchive, LuaJIT, OpenAL, SDL2, Vulkan, shaderc, SPIR-V Cross,
SubRandR, sixel, VapourSynth, rubberband and uchardet support. The full
feature report is preserved by the native build records.

## FFmpeg license mode and configure evidence

The exact FFmpeg configure command is recorded in the build tree and enables
`--enable-gpl` and `--enable-version3`, together with the external libraries
listed in `THIRD-PARTY-SOURCE-MANIFEST.md`. The matching configure output ends
with:

```text
License: GPL version 3 or later
```

Therefore the native FFmpeg portion of this release must be treated as
GPLv3-or-later. The applicable GPL/LGPL texts are included in `Licences Open`.
The native library does not import a separate FFmpeg DLL; the FFmpeg objects
are part of the libmpv native component.

## Local build changes

The reproducible build recipes and source trees include the small Windows
compatibility changes used by the native build (OpenSSL, curl, ngtcp2,
libvpl, libplacebo, SubRandR, libsixel and the path/toolchain fixes). The
corresponding recipe and source files are under `Third-Party Source` and the
build log index is in `BUILD-INFO.txt`.

## Corresponding-source requirement

When distributing `Fuze.exe` and `Native/libmpv-2.dll`, keep this Code package
available at the same time, or publish it at a stable public URL. It contains
the Fuse source, the exact mpv and FFmpeg source trees, the build recipes, the
local patches and the dependency source trees. Keep the license and notice
files with both the runtime and source packages for as long as the binary is
distributed.
