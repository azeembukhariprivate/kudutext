# KUDUTEXT

Speech to text and subtitles on Windows, on the user's own PC (whisper.cpp): a list of videos and recordings done one
after another, saved as .srt, .vtt and .txt, checked and corrected, and put into the video.
Part of the KUDU family of free, open-source (MIT) Windows apps by Azeem Bukhari.

## Working with Azeem (the owner)

- **Branches:** work on `main` and push to `main`. Ask before creating any other branch.
- **New features:** first say what you understood, and ask your doubts and suggestions, one question at a time.
  Only start building when Azeem says "build".
- **Look:** keep the app's current look. No redesigns unless asked.
- **About window credit:** "Made by Azeem, with Claude and ♥" (the heart in the accent colour). Keep it.
- **Commits:** author `Azeem Bukhari <12558389+azeembukhariprivate@users.noreply.github.com>`. Clear messages that say
  what changed and why. Don't put AI model names or versions in commits, code or docs.
- **Words:** the app's text, README and release notes use plain everyday words, not technical jargon.
- **Before pushing:** build and run the tests locally, and re-read the diff. After pushing, watch the GitHub Actions
  build until it's green; fix failures, never skip or disable a test to get green.

## How the app is built

- .NET Framework 4.8 WPF app. The screens are built in C# (no XAML compiler); the themes (`Themes/Light.xaml`,
  `Dark.xaml`, `Styles.xaml`) are XAML loaded at startup as embedded resources, using resource keys such as `Accent`,
  `Text`, `SurfaceBg`. Use the helpers in `Controls/Ui.cs` and existing keys rather than hard-coded colours.
- It builds with the plain .NET SDK on any OS (Microsoft.NETFramework.ReferenceAssemblies); it runs on Windows 10/11.
- Logic that doesn't need Windows lives in `Core/` and is tested by `tests/KUDUTEXT.Tests` (a .NET 8 console program).
- `KUDUTEXT.exe --self-test` drives the real app on Windows (GitHub Actions runs it, keeping screenshots of every window as
  a build download). Add a self-test step for new screens and features.
- Settings live in `HKEY_CURRENT_USER\Software\KUDUTEXT`. The Inno Setup installer (`installer/KUDUTEXT.iss`) installs per
  user, without administrator rights.

```
dotnet run --project tests/KUDUTEXT.Tests -c Release
dotnet build src/KUDUTEXT/KUDUTEXT.csproj -c Release -o out/KUDUTEXT
out\KUDUTEXT\KUDUTEXT.exe --self-test --model ggml-tiny.bin --samples <folder with jfk.mp3> --ffmpeg <ffmpeg zip>   (Windows only; needs engine\)
```

## Shared code (kudu-common)

The files listed in `.kudu-common` (the look and controls in `Controls/`, `Core/Admin.cs`, `AppInfo.cs`, `Args.cs`,
`ThemeManager.cs`, `Unelevated.cs`, `UpdateChecker.cs`, `Views/Dialogs.cs`, `Views/Screenshots.cs`, ...) are copied from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common) and start with a comment saying so.
**Don't edit them here**: change them in kudu-common, then run `python sync.py ../kudutext` there and commit both repos.
The build fails when a copy differs from kudu-common. App-specific details go in `Core/AppDetails.cs`.

## Releasing

1. Raise `<Version>` in `src/KUDUTEXT/KUDUTEXT.csproj` (and `MyAppVersion` in `installer/KUDUTEXT.iss`).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`, Windows) tests and builds the app. When no release exists yet for that
   version it creates the `v` tag and publishes the installer and portable zip on the Releases page, plus a copy named
   `KUDUTEXT-setup.exe` that the README's download button links to (`releases/latest/download/KUDUTEXT-setup.exe`).
   Pushes that keep the same version only build.

## The plan (agreed with Azeem)

Built in steps, reporting after each one and waiting for "go on":

1. Done (0.1): the app, the list (`Core/Jobs.cs`, jobs.json in `%LocalAppData%\KUDUTEXT`), the speech model download
   (`Core/SpeechModels.cs`: Fast = base, Good = small (the usual), Best = medium, from Hugging Face, carried on after a
   drop, kept only when whisper.cpp's published SHA-1 matches), reading sound (`Views/AudioReader.cs`: NAudio, WAV
   directly, the rest through Media Foundation; MP3/FLAC/WAV fall back to the engine's own reader), the engine
   (`Core/Whisper.cs` runs `engine\whisper-cli.exe`, JSON out, short paths for non-ASCII), `Core/Runner.cs` (one file:
   read, listen, keep the text in `Texts\{id}.json`, write files via `Core/Outputs.cs`, which never replaces a file
   it didn't make).
2. Done (0.2): checking and correcting the text (`Views/EditorWindow.cs`: a virtualised list of `LineRow`s with start,
   end and words; play a line from the original via `Views/LinePlayer.cs` (NAudio WaveOutEvent); split at the cursor,
   join with next, remove; find and replace all; times checked by `Core/TextEdits.cs` before saving; saving writes the
   kept text and the job's own files again via `Outputs.Rewrite`). Opened by "Check text" or a double-click on a row.
3. Done (1.0): subtitles into the video (`Views/VideoWindow.cs`: a track (`Ffmpeg.TrackArgs`: copied, mov_text in
   MP4/MOV, SRT in MKV, others become MKV) or written into the picture (`Ffmpeg.BurnArgs`: the subtitles filter with
   the SRT under a plain name in the work folder, libx264 + AAC MP4); saved as "name (subtitles).ext" / "name (subtitles
   in picture).mp4" beside the video via an ".unfinished" file). FFmpeg (`Core/Ffmpeg.cs`) is Gyan's 9.0.2 essentials
   build, pinned by URL, size and SHA-256, downloaded once into `%LocalAppData%\KUDUTEXT\FFmpeg` (only ffmpeg.exe and
   its licence are kept); it also reads sound Windows can't (`AudioReader.ViaFfmpeg`). Settings can get or remove it.

Versions below 1.0 were built and tested by CI but never released.

## Notes

- The speech engine is whisper.cpp 1.9.5 built from source in CI (pinned commit, all CPU variants, no OpenMP) with the
  Visual C++ runtime DLLs copied next to it; it isn't in git. The self-test gets the tiny model (SHA-1 checked) and
  whisper.cpp's `samples/jfk.mp3`.
- Nothing about the user is sent anywhere. The internet is used only for the model and FFmpeg downloads and the update check.
- To move to a newer FFmpeg, change `Ffmpeg.Package` (version, URL, size, SHA-256) and the workflow's self-test download.
- Other languages (1.1): the shared `Lang`/`LangUi` (kudu-common) put each window into the chosen language as it's shown
  (kept as `AppLanguage`; `Language` is the speech language). Texts are in `src/KUDUTEXT/Lang/<code>.txt`, made and
  checked with kudu-common's `tools/lang.py`; the names of the 100 speech languages stay in English. When adding or
  changing a text, add it to every language file (`tools/lang.py check ../kudutext`).
