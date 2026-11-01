<p align="center">
  <img src="docs/logo-512.png" alt="KUDUTEXT logo" width="128">
</p>

<h1 align="center">KUDUTEXT</h1>

<p align="center">Turn the speech in your videos and recordings into text and subtitles on Windows, on your own PC. Free and open source.</p>

<p align="center">
  <a href="https://github.com/azeembukhariprivate/kudutext/releases/latest/download/KUDUTEXT-setup.exe"><img src="https://img.shields.io/badge/Download-KUDUTEXT%20for%20Windows-e8590c?style=for-the-badge" alt="Download KUDUTEXT for Windows"></a>
  <br>
  <sub>The installer for Windows 10 and 11, always the latest version · <a href="https://github.com/azeembukhariprivate/kudutext/releases/latest">portable zip and what's new</a></sub>
</p>

---

## Features

- **Drop videos or recordings** (MP4, MKV, MOV, AVI, WMV, MP3, WAV, M4A, FLAC and more), or a whole folder of them. They're done one after another, and the list is kept for next time.
- **Subtitles and text**: save as `.srt` subtitles (for most video players), `.vtt` subtitles (for web pages) and plain `.txt` text, next to each video or in a folder you choose.
- **Three levels of accuracy**: *Fast*, *Good* (the usual choice) and *Best*. Each speech model is downloaded once, the first time it's used, and checked before it's kept.
- **Around 100 languages**, found by itself or chosen by you, and it can **write the text in English** from speech in any language.
- **Private**: everything happens on your PC. Your videos and recordings never leave it, and after the speech model is downloaded it works without the internet.
- **Check and correct the text**: go through it line by line with its times, play any line to hear it, fix words or times, split or join lines, and replace a word everywhere at once (handy for names). Saving writes the subtitle and text files again.
- **Subtitles into the video**: add them as a subtitle track players can turn on and off (quick, the picture isn't changed), or write them into the picture so they always show, on any player, phone or website (small, normal or large words). The new video is saved beside the original. This uses FFmpeg, a free video tool downloaded once (110 MB), the first time.
- **Never replaces your files**: if a `talk.srt` that KUDUTEXT didn't make is already there, the new one is saved as `talk (2).srt`.
- Light and dark themes, following Windows.

## Download

**[Download the installer](https://github.com/azeembukhariprivate/kudutext/releases/latest/download/KUDUTEXT-setup.exe)** (`KUDUTEXT-setup.exe`, always the latest version), or get any of these from the [Releases page](https://github.com/azeembukhariprivate/kudutext/releases/latest):

- `KUDUTEXT-x.y.z-setup.exe` installs KUDUTEXT for your Windows account (no administrator rights needed to install).
- `KUDUTEXT-x.y.z-portable.zip` runs without installing: unzip it anywhere and open `KUDUTEXT.exe`.

Requires 64-bit Windows 10 or 11. The speech models need some space: Fast 141 MB, Good 465 MB, Best 1.4 GB.

You can also start it from a command line with files to add: `KUDUTEXT.exe talk.mp4 interview.mp3`.

## Privacy

KUDUTEXT works on your computer only. It uses the internet for three things: downloading a speech model the first time it's needed (from Hugging Face, where whisper.cpp publishes them), downloading FFmpeg the first time a video needs it, and an optional check for a newer version on GitHub, which can be turned off in Settings. Each download is checked against a checksum before it's kept. Your videos, recordings and their text never leave your PC.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later). The screens are built in C# and the themes are loaded at startup, so the project builds with `dotnet` on any operating system; the app itself runs on Windows.

```
dotnet run --project tests/KUDUTEXT.Tests
dotnet build src/KUDUTEXT/KUDUTEXT.csproj -c Release -o out/KUDUTEXT
```

The speech engine is [whisper.cpp](https://github.com/ggml-org/whisper.cpp) 1.9.5, built from its source code by the GitHub Actions workflow (`.github/workflows/build.yml` shows the exact steps) and placed in an `engine` folder next to `KUDUTEXT.exe`.

The tests check the subtitle and text files, reading the speech engine's answers, the list of files, the speech model download (carrying on after it stops part way, refusing a file whose checksum is wrong), never replacing other files, and each step of turning a file into text. `KUDUTEXT.exe --self-test --model ggml-tiny.bin --samples <folder with jfk.mp3> --ffmpeg <FFmpeg download>` drives the real app on Windows: it reads sound from a WAV and an MP3, downloads a speech model from a small test server (stopping part way and carrying on), turns a real recording into text with the real speech engine, writes all three kinds of file, corrects the text (replacing words, splitting, joining and removing lines, catching a wrong time) and saves it again, downloads FFmpeg (stopping part way and carrying on), makes a test video and puts its subtitles in as a track and written into the picture (checking a frame has the words), stops a video part way, stops a file part way, refuses a broken file, and shows every window in both themes; it writes `self-test.log` and exits. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and compile `installer/KUDUTEXT.iss`.

### Shared code

Some files (the look, dialogs, the update check, administrator helpers) are shared by the KUDU apps and come from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common); each starts with a comment saying so, and `.kudu-common`
lists them. You don't need kudu-common to build this app, but change those files there, not here: the build checks that
they match.

## Publishing a new version

1. Change `<Version>` in `src/KUDUTEXT/KUDUTEXT.csproj` (and `MyAppVersion` in `installer/KUDUTEXT.iss`).
2. Commit and push to `main`.
3. GitHub Actions tests and builds the app. When no release exists yet for that version, it creates the `v` tag and publishes the installer and portable zip on the Releases page. Pushes that keep the same version only build.

## License

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
