# Design — Voice Control Setup Wizard

**Status:** Design accepted — 2026-06-15. Not yet implemented.
**Decision-makers:** Colin (MM5AGM)
**Related ADR:** [`docs/decisions/0002-alexa-voice-control.md`](../decisions/0002-alexa-voice-control.md)
**Target version:** v2.4.0 or later — depends on bandwidth and on Amazon's resolution of the open simulator-can't-reach-endpoint support case (#20830059391).

## Problem

The current Alexa voice-control setup (documented in `VOICE_CONTROL.md`) takes a technically-confident user **~60 minutes** end-to-end across four phases: Cloudflare DNS migration, `cloudflared` tunnel install, YWC backend configuration, and Alexa skill creation. The friction is killing adoption — to land a feature that benefits partially-sighted operators and hands-busy contesters, we need to make setup as low-friction as the rest of YWC.

Critically, **most of the existing 60-minute flow is automatable** by an in-app wizard. The bits that aren't (Cloudflare account signup, domain DNS migration, Amazon Developer account registration) are visibly distinct from the bits that are, and can be signposted accordingly.

## Constraints

- **No new executables.** YWC is already a self-contained WinExe. A separate setup app is a worse shape — it'd duplicate state (port detection, settings access, validation) and introduce a second install footprint to maintain.
- **No new long-running services besides `cloudflared`.** The wizard runs inside YWC's existing ASP.NET host.
- **Source-of-truth alignment.** The Alexa skill manifest must be generated from the same intent definitions the backend dispatches on. No more drift between `AlexaController.DispatchIntent` and a hand-written manifest.
- **Reversibility.** Every action the wizard takes must have a documented undo path. If a user decides they don't want voice control any more, they should be able to back out cleanly without orphaned tunnels, dangling DNS records, or zombie skills in their Amazon account.

## Architecture overview

A new tab in YWC's Settings page: **Voice Control**. Two-pane layout:

```
┌─ Voice Control Setup ───────────────────────────────────────────────┐
│                                                                     │
│  Step list (left)        │  Current step body (right)               │
│  ──────────────────      │  ────────────────────────                │
│  ✓ 1. Prerequisites      │                                          │
│  ✓ 2. Cloudflare domain  │  [ Content for whichever step is         │
│  ▸ 3. cloudflared        │    currently focused on the left ]       │
│    4. Create tunnel      │                                          │
│    5. Install service    │                                          │
│    6. Verify endpoint    │                                          │
│    7. YWC backend        │                                          │
│    8. Amazon skill       │                                          │
│    9. Simulator test     │                                          │
│    10. Echo test         │                                          │
│                          │                                          │
│  [Re-detect state]       │  [Back]  [Next / Run this step]          │
└─────────────────────────────────────────────────────────────────────┘
```

Step icons:

- `✓` completed
- `▸` current
- `(empty)` not yet reached
- `⚠` failed (right pane shows diagnostic)
- `🛇` blocked-on-human-action (right pane has "I've done this — re-check" button)

Steps progress strictly forward. Earlier steps can be revisited; the "Next" button on the current step only enables when its detection check passes. The "Re-detect state" button forces re-evaluation of every step from scratch, so a user who has manually broken something (deleted the tunnel, signed out of Amazon) and re-entered the wizard sees an accurate picture of where they actually are.

### Design principles

1. **Detect, don't trust.** Every step re-runs its detection logic on entry. Persisted state (see below) is a hint, not gospel.
2. **Visible hand-offs.** The user always knows when they're driving (Cloudflare account, Amazon registration) vs when the wizard is. The 🛇 icon makes the human-only steps unmistakable.
3. **Source-of-truth alignment.** The skill manifest JSON is generated from `AlexaController` at runtime. If the C# code changes, the manifest changes. No drift.
4. **Reversible.** Every automated action is reversible. An "Uninstall" panel tears down: stop service, delete tunnel, delete DNS record, set `AlexaEnabled = false`, optionally delete the skill via SMAPI.
5. **No new processes / installs.** Lives entirely inside YWC. `cloudflared` is the only external dependency, installed via its official MSI from cloudflare.com.

---

## Step-by-step

### Step 1 — Prerequisites

Checks YWC can do its part:

- Windows 11 or 10 — `Environment.OSVersion`
- YWC running on a discoverable port — `HttpPortInfo.Port`
- Admin privileges available for cloudflared service install — `WindowsIdentity` check (warns if YWC is running un-elevated; some later steps need a separate UAC-elevated subprocess)
- .NET runtime version reasonable — diagnostic only

Right pane lists pass/fail for each line and a one-line "what this is for". No buttons except **Next**, which enables only when all pass.

### Step 2 — Cloudflare domain

Three radio options shown to the user:

| Option | Wizard behaviour |
|---|---|
| "I already own a domain on Cloudflare DNS" | Ask for the domain in a text field. Verify via `nslookup -type=ns <domain>` that NS records point at Cloudflare (`*.ns.cloudflare.com`). Pass when verified. |
| "I own a domain elsewhere and need to migrate" | Open VOICE_CONTROL.md Phase 0 in a side panel. Wizard cannot complete this for the user — DNS propagation can take hours. Shows a **"I've moved DNS to Cloudflare — re-check"** button. |
| "I don't own a domain" | Link to Cloudflare Registrar with a short blurb (~£8/year for `.com`, varies by TLD). Same "re-check" button when done. |

This step is the longest by wall-clock time. The user closes the wizard, comes back later, and clicks "re-check". Wizard remembers state across sessions (see "State persistence" below).

### Step 3 — cloudflared install + login

Automated detection + actions:

- **Probe** `cloudflared --version` — if missing, **Install cloudflared** button downloads the official MSI from `https://github.com/cloudflare/cloudflared/releases/latest` to `%TEMP%` and launches it. Wait for the install to complete (poll for the cloudflared.exe to appear at `C:\Program Files\Cloudflare\cloudflared\cloudflared.exe`).
- **Detect login state** via `cloudflared tunnel list`. If response includes "401 Unauthorized" or similar, the user is not authenticated.
- **Open Cloudflare login** button runs `cloudflared login` as a subprocess. This opens the user's default browser to Cloudflare's OAuth consent page.
- Wait for `~/.cloudflared/cert.pem` to appear (polled every 2 seconds, max 5 minutes).
- When `cert.pem` appears, mark login complete and re-run `tunnel list` to confirm.
- "I've completed login — re-check" button is the fallback if the polling misses it (e.g. user closed and reopened the browser).

### Step 4 — Create tunnel + DNS

Fully automated, one button:

```
[ Create tunnel and route alexa.yourdomain.com ]
```

The wizard:

1. Generates a hostname-suffixed tunnel name (`ywc-alexa-<machine-name>`) to avoid clashes if the user has multiple PCs sharing the same Cloudflare account.
2. Runs `cloudflared tunnel create ywc-alexa-<machine-name>`. Captures the UUID and credentials file path.
3. Asks the user for a subdomain (default `alexa`). Confirms full hostname (e.g. `alexa.mm5agm.co.uk`).
4. Runs `cloudflared tunnel route dns ywc-alexa-<machine-name> alexa.<user-domain>`.
5. Writes `~/.cloudflared/config.yml`:
    ```yaml
    tunnel: <uuid>
    credentials-file: C:/Users/<user>/.cloudflared/<uuid>.json
    ingress:
      - hostname: alexa.<user-domain>
        service: http://localhost:<YWC actual port>
      - service: http_status:404
    ```

Error paths:

- DNS already routed elsewhere (e.g. user had a previous tunnel for the same hostname): show the raw cloudflared error, plus a **"Update existing route"** option that runs `cloudflared tunnel route dns --overwrite-dns`.
- UUID collision (very rare — name clash within their Cloudflare account): suggest a different machine-name suffix.
- Permission denied writing to `~/.cloudflared/`: surface the OS error, suggest checking ACLs.

### Step 5 — Install service

Automated, requires admin elevation. The wizard:

1. Runs `cloudflared service install` via a UAC-elevated subprocess.
2. Copies the user-profile `config.yml` and credentials JSON to `C:\Windows\System32\config\systemprofile\.cloudflared\` (the LocalSystem gotcha from VOICE_CONTROL.md Phase 1, gotcha #1).
3. Fixes the service `binPath` to include `tunnel run ywc-alexa-<machine-name>` (the binPath gotcha from Phase 1, gotcha #2 — `cloudflared service install` doesn't write the tunnel-run arguments).
4. Starts the service and waits for healthy state via `Get-Service cloudflared | Where-Object Status -eq Running` plus `cloudflared tunnel info ywc-alexa-<machine-name>` showing at least one connector connected.

Right pane shows live status of each sub-step. If admin elevation fails or is denied, the wizard explains why and offers a manual fallback (the PowerShell commands to run elevated outside the wizard).

### Step 6 — Verify endpoint reachable

The critical end-to-end check. The wizard:

1. **Self-test**: POSTs an empty JSON body to `https://alexa.<user-domain>/api/alexa` from the local machine. Expects HTTP 400 with body containing `"Bad Request"` (YWC's signature verifier rejecting an unsigned request). 400 = pipeline alive.
2. **External-source test**: optionally POSTs the same request from a public testing service. We used [reqbin.com](https://reqbin.com) during weekend debugging — its API endpoint is invokable from a server-side function we can host as part of YWC's release infrastructure. (Alternative: skip this for v1 and rely on the self-test.)
3. Reports the result with interpretation:

| HTTP status | Diagnosis |
|---|---|
| **400** | Pipeline alive (signature verifier rejected unsigned test). ✓ Pass |
| 404 | `AlexaEnabled` is `false`. Wizard will toggle it in step 7. |
| 5xx | Tunnel up but YWC didn't respond. Is YWC running on the expected port? |
| Timeout | Cloudflare can't reach the tunnel. Check service status — `services.msc → cloudflared`. |

A clear pass/fail with diagnosis means **no more "I am unable to reach the requested skill" mystery investigations of the kind we did this weekend.** The wizard catches the broken-chain case at this checkpoint rather than at simulator-test time.

### Step 7 — YWC backend configuration

One screen, two toggles:

- **`AlexaEnabled`** — wizard writes `true` to `appsettings.user.json` once the user clicks **Enable**. This is the Settings UI gap we identified during weekend testing — the wizard naturally fills it.
- **`AlexaSkillId`** — placeholder, filled in automatically by step 8 once SMAPI returns the skill ID. Read-only display.

Wizard then performs a final localhost test that `AlexaEnabled = true` is in effect (curl `localhost:<port>/api/alexa` should now respond 400, not 404).

### Step 8 — Create Alexa skill (SMAPI-driven)

The big-win step. Eliminates the entire Amazon-Developer-Console click-through flow.

```
┌─ Step 8: Create your Alexa skill ───────────────────────────────────┐
│                                                                     │
│  YWC will create the skill in your Amazon Developer account using  │
│  Amazon's Skill Management API (SMAPI). You'll sign in once via    │
│  Login With Amazon and grant YWC permission to create / update     │
│  your skills. After that the wizard does the rest.                 │
│                                                                     │
│  Skill display name: [ Yaesu Web Control            ]              │
│  Invocation name:    [ my rig                       ]              │
│  Endpoint URL:       https://alexa.mm5agm.co.uk/api/alexa          │
│                                                                     │
│           [  Sign in with Amazon and create skill  ]               │
│                                                                     │
│  What happens after you sign in:                                   │
│    • Wizard generates manifest from AlexaController intent list    │
│    • POST /v1/skills creates the skill                             │
│    • Polls /v1/skills/{id}/status until build SUCCEEDED            │
│    • Skill ID stored in YWC settings automatically                 │
│    • Estimated time: ~30 seconds                                   │
└─────────────────────────────────────────────────────────────────────┘
```

Progress shown live after sign-in:

```
  ✓ Got OAuth consent (alexa::ask:skills:readwrite, alexa::ask:models:readwrite)
  ✓ Generated manifest (4 intents, 2 custom slot types, 23 utterances)
  ✓ POST /v1/skills — skill ID amzn1.ask.skill.xxxx
  ✓ PUT /v1/skills/{id}/stages/development/interactionModel/locales/en-GB
  ▸ Building model... (12 seconds elapsed)
```

When SMAPI reports build status `SUCCEEDED`, the wizard writes the Skill ID into `Settings.AlexaSkillId` and marks the step complete.

#### LWA OAuth flow (PKCE)

Standard PKCE flow — no client secret in the YWC binary:

1. Wizard binds an ephemeral local HTTP listener on `127.0.0.1:<free port>` for the redirect.
2. Generates a `code_verifier` (random 32-byte URL-safe string) and `code_challenge` (SHA-256 hash).
3. Opens browser to:
    ```
    https://www.amazon.com/ap/oa
      ?client_id=<YWC_LWA_CLIENT_ID>
      &scope=alexa::ask:skills:readwrite+alexa::ask:models:readwrite
      &response_type=code
      &redirect_uri=http://localhost:<port>/oauth/lwa-callback
      &code_challenge=<base64url-of-SHA256-of-verifier>
      &code_challenge_method=S256
    ```
4. User signs in to Amazon, grants the requested scopes.
5. Amazon redirects to the local listener: `http://localhost:<port>/oauth/lwa-callback?code=<auth_code>`.
6. Wizard exchanges the code for tokens:
    ```
    POST https://api.amazon.com/auth/o2/token
      grant_type=authorization_code
      code=<auth_code>
      redirect_uri=http://localhost:<port>/oauth/lwa-callback
      client_id=<YWC_LWA_CLIENT_ID>
      code_verifier=<the original verifier>
    ```
7. Stores `refresh_token` encrypted via Windows DPAPI (per-user scope) in a new file `%APPDATA%\MM5AGM\Yaesu Web Control\lwa-tokens.dat`. Never stored in `appsettings.user.json` (which gets included in user-shared bug-report bundles).
8. Uses `access_token` for SMAPI calls. Refreshes when expired (1 hour TTL by default) using `grant_type=refresh_token`.

#### SMAPI skill creation

After OAuth, the wizard calls SMAPI:

1. **Get vendor ID** — `GET https://api.amazonalexa.com/v1/vendors` returns the developer's vendor ID(s). Use the first or prompt if multiple.
2. **Build manifest** — generate a JSON manifest from `AlexaController.DispatchIntent` reflection (see "Manifest generation" below).
3. **Create skill** — `POST https://api.amazonalexa.com/v1/skills` with the manifest. Response includes the skill ID.
4. **Set interaction model** — `PUT https://api.amazonalexa.com/v1/skills/{skillId}/stages/development/interactionModel/locales/{locale}` with the intents + slot types.
5. **Poll status** — `GET https://api.amazonalexa.com/v1/skills/{skillId}/status?resource=interactionModel&stage=development` until `lastUpdateRequest.status == "SUCCEEDED"` (typical: 10-30s).
6. **Store skill ID** — write to `Settings.AlexaSkillId`.

If any SMAPI call returns 401, refresh the access token and retry once. If still 401, restart the OAuth flow.

#### Manifest generation

The manifest is generated at runtime by inspecting the four intents in `AlexaController`. Pseudo-code:

```csharp
public class ManifestBuilder
{
    public string Build(string locale, string invocationName, string endpointUrl)
    {
        var intents = new[]
        {
            new IntentDef("SetBandIntent",     ("band", "BAND_NAME"),     SetBandUtterances),
            new IntentDef("SetFrequencyIntent",("frequencyMHz", "AMAZON.NUMBER"), SetFrequencyUtterances),
            new IntentDef("SetModeIntent",     ("mode", "MODE_NAME"),     SetModeUtterances),
            new IntentDef("RigStatusIntent",   null,                       RigStatusUtterances),
        };

        var slotTypes = new[]
        {
            new SlotTypeDef("BAND_NAME", BandNameValues),  // 12 HF bands + 2m + 70cm
            new SlotTypeDef("MODE_NAME", ModeNameValues),  // USB, LSB, CW, AM, FM, RTTY, FT8, data, PSK
        };

        return new ManifestJson { ... }.ToJson();
    }
}
```

The intent list, slot types, utterances, and slot values are defined **once** in a shared source-of-truth file (e.g. `Services/Alexa/AlexaIntentRegistry.cs`). Both the manifest builder and `AlexaController.DispatchIntent` read from this registry. Adding a new intent in the registry automatically:

- Updates the SMAPI manifest the wizard pushes
- Adds the dispatch case in `AlexaController`
- Stays in sync across releases

This is the "no drift" property mentioned in the design principles.

### Step 9 — Simulator test (also via SMAPI)

SMAPI exposes `POST /v1/skills/{skillId}/simulations` to send a simulated utterance from inside the wizard. We don't need the user to open the Amazon console.

```
  [ Send "what's the rig status" to your skill ]

  Simulating...
  ✓ SMAPI accepted simulation request (id: sim-1234)
  ✓ Skill endpoint received POST (verified via YWC log tail)
  ✓ Skill returned valid response: "VFO A is on 14.074 megahertz..."
  ✓ End-to-end success
```

The wizard detects both ends:

- **Amazon's side** — SMAPI's simulation API returns the SSML response the skill produced.
- **YWC's side** — the wizard subscribes to a one-shot watcher on `AlexaController` for the duration of the test. If a request matching the simulated session ID arrives within 30 seconds, that's the YWC-side confirmation.

This is the **gold-standard end-to-end test we didn't have during this weekend's debugging.** If both ends agree, the user can be confident the chain works.

If the simulator times out or Amazon's side reports an endpoint error, the wizard shows the diagnostic flow from VOICE_CONTROL.md Step 11.

### Step 10 — Echo test (optional)

"Find an Echo on the same Amazon account, say 'Alexa, ask my rig for status', confirm you hear the response."

This step is optional — users without an Echo (or with their Echo on a different Amazon account) skip it. The simulator test in Step 9 is the authoritative end-to-end check.

Wizard celebrates with a single "Done!" page. Offers a button: **"Run end-to-end check one more time"** for confidence.

---

## State persistence

Wizard state lives in `appsettings.user.json` under a new `VoiceControlSetup` block:

```json
"VoiceControlSetup": {
    "Domain": "mm5agm.co.uk",
    "Subdomain": "alexa",
    "TunnelName": "ywc-alexa-PC123",
    "TunnelUuid": "e4880ccd-1578-4616-97bd-46be6fcc219f",
    "SkillId": "amzn1.ask.skill.a3083f70-...",
    "LastCompletedStep": 9,
    "InvocationName": "my rig",
    "Locale": "en-GB"
}
```

`refresh_token` lives separately in `%APPDATA%\MM5AGM\Yaesu Web Control\lwa-tokens.dat` (DPAPI-encrypted) — not in `appsettings.user.json` because that file is included in user-shared diagnostic bundles.

Re-entering the wizard resumes at `LastCompletedStep + 1`. Each step's "Re-check" button forces re-detection regardless of saved state — so if the user manually breaks something (deletes the tunnel) and comes back, the wizard detects the regression and walks them through fixing it.

---

## Prerequisite — register an LWA Security Profile

This is the YWC publisher's job (Colin), done once:

1. Sign in at https://developer.amazon.com/loginwithamazon/console/site/lwa/overview.html (same Amazon Developer account used for YWC).
2. Create a Security Profile named **"Yaesu Web Control Wizard"**.
3. Add a **Web** allowed redirect: `http://localhost:0/oauth/lwa-callback` — `0` is the wildcard for any port. (The wizard will bind a free port at runtime and pass the actual port in the `redirect_uri` parameter.)
4. Note the **Client ID** — public, bundled in YWC. Stored as a const in `Services/Alexa/LwaClientConfig.cs` or similar.
5. **PKCE flow** (RFC 7636) — no client secret needed. LWA has supported PKCE since 2023, which is what lets a desktop client safely use OAuth without embedded secrets.

The Client ID is fine to be in a public binary. Without the per-user authorization code + verifier, possession of the Client ID alone gives an attacker nothing.

If we ever need to rotate (e.g. the Client ID is misused) we publish a new YWC version with the new ID.

---

## Implementation effort

| Piece | Lines | Notes |
|---|---|---|
| Step detection + state machine | ~400 C# | Orchestration, persisted state in `VoiceControlSetup` block |
| PowerShell helpers for `cloudflared` subprocess calls | ~150 | One-shot capture of cloudflared stdout/stderr; service-elevation prompt |
| LWA OAuth flow (PKCE, localhost listener, DPAPI token storage) | ~250 | Standard pattern; reuse from the GitHub OAuth flow if we have one |
| SMAPI HTTP client + manifest builder | ~400 | Create, update interaction model, status polling, simulation |
| Refresh token rotation + 401-retry logic | ~100 | Single retry on 401 with token refresh |
| Intent registry refactor (move from `AlexaController` constants) | ~200 | Source-of-truth for both manifest and dispatch |
| Razor wizard page (left pane + right pane + 10 step bodies) | ~600 | Server-rendered, no SPA framework |
| JS for live "re-check" UX and polling | ~150 | Plain JS; consistent with rest of YWC |
| `Uninstall` panel (reverse every automated step) | ~150 | Stop service → delete DNS → delete tunnel → set `AlexaEnabled = false` → optional: delete skill via SMAPI |
| Tests | ~300 | Unit tests for state machine + manifest builder; manual tests for the OAuth and SMAPI flows |
| USER_MANUAL update + VOICE_CONTROL.md retire | ~200 lines docs | The wizard replaces most of VOICE_CONTROL.md; keep that doc only as a "what's happening under the hood" reference |

**Total: ~5-6 days of focused work**, assuming no surprises with `cloudflared` CLI behaviour or SMAPI rate limits.

---

## What this doesn't fix

For honesty:

- **Cloudflare account creation** — still manual (Step 2). No API exists to create Cloudflare accounts programmatically (rightfully so — anti-spam).
- **Domain ownership / DNS migration** — still manual (Step 2). DNS propagation can take hours; nothing the wizard can do to speed that up.
- **Amazon Developer Account creation** — still manual. SMAPI assumes the user already has an account. The form is a one-time fill-in.
- **Identity verification (if user wants to publish)** — still manual *if and when* the user wants to publish the skill to the public Skill Store. Does NOT apply to development skills or private use on the user's own Echo. The wizard creates the skill in Development status; that's sufficient for private use.
- **The current open Amazon support case** (#20830059391, simulator can't reach existing skill) — independent of the wizard. The wizard plan can land regardless of that case's resolution. If anything, the wizard's recreate-via-SMAPI approach is *one possible workaround* for the existing skill being in a stuck state — once the case is resolved we can decide whether to keep the old skill or have everyone (including Colin) recreate via the wizard.

---

## Bonus benefit — continuous skill updates

With SMAPI established, **future YWC versions can update an existing user's skill manifest automatically**. If v2.5.0 adds an `OpenLogIntent`, the wizard can detect "your skill manifest is out of date" on next YWC startup and offer a one-click update via SMAPI's `PUT /v1/skills/{skillId}/stages/development/interactionModel/locales/{locale}` endpoint. The user doesn't have to manually re-paste a JSON manifest every release.

The intent registry being the source of truth makes this clean: increment a `MANIFEST_VERSION` constant when the registry changes, store the version each user is on, prompt the update when versions differ.

---

## Open questions

These are decisions deferred until implementation time:

1. **Naming the new Settings tab.** "Voice Control Setup" is descriptive but long. "Alexa Setup" is shorter but couples us to Alexa specifically. Default to "Voice Control" (future-proof to Google Assistant / Siri if we ever support them) — but commit to the existing four intents for now.

2. **When to ship.** Bundle with the next v2.x release or ship as a v2.4.0 milestone? Default: bundle. Fewer artifacts, less version skew.

3. **External-source reachability test in Step 6.** Reqbin worked during debugging but it's a third-party service; if it disappears the test fails. Options:
   - Keep the reqbin dependency (cheap, fragile)
   - Stand up a tiny YWC-owned ping endpoint (slightly more work, fully under our control)
   - Skip external test, rely on self-test only (simpler, less thorough)

4. **How to handle multiple vendor IDs.** A developer account can have multiple vendor IDs (rare, but possible). Default: prompt the user to pick if more than one is returned. Document the case in USER_MANUAL.

5. **OAuth as the only authentication path?** Or also offer a "paste your LWA refresh token manually" power-user fallback? Default: OAuth-only — the fallback path doubles the surface area we have to test/document. Power users who want CI/CD-style automation can use SMAPI directly from their own tools.

---

## Decisions confirmed 2026-06-15 in the design conversation

1. **OAuth-only** for LWA authentication. No manual-token-paste fallback in v1.
2. **One shared LWA Security Profile** registered by Colin (the YWC publisher), used by all YWC users. Client ID in the binary is fine; PKCE removes the need for a client secret.
3. **Recreate the existing skill via SMAPI** when the wizard is built, rather than preserving the manually-built one we have now. Forces the SMAPI flow to be exercised on a real account before users see it.
