# Clicky for Windows

Clicky is an AI buddy that lives in your system tray. Hold a keyboard shortcut,
talk, and Clicky sees your screen, answers out loud, and can point at things
with a little blue cursor.

## What You Need

Clicky runs entirely on your machine. Nothing is uploaded to Clicky's servers.
The Windows LLM path uses your local Codex/ChatGPT sign-in through
`codex app-server`; speech still uses your own API keys.

| Service | What it does | Setup |
|---------|-------------|-------|
| Codex CLI | Powers the AI through local Codex/ChatGPT sign-in | Install Codex and sign in with ChatGPT/Codex |
| AssemblyAI | Turns your voice into text in real time | API key required |
| ElevenLabs | Speaks Clicky's replies out loud | API key required |

Clicky Settings currently exposes `OpenAI Codex OAuth` as the only LLM service.
It does not require an OpenAI API key.

## Installing

1. Install the Codex CLI and run `codex` once to sign in with ChatGPT/Codex.
2. Download the latest `Setup_Clicky_x.x.x.exe` from Releases.
3. Run the installer.
4. Enter your AssemblyAI and ElevenLabs API keys in Clicky Settings.
5. Grant microphone and screen capture permissions when prompted.
6. Hold **Ctrl+Alt** and talk.

Your speech API keys are encrypted with your Windows account credentials
(DPAPI) and stored only on your PC.

## Switching AI Models

Right-click the Clicky tray icon, then use **Model** to switch between Codex
models available to your signed-in account, including:

- GPT-5.5 (Codex OAuth)
- GPT-5.4 (Codex OAuth)
- GPT-5.4 Mini (Codex OAuth)
- GPT-5.3 Codex
- GPT-5.3 Codex Spark

The switch takes effect immediately.

## System Requirements

- Windows 10 version 1903 or later (Windows 11 recommended)
- .NET 8 Runtime for installed builds, or .NET 8 SDK for local development
- Codex CLI available on `PATH`
- A working microphone

## About the `worker/` Directory

The `worker/` directory contains a Cloudflare Worker proxy used by the Mac
reference implementation. The Windows build does not use the worker. It talks
to the local Codex app-server for LLM calls and directly to AssemblyAI and
ElevenLabs for speech.

## Building From Source

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Codex CLI](https://developers.openai.com/codex), signed in with ChatGPT/Codex
- Optional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) for building the installer

### Run Locally

From the repository root:

```powershell
.\run.cmd
```

Equivalent command:

```powershell
dotnet run --project windows\src\Clicky.App\Clicky.App.csproj -c Release
```

### Build and Test

```powershell
dotnet build windows\Clicky.sln
dotnet test windows\Clicky.sln
```

### Publish a Local Single-File Build

```powershell
.\publish-dev.ps1
```

Output:

- `Clicky.App.exe` in the repository root
- Release build artifacts under `windows\src\Clicky.App\bin\Release\`

Unsigned local single-file builds may be blocked by Windows Smart App Control.
For development, prefer `.\run.cmd`.

## Project Layout

```text
windows/
  Clicky.sln
  src/
    Clicky.App/          # WPF host, tray icon, settings window
    Clicky.Companion/    # Push-to-talk state machine, settings/secrets stores
    Clicky.Audio/        # Microphone capture + resampler
    Clicky.Capture/      # Multi-monitor screenshot capture
    Clicky.Hotkey/       # Global keyboard hook (Ctrl+Alt chord)
    Clicky.Overlay/      # Transparent click-through cursor overlay
    Clicky.Api/          # Codex app-server, AssemblyAI, ElevenLabs clients
    Clicky.Pointing/     # Pointing parser and evaluation helpers
  tests/
    Clicky.Tests/
```
