# Comskip builds for the plugin's "Install Comskip"

`Dockerfile` builds a self-contained (static) Linux Comskip from the upstream source,
<https://github.com/erikkaashoek/Comskip> (GPL-2.0), with a small static ffmpeg (only the
decoders and formats Comskip reads) and argtable2. Comskip's former "donator" features
(H.264 / HEVC, multi-threading) are on: upstream enables them unless `--disable-donator`.

The `Build Comskip` workflow (`.github/workflows/comskip.yml`) builds it for x86-64 and arm64 on
GitHub's own runners and publishes `comskip-linux-x64` and `comskip-linux-arm64`, each with a
`.sha256`, as a release tagged `comskip-<version>-<commit>`. The release notes link the exact
source of Comskip, ffmpeg and argtable2 used.

Build locally: `docker build --output type=local,dest=out comskip` → `out/comskip`.
