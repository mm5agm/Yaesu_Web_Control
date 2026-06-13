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

**[Walkthrough to follow — will document the exact Amazon Developer Console wizard flow once it's been walked through end-to-end on a real account. The Console's wizard branches frequently so verbatim steps from a real session beat generalised instructions.]**

In summary, when documented:

1. Create a custom Skill in the Alexa Developer Console (free Amazon Developer account)
2. Set the invocation name to "Yaesu Control" or similar
3. Define the four intents above with their sample utterances and slot types
4. Configure the Skill's endpoint URL to `https://yourdomain.com/api/alexa` (HTTPS, web service endpoint type)
5. In YWC's Settings, set `AlexaEnabled` to `true`. **Do not** set `AlexaSkipSignatureVerification` to true once you're past local testing.
6. Enable the Skill on your Amazon account; pair it to your Echo
7. Test by saying "Alexa, ask Yaesu Control what's the rig status"

### The two Alexa settings in detail

| Setting | Default | What it does |
|---|---|---|
| `AlexaEnabled` | `false` | Master switch. When `false`, every request to `/api/alexa` returns 404 — the endpoint behaves as if it doesn't exist. Turn this on only after the Skill is fully configured and tested. |
| `AlexaSkipSignatureVerification` | `false` | **Development only.** Bypasses the Amazon signature check on incoming requests. Useful for local testing with curl/Postman. Must NEVER be `true` in a production install — leaves the endpoint accepting any JSON request from anyone. |

Both settings live in `%APPDATA%\MM5AGM\Yaesu Web Control\appsettings.user.json`.

---

## Phase 3 — Using it

**[Documentation to follow.]**

Example commands once everything is wired:

- "Alexa, ask Yaesu Control to go to 40 metres"
- "Alexa, ask Yaesu Control to set frequency to 14.074 megahertz"
- "Alexa, ask Yaesu Control to set mode to CW"
- "Alexa, ask Yaesu Control for rig status"
- "Alexa, ask Yaesu Control to turn on the preamp"

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
