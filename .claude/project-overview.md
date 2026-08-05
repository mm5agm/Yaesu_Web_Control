# Yaesu Web Control – Project Overview for Claude

This document provides Claude with a high‑level understanding of the Yaesu Web
Control project. It describes the purpose of the application, the major
subsystems, the architectural philosophy, and the domain concepts used
throughout the codebase.

Claude should use this document to understand the intent of the project and
generate code that aligns with the existing design.

====================================================================
PROJECT PURPOSE
====================================================================

Yaesu Web Control (YWC) is a browser‑based control and monitoring interface for
Yaesu HF transceivers — currently the FTdx101MP, FTdx101D, FTdx10, FT‑710 and
FTDX3000. It provides real‑time visualisation of radio
metrics (S‑meter, power, SWR, ALC, etc.), CAT command integration, and a clean,
responsive UI for operators.

The application is written in:
- .NET 10 (backend)
- JavaScript ES modules (frontend)
- HTML/CSS for UI layout
- WebSockets for real‑time radio data

The goal is to provide a professional‑grade, modular, maintainable interface
that mirrors the behaviour of the physical radio while remaining easy to extend.

====================================================================
MAJOR SUBSYSTEMS
====================================================================

1. Gauge System
   - Renders S‑meter, Power, SWR, ALC, and other meters.
   - Uses canvas‑gauge library via a custom Gauge base class.
   - All layout logic is centralised in gauge.js.
   - Meter classes extend Gauge and supply configuration only.

2. Calibration Engine
   - Converts raw radio values into calibrated meter values.
   - Ensures consistent scaling across all meters.
   - Acts as the single source of truth for calibration formulas.

3. WebSocket Update Pipeline
   - Receives raw CAT data from the backend.
   - Parses and normalises incoming values.
   - Passes values through the calibration engine.
   - Updates gauges efficiently without re‑rendering.

4. UI Layout System
   - Uses CSS grid/flexbox for layout.
   - Avoids inline styles except for canvas width/height.
   - Ensures responsive behaviour across devices.

5. CAT Command Integration
   - Backend communicates with the FTdx101 via CAT protocol.
   - Frontend receives updates through WebSockets.
   - Commands and responses are modular and event‑driven.

====================================================================
ARCHITECTURAL PHILOSOPHY
====================================================================

The project follows a modular, maintainable, and predictable architecture:

- Single‑responsibility modules.
- No duplication of logic or configuration.
- No global variables.
- No magic strings.
- No layout logic outside gauge.js.
- Pure functions where possible.
- Clear separation of UI, calibration, and data flow.
- Class‑based behaviour for meter specialisation.
- ES module imports for all frontend code.

The codebase should feel like it was written by a disciplined engineering team.

====================================================================
PLATFORMS
====================================================================

- **Windows (`net10.0-windows`)** — full product: WinForms tray, SDR workers,
  Voice Control (SAPI). Shipped via NSIS installer.
- **macOS / Linux (`net10.0`)** — CAT + web UI only. macOS has an Avalonia
  menu-bar tray; Linux is console (or Docker). SDR and Voice Control are
  compiled/gated out. See USER_MANUAL §1 / §15.10 and CLAUDE.md operational
  differences table.

====================================================================
DOMAIN CONCEPTS
====================================================================

Claude should understand the following radio‑related concepts:

- S‑meter values (0–255 raw → S0 to +60 dB)
- Power output (0–200 W)
- SWR ratios (1.0 to 3.0)
- ALC levels
- CAT commands and responses
- Real‑time meter updates (20–60 Hz)
- FTdx101 menu and behaviour quirks

These concepts influence calibration, UI behaviour, and update frequency.

====================================================================
GOALS FOR GENERATED CODE
====================================================================

Claude should aim to generate code that is:

- Modular
- Predictable
- Easy to maintain
- Consistent with existing patterns
- Free of duplication
- ES‑module‑friendly
- Efficient for real‑time updates
- Architecturally aligned with gauge.js and the calibration engine

====================================================================
END OF DOCUMENT
====================================================================
