# Fuse Player V1.0.0 - third-party source manifest

This manifest describes the native playback build that supplies
`Native/libmpv-2.dll`. It is based on the actual mpv and FFmpeg configure
reports retained in the native build records, not on the list of optional
packages in the build recipe alone.

## Exact native revisions

| Component | Revision or version | Source in this package |
| --- | --- | --- |
| mpv/libmpv | `2ee0b2a04b60d9a76ff2be053ba149f935c57855`, `0.41.0-UNKNOWN` | `Third-Party Source/mpv` |
| FFmpeg | `818cecc6e1afab932cf4d40ef0d7b8cd40311a17` | `Third-Party Source/ffmpeg` |
| mpv-winbuild-cmake | tag `20260814` | `Third-Party Source/mpv-winbuild-cmake-20260814-source.zip` |
| Target | `x86_64-w64-mingw32` | Windows native build |

The older `mpv-7b8915bc1d-source.zip` and
`ffmpeg-1d7b14f61-source.zip` files are retained historical archives. They are
not the revisions used by the current DLL; the checked-out source directories
above are authoritative.

## FFmpeg libraries enabled in the shipped build

The exact configure report lists these external libraries:

```text
avisynth  bzlib  iconv  lcms2  libaom  libaribcaption  libass  libbluray
libbs2b  libdav1d  libdvdnav  libdvdread  libfontconfig  libfreetype
libfribidi  libharfbuzz  libjxl  libmodplug  libmp3lame  libmysofa
libopenmpt  libopus  libplacebo  libsoxr  libspeex  libsrt  libssh
libsvtav1  libuavs3d  libvorbis  libvpx  libwebp  libx264  libx265
libxml2  libzimg  libzvbi  lzma  openal  openssl  sdl2  vapoursynth  zlib
libdavs2  librubberband  mediafoundation  opengl
```

Hardware and platform interfaces enabled by that report include AMF, CUDA,
cuvid, NVDEC/NVENC, D3D11VA, D3D12VA, DXVA2, VAAPI, Vulkan, FFNVCodec,
libvpl and the Windows Media Foundation APIs.

The report ends with `License: GPL version 3 or later`, because this build
uses `--enable-gpl` and `--enable-version3`. The native FFmpeg/libmpv portion
must therefore be redistributed under GPLv3-or-later terms. See the matching
texts in `Licences Open`.

## mpv features enabled in the shipped build

The mpv configure report enables libmpv, FFmpeg, libplacebo, libass, libcurl,
libarchive, LuaJIT, OpenAL, SDL2 gamepad support, Vulkan, shaderc, SPIR-V
Cross, SubRandR, sixel, VapourSynth, rubberband, uchardet, DVD navigation,
JavaScript and the Windows D3D/OpenGL backends.

## Source and license locations

The source tree or source archive for every library actually selected by the
two configure reports is present under `Third-Party Source`. Checked-out trees
retain their own license and notice files; archive-only inputs retain the
upstream archive and are also represented by a prefixed notice in `Licences
Open`. The most important license sources are:

| Component or group | License/notice source |
| --- | --- |
| mpv/libmpv | `mpv/Copyright`, `mpv/LICENSE.GPL`, `mpv/LICENSE.LGPL` |
| FFmpeg | `ffmpeg/LICENSE.md`, `ffmpeg/COPYING.GPLv2`, `ffmpeg/COPYING.GPLv3`, `ffmpeg/COPYING.LGPLv2.1`, `ffmpeg/COPYING.LGPLv3` |
| OpenSSL | `openssl/LICENSE.txt` (Apache License 2.0) |
| curl | `curl/COPYING` |
| libarchive, libass, libbluray, libdvdnav, libdvdread, libdvdcss | each source tree's `COPYING` file |
| libplacebo, libvpl, Vulkan, SubRandR | each source tree's `LICENSE`/`LICENSE.txt` file |
| x264, x265, davs2, rubberband | each source tree's `COPYING` file |
| AOM, dav1d, libvpx, libwebp, libjxl | each source tree's `LICENSE`, `COPYING` and/or `PATENTS` files |
| Brotli, c-ares, nghttp2, nghttp3, ngtcp2, libpsl, zstd, zlib, xxHash | each source tree's original license file |
| FLAC, LAME, Opus, Speex, Vorbis, libopenmpt | each source tree's original license/notice files |
| Fontconfig, FreeType, FriBidi, HarfBuzz, lcms2, libxml2, libunibreak | each source tree's original license/notice files |
| libpng, libjpeg, libmodplug, libmysofa, libsoxr, libzimg, libzvbi | each source tree's original license/notice files |
| SDL2, OpenAL Soft, shaderc, SPIR-V Cross, sixel, VapourSynth, uchardet, LuaJIT, MuJS | each source tree's original license/notice files |
| Intel/AMD/NVIDIA/Vulkan headers and Windows APIs | source/header tree notices; system APIs remain subject to their vendor terms |

The table is an index, not a replacement for the original license texts. Keep
the complete source trees and the `Licences Open` folder when redistributing.

## Build changes and corresponding source

The package includes the local Windows build changes used to produce the DLL:
the recipe/toolchain fixes and the small compatibility changes for OpenSSL,
curl, ngtcp2, libvpl, libplacebo, SubRandR and libsixel. The original source
files and the build recipes are available in `Third-Party Source`; the exact
commands and status are recorded in `BUILD-INFO.txt` and the native build logs.

Do not label a different native DLL with this manifest unless its mpv/FFmpeg
revisions and configure report match the values above.
