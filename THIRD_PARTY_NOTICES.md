# Third-party notices

Cosine's original code is licensed under AGPL-3.0-only. Third-party components
retain their own licenses; the project license does not relicense them.

## Fonts and icons

All fonts are self-hosted. The following license files are included in the
frontend's public files and copied into the application during its build.

| Component | Source | License text |
|---|---|---|
| Outfit (display; unmodified Latin variable font) | [Outfit](https://github.com/Outfitio/Outfit-Fonts), distributed by @fontsource-variable/outfit | [SIL OFL 1.1](src/Cosine.Web/public/licenses/outfit-OFL-1.1.txt) |
| Inter (body/UI; @fontsource-variable/inter) | [Inter](https://github.com/rsms/inter) | [SIL OFL 1.1](src/Cosine.Web/public/licenses/inter-OFL-1.1.txt) |
| JetBrains Mono (@fontsource-variable/jetbrains-mono) | [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) | [SIL OFL 1.1](src/Cosine.Web/public/licenses/jetbrains-mono-OFL-1.1.txt) |
| Lucide icons (lucide-vue-next), including Feather-derived portions | [Lucide](https://github.com/lucide-icons/lucide) | [ISC and MIT](src/Cosine.Web/public/licenses/lucide-ISC-MIT.txt) |

The vendored Outfit font comes from
https://cdn.jsdelivr.net/npm/@fontsource-variable/outfit@5.3.0/files/outfit-latin-wght-normal.woff2.
SHA-256: `6c18d579fd87c3776be068b762cbc83fde3acb543d49eabd3ade842eb987e887`.
The OFL copyright notice is preserved in the accompanying license file.

## Package dependencies and runtime software

NuGet and npm dependencies are declared in the project files and
`src/Cosine.Web/package.json`; npm resolutions are recorded in `package-lock.json`.
Those packages retain their upstream licenses and notices. This file is not a
complete inventory of transitive dependencies.

The runtime image installs FFmpeg from Alpine Linux. FFmpeg's license depends on
its build configuration and enabled components; see https://ffmpeg.org/legal.html.
Distributors of binary images must preserve applicable third-party notices and
meet the corresponding source requirements for the exact binaries distributed.

The OpenSubsonic API specification in `tools/openapi/specs/` is used to generate API types.
Its upstream project is https://github.com/opensubsonic/open-subsonic-api,
licensed under [Apache-2.0](tools/openapi/specs/LICENSE).

## Screenshots

Screenshots in `assets/screenshots/` show the application using a personal music
library. Album artwork, artist photographs, and lyrics shown in those screenshots
belong to their respective rights holders and are not covered by Cosine's source
code license.
