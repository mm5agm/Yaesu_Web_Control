# ADR 0002 — Alexa voice control via Cloudflare Tunnel

**Status:** Accepted — 2026-06-11
**Decision-makers:** Colin (MM5AGM)
**Branch:** `feature/alexa-voice-control` (work in progress; not yet on `develop`)

## Context

YWC operators frequently have both hands occupied — adjusting an antenna tuner, taking notes during a contest, logging on paper, working SMA connectors at the back of the radio. Voice control via an Amazon Echo (the most common smart speaker among hams) would let those operators issue common commands ("set frequency to 14.074 megahertz", "go to 40 metres", "rig status") without touching the keyboard or radio knobs.

Two architecturally different ways exist to give an Alexa Skill a working backend:

1. **Amazon's native AWS Lambda + API Gateway path.** Alexa calls a Lambda function via API Gateway. Lambda needs to reach the user's home PC somehow — typically by polling a relay service or via an inverse-tunnel arrangement. Or the Lambda just makes outbound HTTP calls if the user's PC has a public HTTPS endpoint, which they almost never do.

2. **HTTPS webhook endpoint with the user owning the server.** The Alexa Skill is configured to call an HTTPS URL directly. The user's PC needs to be reachable from the public internet at that URL, with a valid TLS certificate.

Path 2 needs a way to expose a localhost web server to the public internet, since YWC runs on the operator's PC behind a typical residential NAT. The tunnelling options for this are:

- **Cloudflare Tunnel** (`cloudflared`)
- **ngrok** (free tier has random URLs, paid tier has stable ones)
- **Tailscale Funnel** (works but more constrained)
- **AWS API Gateway + Lambda + reverse channel** (the natural Alexa-side option)
- **Port forwarding + dynamic DNS + Let's Encrypt** (the old-school way; lots of moving parts, residential ISPs increasingly block this)

## Decision

**Use Cloudflare Tunnel for the public endpoint. Do not use AWS Lambda.**

The Alexa Skill calls an HTTPS endpoint at `alexa.<user-domain>` (e.g. `alexa.mm5agm.co.uk`). That hostname is a CNAME-style record in Cloudflare's DNS pointing at a tunnel UUID. The operator's PC runs `cloudflared` as a Windows service, which connects outbound to Cloudflare's edge and accepts incoming traffic for that tunnel UUID, forwarding it to `http://localhost:8080/api/alexa` inside YWC.

YWC validates the Amazon-supplied request signature on every call (signature certificate chain + timestamp check, per Amazon's Skill certification requirements) before acting on the intent.

## Why Cloudflare Tunnel over AWS Lambda

| Concern | Cloudflare Tunnel | AWS Lambda + API Gateway |
|---|---|---|
| **Maintenance burden** | None ongoing. Tunnel runs as a Windows service; cert + DNS handled by Cloudflare. | Periodic Lambda runtime updates (Node 18 → 20 → 22 etc.), API Gateway version changes, IAM role drift, free tier billing alarms. |
| **Failure surfaces** | One process (`cloudflared`) on user's PC. | Lambda cold starts, API Gateway 5xx, IAM permission drift, region outages, CloudWatch log retention, account-level limits. |
| **Cost** | Free for personal use; HTTPS, DDoS protection, edge caching all included. | Free tier covers most ham usage but exits if a user gets popular. Billing alerts to set up. |
| **Deployment** | One `cloudflared install` Windows service command. | Code packaging, deploy via SAM/CDK/console, redeploy on every change. |
| **Debugging** | YWC's own log file shows everything. | CloudWatch logs (paid after free tier), separate console. |
| **Connectivity to YWC** | Direct: tunnel → localhost:8080. | Indirect: Lambda → relay service → user's PC. Adds another moving part. |
| **Skill endpoint reconfiguration** | URL stays stable for the life of the tunnel. | If the API Gateway URL changes (e.g. account migration), the Alexa Skill needs re-pointing. |

The decisive factor is **maintenance**: this is a hobby project. Anything that requires "log in to AWS quarterly to check that nothing has rotted" is the wrong answer. Cloudflare Tunnel is genuinely set-and-forget; AWS Lambda is genuinely not.

## Why not other tunnel options

- **ngrok** — works but free tier URLs are random (re-allocated on restart). Stable URLs need a paid plan. Not worth the cost when Cloudflare is free.
- **Tailscale Funnel** — works but limited to specific port ranges and requires Tailscale's mesh on the user's PC. Heavier than `cloudflared`.
- **Port forwarding + dynamic DNS** — works in principle but residential ISPs increasingly block inbound 80/443, CGNAT defeats it entirely, and Let's Encrypt cert renewal is one more thing to maintain. Users without a router-config skill set would fail this step.

## Consequences

**Positive:**

- Operators get hands-free voice control of common radio operations.
- The endpoint is private to each user (each user creates their own Skill in their own Amazon Developer account and points it at their own Cloudflare Tunnel URL); no shared/public infrastructure to maintain.
- YWC's existing CAT pipeline handles all the actual radio control — the Alexa endpoint is a thin translator from Alexa intent JSON into existing YWC HTTP calls.
- Disabled-by-default flag means non-voice users see zero behaviour change.

**Negative:**

- **Setup cost for users:** non-trivial. Domain on Cloudflare DNS (free if migrating an existing domain; ~£8/year if buying one), `cloudflared` install, Alexa Developer account, custom Skill setup, signature certificate handling. Probably 30-60 min from start to "Alexa, go to 40 metres" working.
- **Documentation burden:** a 5-10 page step-by-step user guide will be needed.
- **Per-user Amazon Developer accounts:** every user who wants voice control needs to define their own private Skill. There's no way to publish a single multi-user Skill that points at "your local PC" — each Skill instance is tied to one HTTPS endpoint at Skill-definition time.
- **Public-internet exposure:** even with signature validation, the endpoint is publicly reachable. Mandatory: rate-limiting (Cloudflare provides this), signature verification before any side effect, no signature → 403 fast.

## Implementation phases (summary; full plan in chat transcript 2026-06-11)

1. **Phase 0** — User-side prerequisites: Cloudflare account, domain on Cloudflare DNS.
2. **Phase 1** — `cloudflared` tunnel proof of concept: tunnel up, YWC reachable via `https://alexa.<user-domain>` from the public internet.
3. **Phase 2** — YWC's `/api/alexa` endpoint with signature validation, behind disabled-by-default flag. One intent (`GoToBandIntent`) working via curl.
4. **Phase 3** — Alexa Skill definition + real Echo end-to-end test for one intent.
5. **Phase 4** — Full intent set: `SetFrequencyIntent`, `SetModeIntent`, `RigStatusIntent`, `SetPreampIntent`, `SetAtuIntent`, `TunePttIntent`.
6. **Phase 5** — Documentation (`VOICE_CONTROL.md` at repo root; NOT in USER_MANUAL.md until the feature actually ships in a release).

## Documentation rule

Per Colin's directive (2026-06-11), voice-control instructions are written separately as `VOICE_CONTROL.md` at the repo root and are **not** linked from `USER_MANUAL.md` for the next release. Reason: users would otherwise see Alexa documentation and assume the feature is shipped, leading to confusion.

When voice control actually ships in a future release, `USER_MANUAL.md` gains a §17 linking to `VOICE_CONTROL.md`.
