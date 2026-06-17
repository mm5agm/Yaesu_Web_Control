# Voice Control with Alexa

**Status:** In development on the `feature/alexa-voice-control` branch.
**Not yet shipped in any release** — once shipped, this document will be linked from the main User Manual. Until then, treat this as a work-in-progress reference.

---

## Is this for you?

Voice control via an Amazon Echo lets you issue common YWC commands hands-free — useful when you're holding an antenna tuner, working SMA connectors at the back of the radio, or just don't want to walk back to the keyboard.

Before you start, please understand the **commitment level**:

| | |
|---|---|
| **Setup time** | 30–60 minutes for a technically-confident user |
| **One-time cost** | £0 (Cloudflare, Amazon Developer account, and `cloudflared` are all free) |
| **Recurring cost** | £0 if you already own a domain; ~£8/year if you need to buy one |
| **Hardware** | An Amazon Echo (or any Alexa-enabled device) on the same Amazon account you'll use for the Developer Console |
| **Skill level needed** | Comfortable changing DNS nameservers at your domain registrar, comfortable creating an Amazon Developer custom Skill, comfortable editing JSON snippets in a browser console |

If those words make you nervous, voice control isn't the right feature for you *yet* — please ignore the rest of this document until either we publish a one-click installer (no timeline), or you become curious enough to learn the steps.

For everyone else, the rest of this document is the step-by-step.

### Why does setup look this involved?

The Alexa Skill model is fundamentally **per-user** for private skills like this one. Each user creates their own Skill in their own Amazon Developer account, pointing at their own personal HTTPS endpoint. There's no way to publish a single "YWC Voice Control" skill to the Alexa marketplace that magically reaches *your* radio — every Skill instance is tied to one specific URL at definition time.

To get that URL working on your home network without faff (no port-forwarding, no Let's Encrypt cert renewals, no opening your router to the public internet), we use **Cloudflare Tunnel**. Cloudflare gives you a stable HTTPS URL that tunnels back to YWC running on your PC. Cost: £0 ongoing, ~£8/year if you don't already own a domain.

The full architectural reasoning for this design (vs. AWS Lambda, vs. port forwarding, vs. paid services) is in `docs/decisions/0002-alexa-voice-control.md` in the repo.

---

## Phase 0 — Cloudflare account and DNS migration

You need a domain managed by Cloudflare DNS. The Tunnel feature (Phase 1) needs a stable hostname (e.g. `alexa.yourdomain.com`) that resolves to your PC.

### If you already own a domain

You can keep your existing domain registrar (e.g. Mythic Beasts, Namecheap, 123-reg) — you don't have to *transfer* the domain. You just change the **nameservers** to point at Cloudflare. Cloudflare then handles DNS lookups; your registrar still owns the domain.

### If you don't own a domain

The cheapest legitimate option is to register one through **Cloudflare Registrar** itself — they sell at wholesale prices (around £8/year for `.com`, varies for other TLDs). Domains bought through Cloudflare are automatically on Cloudflare DNS.

The rest of this section assumes you already own a domain at an external registrar and need to migrate the DNS. Skip Steps 4–7 if you're using Cloudflare Registrar from the start.

### Step 1 — Survey your current DNS

Before changing anything, look at what's currently configured. From PowerShell:

```powershell
nslookup -type=ns  yourdomain.com
nslookup -type=a   yourdomain.com
nslookup -type=cname www.yourdomain.com
```

Make a note of:

- Your **current nameservers** (you'll be replacing these)
- Whether your root domain points at any IPs (A records) and where they go
- Any CNAMEs (especially `www`)

If you have services like email at `@yourdomain.com`, also note the MX records:

```powershell
nslookup -type=mx yourdomain.com
```

You'll need these later in Cloudflare to preserve mail delivery.

### Step 2 — Sign up at Cloudflare

Go to **cloudflare.com** → click **Sign Up**.

> **Authentication choice:** Cloudflare offers "Continue with GitHub" / "Continue with Google" alongside email + password. **Pick email + password** rather than SSO. Reason: Cloudflare will be managing your domain's DNS, which is important infrastructure. An SSO chain through GitHub or Google means losing that infrastructure if you ever have an SSO account issue. With email + password backed by a password manager, your Cloudflare account is recoverable independently.

Use whatever email address you'd want recovery emails to go to.

### Step 3 — Cloudflare onboarding wizard

After signup, Cloudflare runs an onboarding wizard with several questions. The wizard's branching has changed over time; here's what to pick at the time of writing:

1. **Account type:** Personal project
2. **"How would you like to get started?"** → **Connect and accelerate traffic** (covers their DNS, networking, and Tunnel products)
3. **"What are you connecting and accelerating?"** → **Public websites** (even though we'll eventually use Tunnel which is technically a "Private apps" feature, the first thing we need is DNS hosting — that's a "public websites" workflow)
4. **"Connect a domain"** → next
5. Enter your domain (e.g. `yourdomain.com`) → choose the **Free** plan

None of the wizard answers actually change feature availability — they're marketing segmentation. But the suggested route gets you to the **Add a Site** flow which is what you need.

### Step 4 — Review imported DNS records

Cloudflare scans your current DNS provider and imports the records it finds. Review carefully:

- All **A**, **AAAA**, **CNAME**, and **MX** records should appear
- Compare against the output of your Step 1 `nslookup` commands
- If anything's missing, **add it manually** before continuing — particularly MX records if you receive email at the domain

If you don't receive email at the domain (e.g. it's a redirect-only domain like Colin's `mm5agm.co.uk`), Cloudflare may warn about a missing MX record — that's fine to ignore.

### Step 5 — CRITICAL: set every record to "DNS only" (grey cloud)

This is the single easiest-to-miss step in the whole process. Cloudflare defaults to **Proxied** (orange cloud icon) for new records, which routes the traffic through Cloudflare's edge — that's their core revenue feature.

**For each imported record, click the orange cloud icon to toggle it to grey (DNS only).**

Why this matters:

- If your domain currently serves content from GitHub Pages, Netlify, Vercel, or any service with its own SSL termination, proxying through Cloudflare will fight with that service's SSL setup. You'll get certificate errors or strange redirects.
- "DNS only" tells Cloudflare to do nothing more than DNS resolution — traffic still goes direct to your origin. This preserves your existing setup exactly.
- Later, when you add the `alexa.yourdomain.com` subdomain via Cloudflare Tunnel, **that** record will be proxied (orange cloud) automatically — but only that specific subdomain.

**Verify every existing record has the grey cloud, not the orange cloud, before you click Continue.**

### Step 6 — Cloudflare gives you two nameservers

Cloudflare assigns your account two unique nameservers (e.g. `dahlia.ns.cloudflare.com` and `kianchau.ns.cloudflare.com` — the names are randomly assigned per account, so yours will differ).

Copy both, you'll need them in Step 7.

Cloudflare also shows two **optional recommendations**:

- **"Make sure DNSSEC is off"** at your current registrar. If your registrar has DNSSEC enabled for the domain, turn it **off** before changing nameservers; otherwise DNS validation can break during the propagation window. You can re-enable DNSSEC through Cloudflare later if you want.
- **"Only allow Cloudflare IP addresses at your origin"** — **skip this entirely** if your records are grey-cloud (DNS only). The recommendation only applies to proxied traffic, which yours isn't.

### Step 7 — Change nameservers at your registrar

Log in to your current domain registrar (Mythic Beasts, Namecheap, 123-reg, etc.). Find the **nameserver delegation** for your domain. This is usually on a different page from the DNS records editor — look for "Nameservers" or "Delegation" under the domain management section, not the DNS records section.

Replace the existing nameservers with the two Cloudflare gave you. Save.

**For Mythic Beasts customers specifically:** the page is reached via "Click here to use the Mythic Beasts nameservers" — confusingly named, but it's the toggle between "use Mythic Beasts defaults" and "use custom nameservers". Click into it, edit the nameserver values inline, leave the "IPs (if required)" column blank (no glue records needed for Cloudflare's nameservers), save.

### Step 8 — Wait for propagation

DNS propagation typically completes in **5 minutes to 24 hours**, occasionally up to 48 hours. Cloudflare polls automatically.

While you wait:

- You can close the browser; Cloudflare keeps polling in the background
- You'll receive an **email** from Cloudflare when the nameservers are confirmed and the domain shows "Active"
- The web service at your domain stays up the whole time — both old and new nameservers serve identical records, so users see no downtime

You can also check progress manually with:

```powershell
nslookup -type=ns yourdomain.com
```

When both lines show the Cloudflare nameserver names, propagation has reached your local DNS resolver.

### Step 9 — Verify nothing broke

Once Cloudflare shows the domain as **Active**:

1. Visit your domain in a browser. If it was serving a website (or doing a redirect), confirm that still works.
2. If you got an unexpected response on first try (e.g. Bing search instead of the expected site), it's almost certainly a browser cache hiccup from the propagation window. **Ctrl+F5** to hard-refresh, or open a private/incognito window. Often clears immediately.
3. Confirm HTTPS still works (a meta-refresh or actual content should load over `https://`, not just HTTP).

A useful sanity check:

```powershell
curl.exe -L https://yourdomain.com
```

(`curl.exe` not `curl` — PowerShell aliases the bare word to `Invoke-WebRequest` which has different behaviour.)

If the response is what you expect, **Phase 0 is complete.**

---

## Phase 1 — Cloudflare Tunnel install

This is the part that puts your YWC instance behind a public HTTPS URL (`alexa.yourdomain.com`) without opening any ports on your router or buying a TLS certificate. The Cloudflare Tunnel daemon (`cloudflared`) makes an outbound connection from your PC to Cloudflare's edge; Cloudflare receives requests from Amazon's Alexa service, passes them through that outbound tunnel, and forwards them to `http://localhost:8080` on your PC. No inbound port. No certificate management. Free.

The procedure below was walked end-to-end on a Windows 11 PC running cloudflared 2026.6.0. Two small Windows-specific gotchas required workarounds; both are explained in-line.

### Step 1 — Download `cloudflared`

Go to the [cloudflared releases page](https://github.com/cloudflare/cloudflared/releases/latest) and download the file named **`cloudflared-windows-amd64.msi`** (the Windows 64-bit MSI installer — easier than the standalone `.exe` because it adds `cloudflared` to your PATH automatically). About 25 MB.

Run the `.msi`. The installer is silent — no UI to click through, just a brief UAC prompt. Takes 5–10 seconds.

**Verify by opening a fresh PowerShell window** (existing windows won't have the updated PATH):

```powershell
cloudflared --version
```

You should see something like `cloudflared version 2026.6.0 (built 2026-06-08T11:16 UTC)`. The exact version doesn't matter — any recent release will work.

### Step 2 — Authenticate with Cloudflare

This downloads a permanent credentials file that lets `cloudflared` create and manage tunnels under your Cloudflare account. One-time operation per machine.

```powershell
cloudflared tunnel login
```

What happens:

1. PowerShell prints a URL (`https://dash.cloudflare.com/argotunnel?callback=...`) and tries to open it in your default browser. If it doesn't open automatically, copy the URL and paste it into a browser manually.
2. The Cloudflare page is titled **"Authorize Cloudflare Tunnel"** and lists every domain on your account. Click your domain (e.g. `yourdomain.com`) to select it.
3. Click the **Authorize** button.
4. The browser shows a success page — you can close the tab.
5. Back in PowerShell, you'll see something like:

   ```
   You have successfully logged in.
   If you wish to copy your credentials to a server, they have been saved to:
   C:\Users\<you>\.cloudflared\cert.pem
   ```

The `cert.pem` file is now in your `.cloudflared` folder. **Don't share it** — it's full account-level access.

### Step 3 — Create the tunnel

```powershell
cloudflared tunnel create ywc-alexa
```

The name `ywc-alexa` is a label — purely cosmetic. Use whatever you find readable; the rest of this guide assumes `ywc-alexa`.

Output looks like:

```
Tunnel credentials written to C:\Users\<you>\.cloudflared\<UUID>.json.
Created tunnel ywc-alexa with id <UUID>
```

**Note the UUID down** — it's a long hex string like `e4880ccd-1578-4616-97bd-46be6fcc219f`. You'll need it in the next step.

A second file now exists in `.cloudflared\` named `<UUID>.json` — that's the tunnel's per-tunnel credentials (separate from `cert.pem`).

### Step 4 — Create the config file

Create the file `C:\Users\<you>\.cloudflared\config.yml` (any text editor — Notepad is fine). Contents:

```yaml
tunnel: <your-UUID-from-step-3>
credentials-file: C:/Users/<you>/.cloudflared/<your-UUID-from-step-3>.json

ingress:
  # All requests to alexa.yourdomain.com get forwarded to YWC on localhost:8080.
  - hostname: alexa.yourdomain.com
    service: http://localhost:8080

  # Catch-all required by cloudflared: any request not matching a hostname
  # above returns 404 instead of being silently routed somewhere unexpected.
  - service: http_status:404
```

Replace `<your-UUID-from-step-3>` with the UUID you noted, `<you>` with your Windows username, and `alexa.yourdomain.com` with the public hostname you want. Use forward slashes in the credentials-file path even on Windows — YAML treats backslashes as escape characters in unquoted strings.

### Step 5 — Route DNS to the tunnel

```powershell
cloudflared tunnel route dns ywc-alexa alexa.yourdomain.com
```

This creates a CNAME record in Cloudflare's DNS that points your public hostname at the tunnel. Output:

```
INF Added CNAME alexa.yourdomain.com which will route to this tunnel tunnelID=<your-UUID>
```

Behind the scenes, Cloudflare's DNS adds a CNAME proxied through the orange-cloud — `alexa.yourdomain.com` → `<UUID>.cfargotunnel.com`. SSL is terminated at the Cloudflare edge; you don't manage any certificates.

### Step 6 — Test the tunnel in foreground mode

Before installing as a Windows service (where errors get hidden), test the tunnel manually so you can see any problems live.

**Two PowerShell windows needed.**

**Window 1**: start YWC normally (`Start Menu → Yaesu Web Control` if you installed the MSI). Confirm `http://localhost:8080` works in a browser as it always does.

**Window 2**: a fresh PowerShell window (separate from Window 1):

```powershell
cloudflared tunnel run ywc-alexa
```

PowerShell starts printing log lines. Watch for:

1. `INF Starting tunnel tunnelID=<UUID>`
2. `INF Registered tunnel connection ... locationName=<some-airport-code>` — typically four of these, one per Cloudflare edge data centre your PC's closest to (in the UK you might see `lhr15` / `man01`)
3. A `CONNECTIVITY PRE-CHECKS` table showing DNS, UDP, TCP, and Cloudflare API as `PASS`
4. `SUMMARY: Environment is healthy. cloudflared will use 'quic' as primary protocol.`

The output keeps scrolling slowly with periodic heartbeats. **Don't close Window 2** — that would stop the tunnel.

### Step 7 — Phone test

This verifies the whole chain — your phone → mobile network → Cloudflare edge → tunnel → your PC → YWC.

**Critical: use your phone's mobile data, not your home wifi.** Wifi might route through your local network and short-circuit the public-internet path you're trying to validate. Open Settings on your phone → turn wifi OFF → confirm you're on 4G/5G with a real signal.

In your phone's browser, go to `https://alexa.yourdomain.com`.

Expected: YWC's main page loads. You can interact with it from the phone — click the spectrum to retune, change bands, etc. The HTTPS lock icon shows as secure (Cloudflare provides the TLS cert automatically).

**Don't be alarmed if Window 2 (the cloudflared terminal) doesn't show new log lines per request** — cloudflared doesn't log every successful HTTP at default verbosity. The phone working IS the verification.

If the phone test passes: Phase 1 is fundamentally working. Press **Ctrl+C** in Window 2 to stop the foreground tunnel and proceed to Step 8.

### Step 8 — Install as Windows service

The foreground tunnel stops when you close the PowerShell window or reboot Windows. For permanent operation, install cloudflared as a Windows service.

**This requires an Administrator PowerShell** — close any existing PowerShell windows first, then Start menu → type "PowerShell" → right-click "Windows PowerShell" → **Run as administrator** → approve the UAC prompt.

```powershell
cloudflared service install
```

Output:

```
INF Installing cloudflared Windows service
INF cloudflared agent service is installed windowsServiceName=Cloudflared
INF Agent service for cloudflared installed successfully windowsServiceName=Cloudflared
```

Verify:

```powershell
Get-Service cloudflared
```

Status: Running, StartType: Automatic. Service name is `Cloudflared` (display name `Cloudflared agent`).

### Step 9 — Two Windows-specific gotchas to fix

This is where Windows differs from Linux/macOS guides you might find online. **Skip these and the service will run but the tunnel won't actually work.**

#### Gotcha 1: LocalSystem has no `.cloudflared\` folder

The Windows service runs under the **LocalSystem** account, which has its own user profile separate from yours. Your `C:\Users\<you>\.cloudflared\` folder is **invisible to LocalSystem**, so the service starts up with no config, no credentials, and no idea what tunnel to run.

Symptom: phone test returns `Error 1033: Cloudflare Tunnel error` from your public URL, and the Cloudflare dashboard shows the tunnel as offline even though `Get-Service` reports it Running.

Fix — copy the config and credentials into LocalSystem's profile. In the Administrator PowerShell:

```powershell
New-Item -ItemType Directory -Path "C:\Windows\System32\config\systemprofile\.cloudflared" -Force
Copy-Item "C:\Users\<you>\.cloudflared\*" -Destination "C:\Windows\System32\config\systemprofile\.cloudflared\" -Force
Get-ChildItem "C:\Windows\System32\config\systemprofile\.cloudflared\"
```

The `Get-ChildItem` should now show three files: `cert.pem`, `config.yml`, and `<UUID>.json`.

#### Gotcha 2: Service binary path has no `tunnel run` arguments

`cloudflared service install` registers the service with **just the executable path and no arguments**, so when the service starts, cloudflared runs with no instructions and exits immediately. Windows treats the exit as a crash and retries; the cycle continues until Windows gives up.

Symptom: in the Windows Event Log (filter: Source = `cloudflared*`), you see repeated `Cloudflared service starting` / `Cloudflared service arguments: [...cloudflared.exe]` lines with no `tunnel run` in the arguments. The service spends most of its time stopped.

Fix — modify the service binary path to include the `tunnel run ywc-alexa` arguments. PowerShell's `--%` stop-parsing operator is essential here; without it, PowerShell mangles the quoting and `sc.exe` rejects the command:

```powershell
Stop-Service cloudflared -Force -ErrorAction SilentlyContinue
Stop-Process -Name cloudflared -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

sc.exe --% config Cloudflared binPath= "\"C:\Program Files (x86)\cloudflared\cloudflared.exe\" tunnel run ywc-alexa"

# Verify the new path
(Get-CimInstance Win32_Service -Filter "Name='Cloudflared'").PathName
```

The verify line should now show:

```
"C:\Program Files (x86)\cloudflared\cloudflared.exe" tunnel run ywc-alexa
```

(Note the `tunnel run ywc-alexa` suffix — that's the fix.)

Start the service and confirm it stays running:

```powershell
Start-Service cloudflared
Start-Sleep -Seconds 5
Get-Service cloudflared
```

Status should now be **Running** and stay running.

### Step 10 — Final verification

Retest the phone on mobile data: `https://alexa.yourdomain.com`. YWC's main page should load same as in Step 7. You're now done — the tunnel survives reboots automatically, runs on Windows startup, and routes the public URL to your local YWC.

**Phase 1 complete.** Your YWC is now reachable from anywhere on the internet at the URL you chose, via a tunnel that needs no router configuration, no port forwarding, no certificate management, and no monthly fees.

The Alexa Skill setup in Phase 2 will use this URL as its webhook endpoint.

---

## Phase 2 — Alexa Skill setup

The YWC-side endpoint at `https://yourdomain.com/api/alexa` is already built and ready (see "What's in YWC" below). What's left is creating the Alexa Skill in Amazon's Developer Console that POSTs to it.

### What's in YWC

A REST endpoint at `/api/alexa` that accepts Alexa Skill JSON requests. The endpoint is **dormant by default** — `Settings.AlexaEnabled` is `false` on every install, so a fresh YWC user who hasn't gone through this guide is not exposing a voice-controlled rig surface.

When you turn it on (Phase 2 step 4 below), four intents are handled:

| Intent | Voice example | What it does |
|---|---|---|
| `SetBandIntent` | "go to 40 metres" | Tunes VFO A to the band's default frequency |
| `SetFrequencyIntent` | "set frequency to 14.074 megahertz" | Tunes VFO A to that exact frequency |
| `SetModeIntent` | "set mode to USB" | Changes VFO A's mode (USB / LSB / CW / FT8 / etc.) |
| `RigStatusIntent` | "what's the rig status" | Speaks current VFO A frequency, band, mode, and S-meter |

By design there is **no transmit-by-voice intent in v1** — voice-triggered transmit has regulatory and safety concerns that need a more deliberate UX before being exposed. Other things explicitly not in v1: dual-VFO addressing (always VFO A), memory recall, per-mode settings.

### How the endpoint is protected

Amazon signs every Alexa Skill request with their private key. YWC verifies the signature on each incoming request:

1. The `SignatureCertChainUrl` HTTP header is validated — must be `https://s3.amazonaws.com/echo.api/...` (anywhere else is rejected)
2. The certificate chain at that URL is fetched (cached after the first fetch) and validated against Windows' trusted-root store
3. The leaf certificate's Subject Alternative Name must include `echo-api.amazon.com`
4. The SHA-256 RSA signature in the `Signature` header is verified against the raw request body bytes
5. The body's timestamp must be within 150 seconds of now (replay protection)

If any step fails, the request gets a `400 Bad Request` and is logged. Without all five checks passing, an attacker who learns your public tunnel URL would not be able to drive your radio by sending fake JSON.

For local testing before going live, an `AlexaSkipSignatureVerification` setting bypasses the signature check — see the warning in step 4 below.

### Steps to create the Skill

The Amazon Developer Console wizard branches a lot. These are the exact steps from a real walkthrough on 2026-06-13 with the wording and choices that worked. The console UI changes over time — if labels differ slightly, the *intent* of each step is what matters.

#### Step 1 — Amazon Developer account

The skill must live under an Amazon account that's also paired to your Echo (if you have one), otherwise testing on a real device requires Beta Testing invitations and clicked email links. To find which email your Echo is on: Alexa app → More → Settings → Your Account.

1. Go to https://developer.amazon.com/alexa/console/ask
2. Sign in with the same email as your Echo / shopping account
3. If you've never developed before, you land on a **Developer Profile registration form** — fill it in:
   - Full Name: your real name (appears on skill metadata)
   - Company Name: leave blank or use your callsign
   - Country, address, phone: required (Amazon need it even for free skills)
   - Customer support email: your *public-facing* email (e.g. `you@example.com`) — this is shown in the Skill Store listing for users to contact for support
   - "Will you collect payments?" → **No**
   - "Will your skill be directed to children under 13?" → **No**
   - Tick **"Building Alexa experiences"** under interests; leave Fire TV / Mobile and Marketing unticked
4. Agree to the Amazon Developer Services Agreement → Submit
5. If you see an **"Account Identity Verification Failed"** banner — ignore it. That's the Amazon **Appstore** (apps for Fire tablets), nothing to do with Alexa. Skill creation works fine without it. Identity verification is only required when you eventually submit a skill for **public release**; for development and personal-Echo use you don't need it.
6. From the main console, click **Alexa Skills Kit** (not Alexa Voice Service — that's for embedding Alexa into hardware you build)

#### Step 2 — Create the Skill

1. Click **"Create Skill"** (top-right, blue)
2. **Skill name:** `Yaesu Web Control` (no quotes, just the words). This is the display name in the Alexa app and Skill Store — separate from the spoken invocation name set later.
3. **Primary locale:** `English (UK)` for UK users / `English (US)` for US, etc. This decides which voice model Amazon uses.
4. Click **Next**
5. **Type of experience:** Select **"Other"** (everything else is for fixed pre-built models — news, smart home etc., none fit a custom radio-control skill)
6. **Choose a model:** **"Custom"** (lets us define our own intents)
7. **Hosting services:** **"Provision your own"** — your backend is the YWC instance on your PC behind the cloudflared tunnel; not Alexa-Hosted Lambda
8. Click **Next**
9. **Template:** **"Start from Scratch"** (other templates load sample intents you'd have to delete)
10. Click **Next** → **Create Skill** (review page)
11. Wait 30-60s for provisioning. You land on the skill's **Build** tab.

#### Step 3 — Set the invocation name

The **invocation name** is what users SAY to call the skill. Amazon's rules:
- Lowercase, 2+ words preferred
- No third-party trademarks (so **NOT** "yaesu web control" — "Yaesu" is a registered trademark of Yaesu Musen Co. Ltd. Amazon's certification reviewers will reject this for public release, and the console flags it during entry)
- No Amazon wake words (alexa, echo, amazon, computer)
- No numbers, no special characters

**Recommended:** `my rig`

1. Build tab → left sidebar → **Invocations** → **Skill Invocation Name**
2. Type `my rig`
3. **Save Model** (top)

The brand-warning text under the field is generic boilerplate shown to everyone — it doesn't mean Amazon objected to "my rig" specifically.

#### Step 4 — Configure the endpoint

This points the skill at your cloudflared tunnel.

1. Left sidebar → **Endpoint**
2. Select **HTTPS** (not AWS Lambda)
3. **Default Region URL:** `https://yourdomain.com/api/alexa` (use your actual domain from Phase 1)
4. **Select SSL certificate type:** **"My development endpoint is a sub-domain of a domain that has a wildcard certificate from a certificate authority"**

   This is the **non-obvious correct choice** for a cloudflared tunnel. Your subdomain (e.g. `alexa.mm5agm.co.uk`) is served by Cloudflare's *wildcard* certificate (`*.mm5agm.co.uk`), not its own dedicated cert — so the wildcard option is the one Amazon expects.

   Choosing **"My development endpoint has a certificate from a trusted certificate authority"** instead looks plausible (Cloudflare is publicly-trusted, after all) but causes Amazon to reject the request before it ever leaves Amazon's edge — the simulator returns "I am unable to reach the requested skill" with `skillExecutionTimeInMilliseconds: 47` and YWC's log shows no request arrived. Took Amazon developer support case #20830059391 to identify this; saving you the same dead-end.
5. Leave North America / Europe and India / Far East **blank** — Default Region is the catch-all, and Cloudflare's edge network handles geographic routing automatically
6. **Save Endpoints** (top-right)

#### Step 5 — Upload the interaction model

YWC ships a ready-made interaction model JSON at [`docs/alexa/interaction-model.json`](docs/alexa/interaction-model.json) in the repository. Uploading it takes about one minute and replaces around thirty minutes of manual entry for the two custom slot types (`BAND_NAME`, `MODE_NAME`) and four intents (`SetBandIntent`, `SetFrequencyIntent`, `SetModeIntent`, `RigStatusIntent`).

1. Open `docs/alexa/interaction-model.json` from your local clone of the YWC repository (or [view the latest version on GitHub](https://github.com/mm5agm/Yaesu_Web_Control/blob/feature/alexa-voice-control/docs/alexa/interaction-model.json) and use the **Copy raw file** button)
2. **Select all** (`Ctrl+A`) and **copy** (`Ctrl+C`)
3. In the Alexa Developer Console, left sidebar → **JSON Editor** (under Interaction Model)
4. **Select all** in the editor pane and **paste** your copied JSON over the existing content
5. **Save Model** (top of page)

What this gives you (matches the manual setup in Appendix A below, exactly):

- Invocation name: `my rig`
- Two custom slot types — `BAND_NAME` (160m through 70cm, with synonyms) and `MODE_NAME` (USB/LSB/CW/AM/FM/RTTY/FT8/data/PSK)
- Four intents with full sample utterances, including the short single-word `status` form

**To use a different invocation name** (e.g. `my shack`, `hf radio`), edit the `invocationName` field in the JSON before pasting. Amazon's rules: lowercase only, two-word names cannot contain articles (`a`/`an`/`the`) or prepositions (`to`/`for`/`in`/etc), no third-party trademarks like Yaesu.

If the JSON Editor flags a validation error after paste, you've likely got an extra character or partial copy. Make sure the editor pane contains nothing else and that the paste starts with `{` and ends with `}`.

#### Step 6 — Build the skill

1. **Build Skill** (top-right, blue) — compiles your interaction model and trains Alexa's voice recogniser
2. Wait 1-3 minutes for "Build Successful" banner

If the build fails with utterance conflicts or schema errors, Amazon shows them on screen with line references — fix and rebuild.

#### Step 7 — Enable the YWC endpoint

Currently no UI for this exists in YWC's Settings page (TODO before any public release). Manually add the keys to `appsettings.user.json`:

```powershell
$path = Join-Path $env:APPDATA 'MM5AGM\Yaesu Web Control\appsettings.user.json'
$json = Get-Content $path -Raw | ConvertFrom-Json
$json | Add-Member -NotePropertyName 'AlexaEnabled' -NotePropertyValue $true -Force
$json | Add-Member -NotePropertyName 'AlexaSkipSignatureVerification' -NotePropertyValue $false -Force
$json | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding utf8
```

**Restart YWC** so the new setting is read.

If `AlexaEnabled` is missing or `false`, every request to `/api/alexa` returns **404 silently** — that's by design (no probing surface for attackers), but it makes debugging confusing. If the simulator can't reach the skill, this is the first thing to check.

#### Step 8 — Test in the simulator

1. Go to the **Test** tab (top of skill page)
2. Set **"Skill testing is enabled in:"** to **Development**
3. In the chat input, type `ask my rig for status` and press Enter
4. Within 5 seconds, expect a spoken response like *"VFO A is on 14.074 megahertz in the 20m band, mode USB."*
5. If the response is **"I am unable to reach the requested skill"**, see the gotcha section below

#### Step 9 — Common gotchas

**Gotcha 1: "I am unable to reach the requested skill" with no requests in YWC's log**

A persistent failure where the simulator returns this message every time, the Device Log shows `code: SKILL_ENDPOINT_ERROR` / `error.type: INVALID_RESPONSE`, and `skillExecutionTimeInMilliseconds: 47` (or any value far shorter than a real cross-continent round-trip). The 47-ms timing is the key signal — Amazon's request is failing immediately, before any real HTTP round-trip could complete.

A full diagnostic checklist is in **Step 10** below. Common root causes ordered by frequency:

1. **Wrong SSL certificate type** in the endpoint config — Phase 2 Step 4. The "trusted certificate authority" option looks plausible but causes Amazon to reject the request before it leaves their edge. The wildcard sub-domain option is correct. This is what Amazon developer support case #20830059391 ultimately diagnosed; if you're seeing the 47 ms timing pattern this is the first thing to check.
2. `AlexaEnabled` not set to `true` in `appsettings.user.json` — the controller returns 404 silently by design (Step 8)
3. PC clock skew breaking the 150 s signature timestamp window — fixable with `w32tm /resync`

**Gotcha 2: Yaesu trademark in invocation name**

Don't use "Yaesu" anywhere in the invocation name (what users say). Amazon's review process rejects third-party trademarks. The skill *display* name "Yaesu Web Control" is fine — but invocation must be brand-neutral. Use `my rig`, `radio control`, `ham radio`, or similar.

**Gotcha 3: Signature verification fails with clock skew**

The YWC endpoint rejects requests where the body timestamp is more than 150 seconds from current time. If your PC's clock is off (no NTP sync, or laptop just woken from sleep), Amazon's correctly-timestamped requests look stale and get a 400 with `"Alexa request rejected: timestamp ... is Ns away from now"` in YWC's log.

Fix: `w32tm /resync` in admin PowerShell, or check Windows time settings.

**Gotcha 4: Intent name casing**

Intent names are case-sensitive on both sides. `setBandIntent`, `Setbandintent`, or `SET_BAND_INTENT` will all be defined fine in the console but won't dispatch to anything in `AlexaController.DispatchIntent`. Use the exact casing: `SetBandIntent`, `SetFrequencyIntent`, `SetModeIntent`, `RigStatusIntent`.

**Gotcha 5: Endpoint change requires rebuild**

If you change the endpoint URL after the first build, you must click **Build Skill** again. Amazon caches the endpoint in the compiled model.

#### Step 10 — Diagnostic checklist for "I am unable to reach the requested skill"

When the simulator stubbornly refuses to reach your endpoint, work through this list in order. The aim is to isolate which hop in the chain (Amazon → Cloudflare → tunnel → YWC) is failing — because the same surface-level error message can mean very different things at each layer.

**A. Confirm YWC is reachable from outside the local PC.** Use https://reqbin.com (or any external HTTP testing service) to POST `{"test": "x"}` to your endpoint, e.g. `https://yourdomain.com/api/alexa`.

- Expected: HTTP **400 Bad Request** with a body containing `"title":"Bad Request"`. That's YWC's signature verifier rejecting an unsigned test request — and crucially, the request did reach YWC.
- If you get a 502 / timeout / connection refused: YWC isn't running, or the tunnel isn't routing.
- If you get 404: `AlexaEnabled` is `false` (the controller deliberately returns 404 when disabled).

**B. Confirm Cloudflare isn't blocking the traffic.** Cloudflare dashboard → mm5agm.co.uk → **Security → Settings**.

- **Bot Fight Mode** should be **OFF** (the default in newer Cloudflare accounts). The UI used to be at "Security → Bots", now it's a toggle inside "Security → Settings" filtered by "Bot traffic".
- **Security → Analytics** — look for the "Suspicious activity" counter and the "Requests mitigated by Cloudflare" view. If both are zero, Cloudflare isn't blocking anything.

**C. Confirm the TLS configuration.** Cloudflare dashboard → mm5agm.co.uk → **SSL/TLS → Edge Certificates**.

- Universal SSL should be **Active**, and the **Hosts** column should include both `mm5agm.co.uk` and `*.mm5agm.co.uk` (the wildcard covers your `alexa.` subdomain).
- **Minimum TLS Version** should be **1.0** (the default — permissive). Setting it to 1.3 will break Amazon's TLS client.
- **Always Use HTTPS** can be on; it only affects HTTP→HTTPS redirect, not Amazon's already-HTTPS calls.

**D. Confirm the cloudflared tunnel is healthy.** From an admin PowerShell:

```powershell
cloudflared tunnel info ywc-alexa
```

Look for at least one active connector with recent `opened_at` timestamps. Four connections (typically `lhr*` and `man*` colos in the UK, or your region's equivalent) is normal.

**E. Confirm the Amazon-side endpoint configuration.** In the Alexa Developer Console → your skill → **Build** → **Endpoint**.

- HTTPS radio button selected (not Lambda)
- Default Region URL matches your tunnel hostname character-by-character
- SSL certificate type: **"My development endpoint is a sub-domain of a domain that has a wildcard certificate from a certificate authority"** (the wildcard option — see Phase 2 Step 4 for why this rather than the "trusted CA" option that looks more obvious)
- After any change here, click **Save Endpoints** and then **Build Skill** (top right) — endpoint changes require a rebuild

**F. Read the Device Log.** Test tab → tick the **Device Log** checkbox at the top. Then fire a test utterance. Look for a `SkillDebugger.CaptureError` event — expand its JSON. The `invocationRequest.endpoint` field shows the URL Amazon actually tried (verify it matches your config), and `skillExecutionTimeInMilliseconds` indicates how long Amazon waited before giving up. Values under ~100 ms mean Amazon didn't actually round-trip to your origin — the failure was at TLS handshake or earlier.

**G. Check YWC's log for arriving requests.** The log lives at `%APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-YYYYMMDD.log`. Search for `AlexaController`:

```powershell
$log = "C:\Users\<you>\AppData\Roaming\MM5AGM\Yaesu Web Control\logs\ywc-$(Get-Date -Format yyyyMMdd).log"
Select-String -Path $log -Pattern 'AlexaController' | Select-Object -Last 5
```

- If you see `Alexa signature verification failed: ...` — Amazon's request arrived but the signature check failed. Likely PC clock skew (Gotcha 3) or bypass disabled when it shouldn't be.
- If you see `Alexa intent: <IntentName>` — the request arrived and dispatched. The simulator-side failure is post-response, e.g. a malformed JSON response from YWC.
- If you see nothing at all despite multiple simulator attempts — Amazon's request isn't reaching YWC. Re-check A–F above; the gap is somewhere before YWC.

**H. If the failure is "Amazon's request never arrives" despite local-side checks (A, B, C, D, G) passing**, the most likely cause is **wrong SSL certificate type in the Amazon endpoint config** (covered in check E). The "trusted certificate authority" option needs to be the **wildcard sub-domain** option for cloudflared tunnels — see Phase 2 Step 4 for the full explanation. This is what causes the 47 ms timing pattern: Amazon refuses to dial out at all because they think the cert configuration is wrong.

If you've verified check E is correctly set and the failure persists, only then is a developer support case warranted (Alexa Developer Console → Support → Contact Us → Alexa Skill Building → Can't Launch Skill) with these specifics:

- Skill ID
- Endpoint URL
- The Device Log `SkillDebugger.CaptureError` JSON including `skillExecutionTimeInMilliseconds`
- Evidence the endpoint is reachable from outside (paste the ReqBin result from step A)
- Steps already tried (rebuild, fresh browser session, all the above checks)

Amazon typically responds within 1-2 business days. Case #20830059391 is the canonical example — the resolution turned out to be the SSL setting, which is now check E above.

### Appendix A — Manual model entry (alternative to Step 5)

If you'd rather enter the interaction model by hand than upload the JSON — perhaps to understand what each piece does, or because you want to customise as you go — these are the equivalent manual steps. Skip this entire appendix if you used Step 5.

**A.1 — Create slot type `BAND_NAME`**

1. Left sidebar → **Slot Types** → **+ Add Slot Type** → **Create custom slot type**
2. Name: `BAND_NAME` (uppercase, underscore — convention)
3. Add these values (one per row, leave ID blank):

| Value | Synonyms (one per box, optional but recommended) |
|---|---|
| `160 metres` | `160 meters`, `160m`, `one sixty`, `top band` |
| `80 metres` | `80 meters`, `80m`, `eighty metres`, `eighty` |
| `60 metres` | `60 meters`, `60m`, `sixty metres`, `sixty` |
| `40 metres` | `40 meters`, `40m`, `forty metres`, `forty` |
| `30 metres` | `30 meters`, `30m`, `thirty metres`, `thirty` |
| `20 metres` | `20 meters`, `20m`, `twenty metres`, `twenty` |
| `17 metres` | `17 meters`, `17m`, `seventeen metres`, `seventeen` |
| `15 metres` | `15 meters`, `15m`, `fifteen metres`, `fifteen` |
| `12 metres` | `12 meters`, `12m`, `twelve metres`, `twelve` |
| `10 metres` | `10 meters`, `10m`, `ten metres`, `ten` |
| `6 metres` | `6 meters`, `6m`, `six metres`, `six`, `magic band` |
| `4 metres` | `4 meters`, `4m`, `four metres`, `four` |
| `2 metres` | `2 meters`, `2m`, `two metres`, `two`, `V H F` |
| `70 centimetres` | `70 centimeters`, `70cm`, `seventy centimetres`, `seventy cm`, `U H F` |

(2m and 70cm currently get a polite "not supported on this radio" response from the YWC backend — they're added now for future FT-991A / FT-710 support without needing to retrain the slot type.)

4. **Save** (top)

**A.2 — Create slot type `MODE_NAME`**

1. Slot Types → **+ Add Slot Type** → Create custom slot type
2. Name: `MODE_NAME`
3. Add these values:

| Value | Synonyms |
|---|---|
| `USB` | `U S B`, `upper sideband`, `upper side band`, `upper` |
| `LSB` | `L S B`, `lower sideband`, `lower side band`, `lower` |
| `CW` | `C W`, `morse`, `morse code` |
| `AM` | `A M`, `amplitude modulation` |
| `FM` | `F M`, `frequency modulation` |
| `RTTY` | `R T T Y`, `ritty`, `radio teletype`, `teletype` |
| `FT8` | `F T 8`, `F T eight`, `eff tee eight` |
| `data` | `digital`, `digi`, `data mode`, `digital mode` |
| `PSK` | `P S K`, `phase shift keying` |

4. **Save**

**A.3 — Create the four intents**

The names below MUST match exactly — they're hardcoded in `Controllers/AlexaController.cs:172-175`.

**Intent 1: `SetBandIntent`**

1. Left sidebar → **Intents** → **+ Add Intent** → Create custom intent
2. Name: `SetBandIntent` (case-sensitive) → **Create Custom Intent**
3. **Intent Slots** section: add a slot named `band` (lowercase), set its **Slot Type** to `BAND_NAME`, leave **Multi-Value OFF**
4. **Does this intent require confirmation?** OFF — confirmation adds friction with no benefit; tuning is non-destructive
5. **Sample Utterances**:

```
set band to {band}
go to {band}
switch to {band}
tune to {band}
change to {band}
change band to {band}
```

When you type `{band}` it should auto-link to the slot. If a chooser pops up, pick **Existing → band**.

6. **Save**

**Intent 2: `SetFrequencyIntent`**

1. + Add Intent → `SetFrequencyIntent`
2. Slot: name `frequencyMHz` (exact camelCase — matches `AlexaController.cs:242`), type `AMAZON.NUMBER`
3. Sample utterances:

```
tune to {frequencyMHz} megahertz
set frequency to {frequencyMHz} megahertz
go to {frequencyMHz} megahertz
frequency {frequencyMHz} megahertz
QSY to {frequencyMHz} megahertz
QSY {frequencyMHz} megahertz
```

"megahertz" appears in every utterance to disambiguate from band selection.

4. **Save**

**Intent 3: `SetModeIntent`**

1. + Add Intent → `SetModeIntent`
2. Slot: `mode` (lowercase), type `MODE_NAME`
3. Sample utterances:

```
set mode to {mode}
switch to {mode}
switch mode to {mode}
change mode to {mode}
use {mode}
{mode} mode
```

4. **Save**

**Intent 4: `RigStatusIntent`** (no slots — easiest)

1. + Add Intent → `RigStatusIntent`
2. **No slot to add** — skip the Intent Slots section
3. Sample utterances:

```
status
rig status
status report
report status
give me the status
what's the status
what's the rig status
what is the radio doing
tell me the status
```

4. **Save**

After all four are saved, return to **Step 6 (Build the skill)** above.

### The two Alexa settings in detail

| Setting | Default | What it does |
|---|---|---|
| `AlexaEnabled` | `false` | Master switch. When `false`, every request to `/api/alexa` returns 404 — the endpoint behaves as if it doesn't exist. Turn this on only after the Skill is fully configured and tested. |
| `AlexaSkipSignatureVerification` | `false` | **Development only.** Bypasses the Amazon signature check on incoming requests. Useful for local testing with curl/Postman. Must NEVER be `true` in a production install — leaves the endpoint accepting any JSON request from anyone. |

Both settings live in `%APPDATA%\MM5AGM\Yaesu Web Control\appsettings.user.json`.

---

## Phase 3 — Using it

The invocation pattern is:

> **"Alexa, ask** *<invocation name>* *<utterance>***"**

Replacing *<invocation name>* with whatever you set in Phase 2 Step 3 — the rest of this section uses `my rig` (the default suggested above).

### Reliable command list

These are the phrasings most consistent on real Echo hardware. They've been chosen to avoid Alexa's built-in domains (smart-home, music, navigation) preempting the request.

**Get current status:**

- "Alexa, ask my rig status"
- "Alexa, ask my rig for status"
- "Alexa, ask my rig what's the status"

The short `status` form depends on having `status` as a single-word sample utterance on `RigStatusIntent`. The uploaded JSON (Step 5) already includes it; if you set the model up manually before this was documented you may need to add it.

**Change band** (supported: 160m, 80m, 60m, 40m, 30m, 20m, 17m, 15m, 12m, 10m, 6m, 4m on the FTdx101MP region 1; 2m and 70cm will return "not supported on this radio"):

- "Alexa, ask my rig to switch to forty metres"
- "Alexa, ask my rig to tune to twenty metres"
- "Alexa, ask my rig to set band to eighty metres"
- "Alexa, ask my rig to change band to fifteen metres"

**Set frequency** (the word "megahertz" is required — disambiguates from a band request):

- "Alexa, ask my rig to tune to fourteen point zero seven four megahertz"
- "Alexa, ask my rig to set frequency to seven point zero five megahertz"
- "Alexa, ask my rig to QSY to twenty one point three megahertz"

**Change mode** (supported: USB, LSB, CW, AM, FM, RTTY, FT8, data; "data" maps to DATA-U for FT8/digital):

- "Alexa, ask my rig to switch to USB"
- "Alexa, ask my rig to switch mode to CW"
- "Alexa, ask my rig to set mode to LSB"
- "Alexa, ask my rig to use data"

### Things to avoid

| Pattern | Why |
|---|---|
| `"Alexa, tell my rig …"` | The `tell` launch verb is more permissive than `ask` and Alexa is more willing to redirect the request to a built-in domain instead of your skill. Use `ask` always. |
| `"Alexa, my rig …"` (no launch verb) | Without `ask` / `tell` / `open` / `launch` / `start`, Alexa has no way to know you're addressing a custom skill — she'll treat it as natural-language input to a built-in domain. |
| `"Alexa, ask my rig to go to forty metres"` | `go to {band}` is in the sample utterances but Alexa's smart-home and navigation domains aggressively claim "go to N metres" before the skill resolution runs. Use `switch to` / `tune to` / `set band to` instead. |
| Anything with **Yaesu** in the invocation | Amazon rejects third-party trademarks during certification — even for self-use the validation may fail at skill-build time. |

### What works on simulator vs Echo

The Alexa Developer Console **simulator** and a real **Echo device** both hit the same `/api/alexa` endpoint with identical request shape. If a phrasing works in the simulator it'll work on the Echo (and vice versa). Use the simulator to iterate on new utterances quickly without having to keep saying things to your Echo.

---

## FAQ

**Q: Why do I need a domain just for voice control?**
A: Cloudflare Tunnel needs a stable hostname. Without a domain, you'd have to use Cloudflare's "Quick Tunnels" which assign a random URL on each restart — your Alexa Skill would break every time you reboot. £8/year is the price of stability.

**Q: Why can't I just install YWC and use Alexa? Why all the setup?**
A: Amazon's Skill model requires every Skill to be tied to a specific HTTPS endpoint at definition time. There's no built-in way to publish a single "YWC Voice Control" skill that talks to *your* PC at home. So every user creates their own private Skill pointed at their own home setup. This is the only way without us running a paid cloud service.

**Q: Is voice control a paid feature?**
A: No. YWC itself remains free. The infrastructure pieces (Cloudflare, Amazon Developer) are also free at the level we use them. The only potential cost is the domain (£8/year) and *only* if you don't already own one.

**Q: Can my voice commands be intercepted?**
A: All hops are TLS-encrypted: voice from Echo → Amazon TLS, Amazon → Cloudflare TLS, Cloudflare → your PC via the tunnel (also TLS). YWC additionally validates Amazon's request signature on every command — anyone who somehow obtained your tunnel URL still couldn't issue commands without Amazon's signing certificate.

**Q: My Cloudflare account got hacked — can someone control my radio?**
A: They could change DNS records, including the `alexa.` subdomain. But to actually send commands, they'd also need to compromise your Amazon Developer account (where the Skill is defined) to redirect its endpoint. Two-account compromise is a much higher bar than one. Use 2FA on both accounts.
