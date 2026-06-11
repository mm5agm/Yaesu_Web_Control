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

**[Documentation to follow as this phase is implemented on the feature branch.]**

In summary: install `cloudflared` as a Windows service, authenticate it with your Cloudflare account, create a named tunnel, route `alexa.yourdomain.com` to it, configure it to forward traffic to `http://localhost:8080` (where YWC runs).

---

## Phase 2 — Alexa Skill setup

**[Documentation to follow.]**

In summary: create a custom Skill in the Alexa Developer Console, define the intents (`GoToBandIntent`, `SetFrequencyIntent`, `SetModeIntent`, `RigStatusIntent`, etc.), point the Skill's HTTPS endpoint at `https://alexa.yourdomain.com/api/alexa`, enable the Skill on your Echo.

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
