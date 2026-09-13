## 7. Application Setup

Access Application Setup from the navigation bar. This page configures the external application buttons and the WSJT-X UDP connection.

> **Windows-oriented.** The launch buttons run executables on the machine hosting YWC. On macOS/Linux the defaults are Windows paths — either leave the buttons hidden or point them at apps installed on that host. You can still run WSJT-X (etc.) on another PC and connect via YWC's **rigctld** server over the LAN.

### 7.1 External App Buttons

Up to five buttons can appear in the top bar to launch external applications. For each button you can set:

- **Show / Hide** — whether the button appears on the main page
- **Button Name** — the label shown on the button (e.g., "WSJT-X")
- **Command Line** — the full path to the executable, including any arguments

Default command lines:

| App | Default |
|-----|---------|
| WSJT-X | `C:\WSJT\wsjtx\bin\wsjtx.exe --rig-name=WebApp` |
| JTAlert | `C:\HamApps\JTAlert\JTAlert.exe` |
| Log4OM | `"C:\Program Files (x86)\Log4OM 2\Log4OM.exe"` |
| GridTracker | `"C:\Program Files\GridTracker2\GridTracker2.exe"` |
| Fldigi | `"C:\Program Files\Fldigi-4.2.11\fldigi.exe"` (version may differ) |

Adjust these to match where you have installed each program. GridTracker and Fldigi are **off by default** — tick the **Show** box for each once you've installed it and confirmed the command line is correct. The Fldigi button was added in v2.4.0 at the request of Bill W1WRH ([#52](https://github.com/mm5agm/Yaesu_Web_Control/issues/52)); the process detection uses the `fldigi.exe` task-manager name.

#### Path quoting — important

YWC parses each command-line entry into two parts: the **path to the executable** and any **arguments** to pass to it.

- If your **path contains spaces** (anything under `C:\Program Files`, `C:\Program Files (x86)`, etc.), the path **must be wrapped in double quotes** so YWC knows where the path ends and arguments begin.
- If your path has no spaces, quotes are optional.
- Anything after the closing quote (or, for unquoted paths, after the first space) is passed to the program as command-line arguments.

Examples:

| Entry | What gets launched |
|-------|--------------------|
| `C:\HamApps\JTAlert\JTAlert.exe` | `JTAlert.exe` with no arguments — no spaces in path, no quotes needed |
| `"C:\Program Files (x86)\HamApps\JTAlertV2\JTAlertV2.exe" /wsjtx` | `JTAlertV2.exe` with the argument `/wsjtx` — path has spaces so the quotes are required; everything after the closing quote is passed as arguments |
| `C:\Program Files (x86)\HamApps\JTAlertV2\JTAlertV2.exe /wsjtx` | Will **fail to launch** — without quotes, YWC takes everything up to the first space (`C:\Program`) as the path |
| `"C:\Program Files (x86)\Log4OM 2\Log4OM.exe"` | `Log4OM.exe` with no arguments — quotes required because the path contains spaces |

The four defaults above already follow this rule. If you've upgraded from an earlier release that allowed unquoted paths with spaces, YWC will automatically add the quotes the first time it reads your settings, so existing setups continue to work. If you add command-line arguments later, double-check that the quotes still surround **only the path**, not the whole string.

---

### 7.2 WSJT-X UDP Settings

| Setting | Default | Description |
|---------|---------|-------------|
| UDP Address | 239.255.0.1 | Multicast address WSJT-X sends status packets to |
| UDP Port | 2237 | UDP port number |

These must match WSJT-X's **Settings → Reporting → UDP Server** settings. See Section 9.1 for full WSJT-X setup instructions.

---
