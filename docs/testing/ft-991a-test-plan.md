# FT-991A test plan

Thank you for testing YWC with your FT-991A. Your first test, with the radio
set to FT-710, showed the basics work: connect, the dial, bands, modes, the
S-meter and transmit. YWC now has the FT-991A as a model of its own, built
from the 991A CAT manual. I don't have a 991A, so this test is what tells me
whether I read the manual correctly.

You'll need a YWC build newer than v2.5.3-pre5. The FT-991A isn't in the
Radio Model list before that. Nothing in this test changes the radio's own
menus.

There are four parts, and each one is useful on its own. Do them in order,
and stop whenever you like.

## Before you start

- A Windows PC with the 991A connected by its USB cable, as before.
- A dummy load for the transmit steps (part 3).
- If you have another program that talks to the radio over CAT (a logger,
  WSJT-X and so on), close it first. Only one program can open the COM port.

Each step says **what you should see**. If you see something else, note the
step number and what happened. That's a useful result, not a failed test.

## Part 1 - the model, bands and modes

**1. Install the new build.**
Download it from https://github.com/mm5agm/Yaesu_Web_Control/releases and
install it over the old one. Your settings are kept.

**2. Choose the FT-991A.**
In **Settings**, under **Radio Connection**, set **Radio Model** to
**FT-991A**. Leave the COM port and baud rate as they were, then click
**Save**.
*What you should see:* the note under the model says 100 W (50 W on 2 m and
70 cm). Back on the main page, the frequency and mode match the radio.

**3. Band buttons.**
*What you should see:* buttons from 160m to 6m, then **2m** and **70cm**.
There's no 4m button.

**4. 2 m and 70 cm.**
Click **2m**, then **70cm**, then **20m**.
*What you should see:* the radio changes band each time. The frequency on
the page shows all the digits, for example 144.300.000 or 432.100.000.

**5. Turn the dial on 2 m.**
On 2 m, turn the radio's VFO knob.
*What you should see:* the page follows within about a second.

**6. C4FM.**
Put the radio into C4FM on its own **MODE** button.
*What you should see:* the page says **C4FM**. The old FT-710 setting would
have called it PSK.

**7. Mode from YWC.**
Choose USB, then CW, then FM, then C4FM from the mode list on the page.
*What you should see:* the radio follows each one.

## Part 2 - filters and split (receive only)

Go back to 20 m in USB for this part.

**8. IF width.**
Change **IF Width** on the page to 1800, then 2400, then 3000.
*What you should see:* the radio's WIDTH changes to the same figure each time.
In the earlier test the page showed a width of "1;". It should now show a
proper figure.

**9. IF width with NARROW on.**
Press **NARROW** on the radio, then choose 1800 on the page again.
*What you should see:* tell me what the radio shows. The manual lists some
widths under Narrow only and some under Wide only, and I'd like to know what
the radio does when YWC asks for a Wide one while NARROW is on. Then press
NARROW again to turn it off.

**10. IF shift.**
Move the **IF Shift** slider on the page to the left and right, then click
**Zero**.
*What you should see:* the radio's SHIFT follows the slider, and **Zero**
puts it back to 0.

**11. IF shift from the radio.**
Turn the radio's SHIFT control.
*What you should see:* the **IF Shift** figure on the page follows it.

**12. Split.**
Click **Split** on the page. Then click it again.
*What you should see:* the first click turns split on in the radio (it
transmits on VFO B), and the page shows **SPLIT TX**. The second click turns
it off. Also turn split on with the radio's own button and check the page
follows.

## Part 3 - transmit (into a dummy load)

**Connect the dummy load before doing any of this.**

**13. Power on HF.**
On 20 m, set the power on the page to 10 W.
*What you should see:* the radio's power setting changes to 10.

**14. Power on 2 m.**
Click **2m**. Look at the top of the power slider on the page.
*What you should see:* the slider's maximum is now 50 W, not 100 W. Click
**20m** again and it goes back to 100 W.

**15. Transmit and meters.**
On 20 m in FM, at 10 W, click the **TX** button on the VFO panel. Wait three
seconds and click it again.
*What you should see:* the radio transmits and returns to receive. Note what
the power, ALC and SWR meters on the page show. The page no longer shows a
Temperature meter, because the 991A has none.

**16. CW break-in.**
In CW, on the **CW Keyer** panel, set **Break-in** to Semi, then Full, then
move the delay slider.
*What you should see:* the radio's BK-IN TYPE menu (056) follows Semi and
Full, and its break-in delay follows the slider.

## Part 4 - RTTY and memories

**17. RTTY settings.**
Put the radio in RTTY and open the **RTTY tuner** from the page.
*What you should see:* the tuner shows the same mark tone and shift as the
radio's menu items 101 (RTTY MARK FREQ) and 100 (RTTY SHIFT PORT).

**18. Memories.**
Store a channel on the radio in C4FM if you have a free one. Then, on the
page's **Memories** screen, click **Import (Add)**. Please use **Add** and
not **Replace**, so nothing you already have in YWC is lost.
*What you should see:* your radio's channels 001-099 appear. The C4FM one
says C4FM.

## Sending me the results

Please post on https://github.com/mm5agm/Yaesu_Web_Control/discussions/86,
as before.

1. The step numbers that **didn't** do what the plan says, and what happened
   instead. You don't need to list the ones that worked.
2. Your answer to step 9 (what NARROW does).
3. The **Diagnostics** block: on the **About** page, click **Copy
   diagnostics** and paste it into your post.
4. For anything that went wrong, a log: on the **Diagnostics** page click
   **Start fresh test log**, repeat that step, click **Download test log**,
   and drag the file into your post.

Two questions from your first log, if you remember:

- The VFO A frequency flicked between 21.2699 and 21.2698 MHz about 50 times
  in 2 seconds. Were you turning the dial, or was it left alone?
- Which version of the 991A is it (China, Asia, or another region)? That
  decides which bands it allows.

Thank you. 73, Colin MM5AGM
