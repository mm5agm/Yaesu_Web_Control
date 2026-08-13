# Radio Web Control Core

Radio-agnostic code shared by **[Icom Web Control](https://github.com/mm5agm/Icom_Web_Control)**
(IWC) and **[Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control)**
(YWC).

IWC was cloned from YWC, so most of the code above the radio layer is the same
code maintained twice — measured in August 2026 at **62 of 89 shared-path files
effectively identical**. This repository is where that code goes so it is
maintained once.

**This is not a general-purpose library and does not want strangers as
consumers.** It exists to stop two applications drifting apart. Design it for
those two.

---

## The rule for what belongs here

**If it needs to know what a radio is, it does not go here.**

Everything in this repository must be usable by an application talking to an
Icom over CI-V and by one talking to a Yaesu over CAT, without either knowing
about the other. In practice that means DX cluster handling, memories, ADIF,
calibration maths, meter rendering, the SignalR transport, the tray icon and
text-to-speech — the parts of both apps that never touch the wire.

No CI-V. No CAT. No serial framing. No `IRadioController`, and no Yaesu
equivalent of it.

**Two copies must already agree before one of them moves.** Diff them first.
Where they have drifted, that is a decision about which behaviour wins and it
gets made deliberately — it is not settled by picking whichever repo you
happened to copy from.

## Layout

| | |
|---|---|
| `Models/`, and C# folders alongside it | compiled into `RadioWebControl.Core.dll` |
| `js/` | browser modules, **copied not compiled** — see `js/README.md` |

`RadioWebControl.Core.csproj` targets **`net10.0`, not `net10.0-windows`**, and
that is deliberate: YWC multi-targets `net10.0-windows` and `net10.0` for its
macOS/Linux CAT-only host, so a Windows-only target here would build fine
against IWC and silently break YWC's second target framework.

It has **no package references**, also deliberately. A dependency added here is
paid for by two applications and two installers.

## How the applications consume it

As a **`git subtree`** at `core/`, not a submodule and not a NuGet package. A
plain `git clone` of either application must still build with no extra steps —
that is the whole reason for the choice.

```bash
# first time, in the application repo
git subtree add  --prefix=core https://github.com/mm5agm/Radio_Web_Control_Core.git main --squash

# pull later changes down
git subtree pull --prefix=core https://github.com/mm5agm/Radio_Web_Control_Core.git main --squash

# push changes made inside core/ back up
git subtree push --prefix=core https://github.com/mm5agm/Radio_Web_Control_Core.git main
```

Each application then adds a `ProjectReference` to `core/RadioWebControl.Core.csproj`
**and a `<Compile Remove="core\**" />`** — both apps use the Web SDK, which
globs `**/*.cs`, so without the removal every file here is compiled twice: once
into the library and once directly into the application.

## Testing

Neither application has automated tests. **If anything justifies the first
ones, it is this repository** — a mistake here breaks two applications instead
of one, and the natural first tenants (`AdifParser`, `calibration-engine`) are
pure functions with no DOM, no radio and no I/O.

## Status

Phase 1 — the plumbing, proved end to end with one trivial type. `Models/DxSpot.cs`
is here because it was byte-identical in both applications bar its namespace,
which makes it the cheapest possible way to find out whether the subtree,
the project reference, the build and both installers survive.

The rest moves **opportunistically, not in a batch**: the next time a file here
would have been edited in both repositories, it moves instead. The work is paid
for by the change that needed doing anyway.

Full plan, including the measurements behind it and the costs:
[`docs/design/shared-core-plan.md`](https://github.com/mm5agm/Icom_Web_Control/blob/develop/docs/design/shared-core-plan.md)
in the IWC repository.

## Licence

GPL-3.0, matching both applications.
