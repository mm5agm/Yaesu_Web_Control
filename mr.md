# macOS / Linux CAT-only host, Docker (amd64/arm64), and platform docs

## Summary

Makes Yaesu Web Control runnable as a **CAT + web UI** host outside Windows, without shipping SDR spectrum or Voice Control on those platforms. The Windows installer path (`net10.0-windows`) is unchanged.

- **Multi-TFM:** `net10.0-windows` (full product: WinForms tray, voice, SDR worker) and `net10.0` (CAT-only console host for macOS/Linux).
- **macOS menu-bar tray** via Avalonia (`MacSystemTrayService`), driven on the process main thread after Kestrel `StartAsync` (AppKit-safe). Linux uses plain `app.Run()` with no tray.
- **`AutoShutdownWhenNoBrowsers`** setting (default on) so a headless shack can keep the process alive with no browser tabs; **Docker forces keep-alive** and skips local browser open (`HostRuntime.IsContainer`).
- **Linux Docker** multi-arch image (`linux/amd64`, `linux/arm64`) + `docker-compose.yml` with serial device pass-through and a data volume under `XDG_CONFIG_HOME=/data`.
- **UI/docs:** Settings/Index gate Windows-only panels with `IsWindowsHost`; serial validation accepts `/dev/…` on Unix; README / USER_MANUAL / CLAUDE document the operational differences (platforms table, install paths, FAQ §15.10).

### Notable code

| Area | Change |
|------|--------|
| `Yaesu_Web_Control.csproj` | Dual TFM; compile-remove WinForms tray / voice / SDR controller on `net10.0`; Avalonia + DBus pin on CAT-only |
| `Program.cs` | `#if WINDOWS` for tray/voice/SDR; macOS tray main-loop; Linux `app.Run()` |
| `RadioHub.cs` | Respects auto-shutdown setting; containers never auto-exit |
| `Services/MacSystemTrayService.cs` | Open / About / user data / Exit |
| `Dockerfile`, `docker-compose.yml`, `docker/entrypoint.sh` | Publish `net10.0` RID image; data dir under `/data`; container defaults via `HostRuntime` / `ApplyContainerDefaults` (no baked settings file) |
| CI `release.yml` | Explicit `-f net10.0-windows` for the Windows publish |

## Test plan

- [ ] `dotnet build -f net10.0-windows` succeeds (Windows product TFM; OK with `EnableWindowsTargeting` on macOS)
- [ ] `dotnet build -f net10.0` / `dotnet run --framework net10.0` on macOS — menu-bar status item appears; Open / Exit work; log shows `macOS menu-bar status item ready`
- [ ] CAT: set Serial Port to `/dev/cu.*`, Test Connection, meters/frequency update in the browser
- [ ] Settings: **Automatically exit when no browser is connected** off → closing all tabs does not exit the host within ~30s
- [ ] Settings: SDR / Voice Control sections hidden on CAT-only host; still present on Windows TFM
- [ ] Docker: `docker build --platform linux/arm64 -t ywc:local .` (and/or `linux/amd64`); `docker run -p 8080:8080 -v …:/data ywc:local` serves HTTP 200
- [ ] Docker compose on Linux/Pi with `YWC_SERIAL_DEVICE` — radio CAT works; settings persist under `./data/ywc` after Save Settings
- [ ] Windows installer / `net10.0-windows` smoke: tray, optional SDR/voice regression (no intentional behaviour change)
