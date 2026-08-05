# Native remote audio (browser ↔ radio USB) + optional HTTPS

## Summary

Adds **in-browser send/receive audio** so a remote operator on LAN/VPN can operate SSB without Mumble/SonoBus. Built on top of `feature/macos-and-linux-build` (this MR is **audio/HTTPS only** — no platform/Docker changes).

- **Remote Audio bridge:** PortAudio host I/O ↔ dedicated `/audio` WebSocket ↔ browser mic/speakers (PCM16 preferred; Opus via Concentus / WebCodecs when negotiated).
- **Settings:** opt-in Remote Audio section with radio RX (capture) / TX (playback) device pickers and gains; devices open only while a session is active.
- **Single session:** second browser is rejected busy; PTT stays on existing CAT TX controls.
- **Optional self-signed HTTPS:** generate cert in Settings, dual-listen HTTP + HTTPS after restart — required for remote `getUserMedia` (secure context).
- **Docs:** USER_MANUAL §6.2 / §6.8 / §18 and FAQ §15.7 updated.

### Notable code

| Area | Change |
|------|--------|
| `Services/Audio/*` | Bridge, PortAudio enumeration, Opus codec, wire protocol, HTTPS cert helper |
| `Controllers/AudioController.cs` | `/api/audio/devices`, status, cert generate |
| `Program.cs` | WebSockets `/audio`; Kestrel dual-bind when HTTPS enabled |
| `Pages/Settings.cshtml` | Remote Audio + HTTPS UI |
| `Pages/Index.cshtml` + `wwwroot/js/audio/*` | Start/Stop bar, mute, levels |
| `Models/ApplicationSettings.cs` | `AudioStreaming*`, `Https*` settings |
| `USER_MANUAL.md` | Remote audio setup, MOD SOURCE, TLS/VPN troubleshooting |

## Test plan

- [ ] `dotnet build -f net10.0` and `-f net10.0-windows` succeed
- [ ] Settings → Remote Audio: enable; RX/TX device lists populate (Refresh works); Save persists
- [ ] Index Remote Audio bar appears only when enabled; Start prompts for mic; RX audible; TX meter moves when speaking
- [ ] CAT TX button / toggle still keys the radio independently of the audio stream
- [ ] Second browser Start audio → busy / rejected
- [ ] Disable Remote Audio → bar hidden; no PortAudio devices held
- [ ] HTTPS: Generate cert with SAN (LAN/WG IP); Enable HTTPS; Save; **restart**; open `https://…:8443`; accept warning; Start audio works remotely
- [ ] Radio MOD SOURCE = USB/REAR; confirm TX audio modulates when keyed
- [ ] Voice Control mic (Windows) remains independent of radio USB device pickers
- [ ] Diff vs `feature/macos-and-linux-build` contains only audio/HTTPS files (no Docker/macOS tray churn)
