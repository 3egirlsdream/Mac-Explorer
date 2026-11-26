# libzip 1.12.1

Upstream archive: https://libzip.org/download/libzip-1.12.1.tar.xz
SHA-256: `c40062022a1963c80573d48f6070792297cecd7c6bffe3aefcac0d8b9bee8913`

The macOS arm64 and x86_64 libraries are compiled from unmodified upstream source
using CMake Release builds with macOS 12.0 as the deployment target. The ZIP editor
uses the system zlib. BZip2, LZMA, Zstandard, OpenSSL, GnuTLS and Windows Crypto
backends are disabled, as are tools, examples, documentation and regression targets.
The install name is `@rpath/libzip.dylib`. See LICENSE for the BSD-3-Clause terms.
