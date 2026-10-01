# Bundled dependency and resource notices

`dependencies.json` pins the complete resolved application dependency inventory (including transitive and native packages), .NET / ASP.NET runtime packs, copyright/author metadata, license provenance and SHA256 of every supplied notice. Its artifact lists cover the arm64 host and conversion plugin; some restored cross-platform/build packages are included conservatively even when no code from them is bundled on macOS. The application build uses `verify-notices.py` to reject missing notices, unreviewed dependency versions and code artifacts without attribution, including inside website plugin packages.

`Dependencies/` preserves upstream package LICENSE and THIRD-PARTY-NOTICES files where available, otherwise a pinned upstream repository license. Native Skia/HarfBuzz and .NET runtime third-party notices are included, not merely their wrapper's MIT license.

Packages with byte-identical notices share one file through the inventory. The project license is copied from the repository's root `LICENSE` when bundling.

The osx-x64 .NET / ASP.NET runtime records reuse the corresponding arm64 notice files: both license and third-party-notice bytes were compared against the cached upstream 10.0.5 packages and are identical. This preserves the existing website release inventory without implying Intel build or runtime verification.

The resource inventory separately checks project license, Fluent icons, LiquidGlass, adapted Vex code, LocalSend logo, conversion/Highlight.js resources and LibRaw's licenses, provenance and corresponding source download instructions. Additional embedded grammar/font notices are listed in the inventory where applicable.

SharpZipLib 1.4.2 and SSH.NET 2026.0.0 use the MIT notices in `Dependencies/`. DotNetZip is no longer shipped; its old notice has been removed. The legacy archive test fixture remains for extraction compatibility.

When changing a package/runtime/resource version, review the actual distribution's license and transitive notices, update the pinned inventory and artifact list, and run both channel audits. An automated inventory does not replace a review of new upstream terms or App Store distribution compatibility.
