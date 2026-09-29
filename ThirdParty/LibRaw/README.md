# LibRaw 0.22.1 for macOS thumbnails

These are standalone builds of the LibRaw C API from
`Watermark.Win/native/third_party/LibRaw-0.22.1`. Only the two dylibs and the
upstream copyright and dual-license texts are copied here; FKFinder does not
use Watermark's native wrapper or business code. The source tree is unchanged.

| Runtime | SHA-256 of `libraw.dylib` |
| --- | --- |
| osx-arm64 | `2679cb5868a13529de2a1de65993e87cba1700b81418506fed9c749c53075929` |
| osx-x64 | `aaaae39827d5d812592d92a966169c0427c4d64191bbb42a5c9342af516bd503` |

Both were built with minimum macOS 11.0, shared library enabled, and static
library, examples, OpenMP, optional JPEG and LCMS support disabled. System zlib
support is enabled for deflate-compressed DNG. Lossy JPEG-compressed DNG needs
optional JPEG support and is not covered by these builds. `otool -L` shows only
`/usr/lib/libz.1.dylib`, `/usr/lib/libSystem.B.dylib`, and
`/usr/lib/libc++.1.dylib` beyond the library's own install name; there are no
Homebrew or build-machine dependencies.

Rebuild outside this repository, using a clean copy of the same upstream source:

```sh
source_dir=/absolute/path/to/LibRaw-0.22.1
build_root=/private/tmp/fkfinder-libraw-build
for rid in osx-arm64 osx-x64; do
  if [ "$rid" = osx-arm64 ]; then arch=arm64; host=aarch64-apple-darwin
  else arch=x86_64; host=x86_64-apple-darwin; fi
  mkdir -p "$build_root/$rid"
  cd "$build_root/$rid"
  env CC="clang -arch $arch" CXX="clang++ -arch $arch" \
    CFLAGS='-O2 -mmacosx-version-min=11.0' \
    CXXFLAGS='-O2 -mmacosx-version-min=11.0' \
    LDFLAGS="-arch $arch -mmacosx-version-min=11.0" \
    "$source_dir/configure" --host="$host" --prefix="$build_root/$rid/install" \
    --enable-shared --disable-static --disable-examples --disable-openmp \
    --disable-jpeg --enable-zlib --disable-lcms
  make -j4 && make install
  cp "$build_root/$rid/install/lib/libraw.25.dylib" \
    "/absolute/path/to/FKFinder/ThirdParty/LibRaw/$rid/libraw.dylib"
  install_name_tool -id @rpath/libraw.dylib \
    "/absolute/path/to/FKFinder/ThirdParty/LibRaw/$rid/libraw.dylib"
done
```

Recheck both SHA-256 values, `lipo -info`, `otool -L`, and the minimum system
version in `otool -l` after rebuilding. The checked-in files are consumed by
local builds and release Actions; release jobs do not rebuild or download them.
