# Custom CEF media build

The native engine is pinned in `cef-media-version.json` to the exact CEF revision used by CefSharp. Build tools and native outputs stay outside the application checkout in `D:\CrimsonCEF`. The installed browser is not changed during compilation.

Requires PowerShell 7.2+, the official depot_tools bootstrap, Visual Studio Desktop C++ with MFC/ATL, and the matching Windows SDK. The build script checks prerequisites, limits compilation to four jobs by default, and stops its own process tree if free space falls below 12 GiB. It generates Release x64 only, with debug symbols and PGO/LTO disabled to reduce build time and space.

```powershell
.\tools\Build-CefMedia.ps1 -Phase Check
.\tools\Build-CefMedia.ps1 -Phase All
Get-Content D:\CrimsonCEF\logs\sync.stdout.log -Tail 20 -Wait
Get-Content D:\CrimsonCEF\logs\compile.stdout.log -Tail 20 -Wait
```

After source synchronization, `-Phase Build` resumes compilation using the existing Ninja outputs; `-Phase Package` creates the native distribution and runs the media and browser regression checks. It writes `verified-engine.json` only after those checks pass. `Stage-CefMedia.ps1` validates native version, commit and x64 architecture before copying a complete distribution into an isolated built application directory and checking every copied hash.

Build and stage the CEF integration test application first. Run its normal regression suite and the opt-in media checks against the staged engine before publishing. A matching version string alone does not prove AAC/H.264 decoding support.

The test executable accepts `--require-aac-h264 --codec-fixture=<local-H264-AAC-MP4>` to require MediaSource support and actual local MP4 playback. `--probe-live=<public-YouTube-URL>` prints codec capabilities and the public player response using an isolated temporary profile. Adding `--require-live-playback` also requires the public player to decode and advance video frames. Network availability, consent and stream availability can affect this opt-in live check.

`Test-CefMedia.ps1 -DistributionRoot <distribution-directory>` builds an isolated test application, stages the engine, and requires H.264/AAC support, playback of a hash-pinned Chromium test clip, and the existing regression suite. The clip is downloaded from the official Chromium GitHub mirror unless `-MediaFixture` points to the pinned local copy.

`build-installer.ps1 -NativeCefDistribution <distribution-directory>` runs this verification before publishing and then stages that engine before packaging. `-NativeCefMediaFixture` can supply the local test clip. Normal builds continue to use the standard NuGet engine unless the custom distribution parameter is supplied.

Compiling with `proprietary_codecs=true ffmpeg_branding=Chrome` does not provide redistribution rights. Review the applicable codec licenses before including the custom engine in a public release. Technical references: [Google's CEF build instructions](https://support.google.com/webdesigner/answer/10043691), [CEF build setup](https://chromiumembedded.github.io/cef/master_build_quick_start).
