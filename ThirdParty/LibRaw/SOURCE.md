# LibRaw source and redistribution notice

FKFinder uses the unmodified LibRaw 0.22.1 C API shared library under the upstream CDDL 1.0 option. Upstream also offers LGPL 2.1; both original license texts and COPYRIGHT are retained. No Watermark application code or native wrapper is included.

The complete, unmodified source corresponding to these libraries can be obtained from [LibRaw](https://www.libraw.org/data/LibRaw-0.22.1.tar.gz). The archive includes source file copyright notices and the licenses for incorporated code. The version is also identified by [the 0.22.1 release](https://github.com/LibRaw/LibRaw/releases/tag/0.22.1). FKFinder uses the prebuilt libraries and does not compile or bundle the source archive.

SHA-256 of the source archive: `a789dc4e2409e2901d93793a4e0b80c7b49d0d97cf6ad71c850eb7616acfd786`.

The build configuration and original binary hashes are in the repository's `ThirdParty/LibRaw/README.md`. The two libraries disable OpenMP, JPEG, LCMS, examples and static builds; the library install name is changed to `@rpath/libraw.dylib`. These packaging changes do not modify the library's source. Distribution signing changes the packaged binary hash.

These notices and source download instructions remain in both distribution channels. Distributors must keep the corresponding source available to recipients; if the upstream download becomes unavailable, provide an alternative download of the same archive. Changes to covered source must be accompanied by the corresponding source under its license. The application itself has separate licensing; this notice makes no statement about its commercial model.
