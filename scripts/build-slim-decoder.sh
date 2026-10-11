#!/usr/bin/env bash
# Run in MSYS2 UCRT64; no compiler is installed by this script.
set -euo pipefail
export LC_ALL=C

FFMPEG_VERSION=9.0.2
LIBVPX_VERSION=1.16.0
LIBVPX_COMMIT=1024874c5919305883187e2953de8fcb4c3d7fa6
FFMPEG_KEY=FCF986EA15E6E293A5644F10B4322F04D67658D8
script_path=$(realpath "${BASH_SOURCE[0]}")
project_root=$(dirname "$(dirname "$script_path")")
output_arg=${1:-"$project_root/output/slim-decoder"}
source_bundle=${2:-}
[[ ${MSYSTEM:-} == UCRT64 ]] || { echo 'Use the MSYS2 UCRT64 shell.' >&2; exit 1; }
for tool in gcc make nasm perl pkg-config curl tar gpg objdump strip python; do
    command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 1; }
done
[[ $(gcc -dumpmachine) == x86_64-w64-mingw32 ]] || { echo 'Wrong GCC target.' >&2; exit 1; }
mkdir -p "$output_arg"
output=$(realpath "$output_arg")
work=$(mktemp -d "${TMPDIR:-/tmp}/fuwaru-slim.XXXXXXXX")
prefix="$work/prefix"
bundle="$work/corresponding-source"
mkdir -p "$prefix" "$bundle/sources" "$bundle/configuration" "$bundle/scripts" "$bundle/docs" "$output/licenses"
cp "$script_path" "$bundle/scripts/build-slim-decoder.sh"
cp "$project_root/docs/slim-decoder.md" "$bundle/docs/slim-decoder.md"

download() { curl --fail --location --retry 4 --retry-delay 3 --output "$2" "$1"; }
if [[ -n $source_bundle ]]; then
    # Rebuild offline from the extracted corresponding-source.zip.
    source_bundle=$(realpath "$source_bundle")
    cp "$source_bundle/sources/ffmpeg-$FFMPEG_VERSION.tar.xz" "$bundle/sources/"
    cp "$source_bundle/sources/libvpx-$LIBVPX_COMMIT.tar.gz" "$bundle/sources/"
    cp "$source_bundle/sources/SHA256SUMS" "$bundle/sources/"
    (cd "$bundle/sources" && sha256sum --check SHA256SUMS)
else
    download "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz" "$bundle/sources/ffmpeg-$FFMPEG_VERSION.tar.xz"
    download "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz.asc" "$bundle/sources/ffmpeg-$FFMPEG_VERSION.tar.xz.asc"
    download 'https://ffmpeg.org/ffmpeg-devel.asc' "$bundle/sources/ffmpeg-devel.asc"
    # NTFS ACLs do not implement Unix chmod; this directory holds public keys only.
    mkdir -p "$work/gnupg"
    gpg --homedir "$work/gnupg" --batch --import "$bundle/sources/ffmpeg-devel.asc"
    gpg --homedir "$work/gnupg" --batch --status-fd 1 --verify \
        "$bundle/sources/ffmpeg-$FFMPEG_VERSION.tar.xz.asc" \
        "$bundle/sources/ffmpeg-$FFMPEG_VERSION.tar.xz" > "$bundle/configuration/source-signature.txt"
    grep -F "[GNUPG:] VALIDSIG $FFMPEG_KEY " "$bundle/configuration/source-signature.txt" >/dev/null
    # Official GitHub mirror, pinned to the upstream v1.16.0 commit, not a floating tag.
    download "https://codeload.github.com/webmproject/libvpx/tar.gz/$LIBVPX_COMMIT" "$bundle/sources/libvpx-$LIBVPX_COMMIT.tar.gz"
    (cd "$bundle/sources" && sha256sum "ffmpeg-$FFMPEG_VERSION.tar.xz" "libvpx-$LIBVPX_COMMIT.tar.gz" > SHA256SUMS)
fi
mkdir -p "$work/ffmpeg" "$work/libvpx"
tar -xf "$bundle/sources/ffmpeg-$FFMPEG_VERSION.tar.xz" --strip-components=1 -C "$work/ffmpeg"
tar -xf "$bundle/sources/libvpx-$LIBVPX_COMMIT.tar.gz" --strip-components=1 -C "$work/libvpx"
export SOURCE_DATE_EPOCH=1790000000
jobs=${BUILD_JOBS:-$(nproc)}
common_flags='-Os -ffunction-sections -fdata-sections'
vpx_args=(--target=x86_64-win64-gcc --prefix="$prefix" --as=nasm
    --enable-static --disable-shared --disable-vp8 --disable-vp9-encoder
    --enable-vp9-decoder --disable-vp9-highbitdepth --disable-multithread
    --disable-examples --disable-tools --disable-docs --disable-unit-tests
    --disable-webm-io --disable-libyuv --disable-install-docs --disable-debug
    --enable-small --extra-cflags="$common_flags")
printf '%s\n' "${vpx_args[@]}" > "$bundle/configuration/libvpx-arguments.txt"
(
    cd "$work/libvpx"
    ./configure "${vpx_args[@]}"
    make -j "$jobs"
    make install
)
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
ffmpeg_args=(--prefix="$prefix" --target-os=mingw32 --arch=x86_64
    --cc=gcc --cxx=g++ --ar=ar --ranlib=ranlib --strip=strip --x86asmexe=nasm
    --pkg-config=pkg-config --pkg-config-flags=--static
    --disable-autodetect --disable-everything --disable-shared --enable-static
    --disable-gpl --disable-version3 --disable-nonfree --enable-small
    --disable-debug --disable-doc --disable-checkasm --disable-ffplay --disable-ffprobe
    --disable-avdevice --disable-swresample --disable-network --disable-pthreads
    --enable-w32threads --disable-hwaccels --enable-libvpx
    --enable-decoder=libvpx_vp9 --enable-parser=vp9
    --enable-demuxer=matroska --enable-protocol=file,pipe
    --enable-filter=scale,format --enable-encoder=rawvideo,wrapped_avframe
    --enable-muxer=rawvideo,null
    --extra-cflags="$common_flags -I$prefix/include"
    --extra-ldflags="-static -static-libgcc -static-libstdc++ -Wl,--gc-sections -L$prefix/lib")
printf '%s\n' "${ffmpeg_args[@]}" > "$bundle/configuration/ffmpeg-arguments.txt"
(
    cd "$work/ffmpeg"
    ./configure "${ffmpeg_args[@]}"
    make -j "$jobs" ffmpeg.exe
)
cp "$work/ffmpeg/ffmpeg.exe" "$output/ffmpeg.exe"
strip --strip-all "$output/ffmpeg.exe"
objdump -p "$output/ffmpeg.exe" > "$bundle/configuration/pe-imports.txt"
# A system DLL is fine; an MSYS/MinGW/libav DLL would break on a clean Windows PC.
if grep -Ei 'DLL Name:.*(msys-|libgcc|libstdc\+\+|libwinpthread|libvpx|libav|libsw)' "$bundle/configuration/pe-imports.txt"; then
    echo 'Unexpected external runtime DLL dependency.' >&2
    exit 1
fi
"$output/ffmpeg.exe" -version > "$bundle/configuration/ffmpeg-version.txt" 2>&1
"$output/ffmpeg.exe" -decoders > "$bundle/configuration/decoders.txt" 2>&1
"$output/ffmpeg.exe" -encoders > "$bundle/configuration/encoders.txt" 2>&1
"$output/ffmpeg.exe" -protocols > "$bundle/configuration/protocols.txt" 2>&1
grep -F 'libvpx-vp9' "$bundle/configuration/decoders.txt" >/dev/null
if grep -F 'libvpx-vp9' "$bundle/configuration/encoders.txt"; then
    echo 'Unexpected VP9 encoder in playback build.' >&2
    exit 1
fi
cp "$work/ffmpeg/COPYING.LGPLv2.1" "$output/licenses/FFmpeg-LGPL-2.1.txt"
cp "$work/libvpx/LICENSE" "$output/licenses/libvpx-BSD.txt"
cp "$work/libvpx/PATENTS" "$output/licenses/libvpx-PATENTS.txt"
# Keep notices for the static compiler/runtime support shipped in the executable.
for directory in /ucrt64/share/licenses/gcc /ucrt64/share/licenses/gcc-libs \
    /ucrt64/share/licenses/mingw-w64-gcc /ucrt64/share/licenses/crt \
    /ucrt64/share/licenses/mingw-w64-crt; do
    if [[ -d $directory ]]; then cp -r "$directory" "$output/licenses/$(basename "$directory")"; fi
done
gcc -print-file-name=libgcc.a > "$bundle/configuration/libgcc-path.txt"
{
    gcc --version
    ld --version
    nasm --version
    make --version
    perl --version
    pkg-config --version
    python --version
    pacman -Q
} > "$bundle/configuration/tool-versions.txt"
for config in config.h config_components.h ffbuild/config.mak ffbuild/config.log; do
    mkdir -p "$bundle/configuration/ffmpeg/$(dirname "$config")"
    cp "$work/ffmpeg/$config" "$bundle/configuration/ffmpeg/$config"
done
for config in vpx_config.h config.mk config.log; do
    cp "$work/libvpx/$config" "$bundle/configuration/libvpx-$config"
done
export SLIM_OUTPUT="$(cygpath -m "$output")" SLIM_BUNDLE="$(cygpath -m "$bundle")"
export FFMPEG_VERSION LIBVPX_VERSION LIBVPX_COMMIT
python - <<'PY'
import hashlib, json, os, pathlib, re, zipfile
output = pathlib.Path(os.environ['SLIM_OUTPUT'])
bundle = pathlib.Path(os.environ['SLIM_BUNDLE'])
binary = output / 'ffmpeg.exe'
info = {
    'purpose': 'FUWARU Windows x64 transparent VP9 WebM playback decoder',
    'ffmpegVersion': os.environ['FFMPEG_VERSION'],
    'libvpxVersion': os.environ['LIBVPX_VERSION'],
    'libvpxCommit': os.environ['LIBVPX_COMMIT'],
    'ffmpegSource': f"https://ffmpeg.org/releases/ffmpeg-{os.environ['FFMPEG_VERSION']}.tar.xz",
    'libvpxSource': f"https://codeload.github.com/webmproject/libvpx/tar.gz/{os.environ['LIBVPX_COMMIT']}",
    'binarySha256': hashlib.sha256(binary.read_bytes()).hexdigest(),
    'binaryBytes': binary.stat().st_size,
    'sourceArchives': {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                       for p in sorted((bundle / 'sources').iterdir())
                       if p.name.endswith(('.tar.gz', '.tar.xz'))},
    'configure': {name: (bundle / 'configuration' / f'{name}-arguments.txt').read_text().splitlines()
                  for name in ('ffmpeg', 'libvpx')},
    'imports': re.findall(r'DLL Name:\s*(\S+)', (bundle / 'configuration/pe-imports.txt').read_text()),
    'license': 'FFmpeg LGPL-2.1-or-later; libvpx BSD-3-Clause; GCC runtime exception',
    'sourceArchive': 'corresponding-source.zip',
}
text = json.dumps(info, indent=2, ensure_ascii=False) + '\n'
(output / 'build-info.json').write_text(text, encoding='utf-8')
(bundle / 'build-info.json').write_text(text, encoding='utf-8')
for path in (output / 'licenses').rglob('*'):
    if path.is_file():
        destination = bundle / 'licenses' / path.relative_to(output / 'licenses')
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(path.read_bytes())
with zipfile.ZipFile(output / 'corresponding-source.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(bundle.rglob('*')):
        if path.is_file():
            archive.write(path, 'corresponding-source/' + path.relative_to(bundle).as_posix())
print(f"Decoder: {binary.stat().st_size / 1024 / 1024:.2f} MiB")
print(f"Corresponding source: {output / 'corresponding-source.zip'}")
PY
echo "Build work/logs retained at $work"
echo "Playback decoder ready at $output"
