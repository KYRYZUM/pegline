# Code signing policy

This page describes how Pegline's Windows releases are built and which of them are signed.

## What is signed

Only the Windows release files published on this repository's
[Releases](https://github.com/KYRYZUM/pegline-clipboard-manager/releases) page for a `v*` tag: the installer
(`PeglineApp-win-Setup.exe`), the portable package (`PeglineApp-win-Portable.zip`) and the update
packages (`PeglineApp-*-full.nupkg`, `PeglineApp-*-delta.nupkg`), together with the executables and
libraries built from this repository that they contain. Nothing else is signed: no third-party
binaries, no builds made on a developer's machine and no files that were not built from this
repository's source code.

## How releases are built

Every release is built from source by the [`Release` workflow](.github/workflows/release.yml) on
GitHub-hosted runners when a version tag such as `v0.1.3` is pushed. The workflow runs the test suite,
downloads the pinned visual search models from this repository's `models-mobileclip-s2-v1` release
and verifies them by SHA-256, packages the app with Velopack and publishes the GitHub release. The
build log of every release is public under [Actions](https://github.com/KYRYZUM/pegline-clipboard-manager/actions).

## Team roles

- Committers and reviewers: [@KYRYZUM](https://github.com/KYRYZUM) (Enes Sarı). Changes from anyone
  else arrive as pull requests and are reviewed by a committer before they are merged.
- Approvers: [@KYRYZUM](https://github.com/KYRYZUM) (Enes Sarı). Every signing request is approved by
  hand, one release at a time.

All team members use multi-factor authentication for GitHub.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

Everything Pegline captures (copied images, texts and links, screenshots, recordings, notes, folders
and tags) stays on the computer in `%LOCALAPPDATA%\Pegline`. Text recognition (Windows OCR) and
visual search (Apple MobileCLIP-S2 with ONNX Runtime) run entirely on the device.

Pegline connects to the network only for these features, each of which can be turned off:

- **Update checks** (Settings › Updates › "Automatically check for updates"): the installed app asks
  GitHub for this repository's latest release at launch and every hour, and downloads an update only
  when the user chooses to install it. See the
  [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
- **Link previews** (Settings › Sources › "Fetch link titles and thumbnails", also in the tray menu):
  when a public HTTPS link is copied, Pegline requests that page to read its title and thumbnail, as
  a browser would. For YouTube links it asks YouTube's oEmbed service
  ([Google Privacy Policy](https://policies.google.com/privacy)). Links to local networks and links
  carrying keys, tokens, signatures or credentials are never requested.
- **Opening a link** in the browser happens only when the user asks for it.

Pegline has no telemetry, analytics, accounts or crash reporting.
