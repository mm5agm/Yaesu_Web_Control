# FT-991A test plan

Thank you for testing YWC with your FT-991A. I don't have a 991A, so your
results are what a proper 991A profile will be built from.

YWC doesn't list the FT-991A yet, so for this test you'll tell it the radio
is an FT-710. That's the closest radio it knows: single receiver, and it
sends frequencies to the radio in the same form the 991A expects. Some
things will be wrong under that setting, and finding out which ones is the
point of the test. Nothing here changes the radio's own menus.

It takes about 30 minutes. You don't need to do every step. Doing part 1
only is still very useful.

## Before you start

- A Windows PC with the 991A connected by its USB cable.
- A dummy load for the transmit steps (part 2).
- Stay on **HF and 6 m** throughout. YWC doesn't handle 2 m or 70 cm yet,
  and under the FT-710 setting it won't tune there.
- If you use another program that talks to the radio over CAT (a logger,
  WSJT-X, and so on), close it first. Only one program can open the radio's
  COM port.

For each step there's **what you should see**. If you see something else,
note the step number and what happened. That's a useful result, not a
failed test.

## Part 1 - connect and receive

**1. Install YWC.**
Go to https://github.com/mm5agm/Yaesu_Web_Control/releases and download
`Yaesu_Web_Control_Setup.exe` from the newest release at the top of the
page (it may be a pre-release; that's fine). Run it.
*What you should see:* YWC starts and opens a page in your browser at
http://localhost:8080.

**2. Find the radio's COM port.**
Open Windows Device Manager and expand **Ports (COM & LPT)**. The 991A shows
two ports. Note the number of the one called **Enhanced COM Port**. That's
the CAT port. The **Standard** one isn't used here.
*If you don't see either:* install the Silicon Labs CP210x driver from
https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads
and **reboot the PC**. Skipping the reboot is the most common reason this
step fails.

**3. Check the radio's CAT RATE.**
On the 991A, look up the **CAT RATE** menu item and note the value (for
example 38400).

**4. Set up YWC.**
In YWC, open **Settings** (top of the page). Under **Radio Connection**:
- **Radio Model:** choose **FT-710**. Please don't pick any of the others.
  Some of them send frequencies in a form the 991A ignores, so tuning would
  appear not to work.
- **Serial Port:** the Enhanced COM port from step 2 (for example COM5).
- **Baud Rate:** the same as the radio's CAT RATE from step 3.

Click **Test Connection**.
*What you should see:* a green tick. Then click **Save**.

**5. Main page.**
Go back to the main control page.
*What you should see:* the frequency on the page matches the radio's
display, and so does the mode.

**6. Turn the VFO knob on the radio.**
*What you should see:* the frequency on the page follows it within about a
second.

**7. Change band from YWC.**
Click a band button on the page, for example 20m, then 40m. (If you're in
Europe you'll see a 4m button too. The 991A has no 4 m, so skip that one.)
*What you should see:* the radio changes band each time.

**8. Type a frequency.**
Click the small keypad button just to the right of **MHz** beside the
frequency. Type `14.074` on the number pad that opens and confirm it.
*What you should see:* the radio goes to 14.074.000.

**8a. Tune from the page.**
Click the kHz digit of the frequency on the page, then roll the mouse wheel.
*What you should see:* the radio tunes along with it.

**9. Mode from YWC.**
Change the mode on the page: USB, then LSB, then CW, then AM, then FM.
*What you should see:* the radio follows each one.

**10. Mode from the radio.**
Change the mode on the radio's own buttons.
*What you should see:* the page follows.

Also note any mode that shows wrongly on the page (for example the radio
says DATA-USB and the page says something else). The mode list is the
FT-710's, so one or two may not match.

**11. S-meter.**
Tune to a strong signal or a busy part of a band.
*What you should see:* the S-meter on the page moves with the signal. Note
roughly how it compares with the radio's own meter (for example "the page
shows S7 when the radio shows S9"). The scale is the FT-710's, so a
difference is expected; how big it is, is what I need to know.

That's the end of part 1. If you stop here, please skip to **Sending me the
results**.

## Part 2 - transmit (into a dummy load)

**Connect the dummy load before doing any of this.**

**12. Power setting.**
On the page, set the power to 10 W.
*What you should see:* the radio's power setting changes to 10.

**13. Transmit.**
In FM mode, click the **TX** button on the VFO panel, wait three seconds,
then click it again to stop. (YWC's TX button doesn't send any audio, so
FM gives a plain carrier, which is what the meters need.)
*What you should see:* the radio goes into transmit and back to receive.
Note what the power meter on the page reads compared with the radio.

**14. Other transmit meters.**
While transmitting in step 13, look at ALC, SWR and any other meters on the
page.
*What you should see:* note which ones move and which stay at zero.

**15. Your own list.**
Try anything else you'd normally use (filters, noise reduction, split, CW
keying, memories). A short list of what worked and what didn't is plenty.

## Sending me the results

Please post everything on
https://github.com/mm5agm/Yaesu_Web_Control/discussions/86 rather than
by email, so other 991A owners can see it too.

1. The step numbers that **didn't** do what the plan says, and what
   happened instead. (If a step worked, you don't need to mention it.)
2. The copy of the **Diagnostics** block. On the **About** page, click
   **Copy diagnostics** and paste it into your post.
3. A log from the test:
   - Go to the **Diagnostics** page and click **Start fresh test log**.
   - Repeat whichever steps went wrong.
   - Click **Download test log** and drag the file into your post on #86.
4. A screenshot of the main page while the radio is receiving, if you can.

Thank you. 73, Colin MM5AGM
