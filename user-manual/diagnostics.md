## 11. Diagnostics

Access the Diagnostics page from the navigation bar. It is primarily used when something is not working as expected.

**COM Ports button** — Opens a list of all serial ports currently detected on your PC. Use this if you are unsure which port the radio is connected to.

**CAT Status JSON button** — Opens a live JSON view of every radio parameter the app knows about. Useful when reporting a bug.

**Live Meter Readings table** — Shows the most recent raw value (0–255) received from the radio for each meter, alongside the CAT command used to request it and the time it was last updated. Rows flash yellow when a new value arrives. High SWR raw values are highlighted in orange.

**SignalR Event Log** — A scrolling log of every radio state update received over the websocket connection, with millisecond timestamps. Use the filter dropdown to narrow the log to a single property (e.g., SWR, Power, S-Meter). The **Pause** button freezes the log so you can read it; **Clear** empties it; **Save…** downloads the current log as a text file.

**About-page Diagnostics block** — the **About** page in the navigation bar has a separate Diagnostics block with a one-click **Copy diagnostics** button. The block lists YWC version, radio model, COM port, browser, .NET runtime, operating system, and (from v2.3.7) the **CPU model + logical core count** and **total physical memory** of the host PC. Paste the block when reporting a bug so it's clear whether you're running on hardware that can comfortably drive two SDRs + radio polling + spectrum render or whether resource pressure might be a factor.

### 11.1 The log file, and sending me one

YWC keeps a log of what it did, one file per day, rolling daily and keeping the last seven days. On Windows that is `%APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-YYYYMMDD.log`; §14.1 lists the macOS, Linux and Docker paths.

**It is always on, and I would ask you not to turn it off.** The faults that are hardest to fix are the intermittent ones — the app hangs at "Initializing" one morning in twenty, and by the time you tell me the moment has passed. A log that is already running is the only thing that catches those. It is deliberately modest: startup, the radio connection, band and mode changes, warnings and errors, at roughly a megabyte a day.

**Sending me a log.** Rather than hunting through the folder, use the buttons at the top of the Diagnostics page:

1. Click **Start fresh test log**. This drops a marker in the file — it does not erase anything.
2. Do the thing that goes wrong.
3. Click **Download test log**. You get only the lines since the marker, which is usually a page or two rather than a hundred thousand lines.
4. Drag the downloaded file straight into the GitHub issue or discussion.

If you want the lot instead, there is a **Download the full log** link beside those buttons.

**Detailed logging.** Settings has a **Detailed logging (for bug reports)** switch, off by default. Turning it on adds every CAT command sent, every reply received, each saved state and each status poll — tens of thousands of extra lines a day. For most problems the normal log is enough and the extra volume only makes the interesting line harder to find, so **please only turn it on if I've asked you to.**

Three things worth knowing about it:

- **It takes effect immediately**, with no restart. That is deliberate — if restarting were needed, the restart would often clear the very state that caused the problem.
- **It writes to its own file**, `ywc-detail-YYYYMMDD.log`, alongside the normal one. Your ordinary log carries on unchanged, so switching this on can never bury or shorten the history that might explain a problem you hit last week. **Download test log** hands you the detailed file automatically while the setting is on, so you do not have to pick.
- **Turn it off again when you're done**, but nothing breaks if you forget. The detailed file is capped and rolls rather than filling your disk. It is written fast — on a busy radio it can be a hundred times the size of the normal log — so there is no point leaving it running for days.

The natural order is: switch **Detailed logging** on in Settings → go to Diagnostics → **Start fresh test log** → reproduce the fault → **Download test log** → switch detailed logging back off.

---
