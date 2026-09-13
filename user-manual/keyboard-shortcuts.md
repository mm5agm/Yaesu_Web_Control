## 13. Keyboard Shortcuts

| Key / Action | Result |
|---|---|
| **F** | Enter full-screen mode |
| **Esc** | Exit full-screen mode |
| Mouse wheel (on spectrum) | Tune VFO A up or down in 1 kHz steps |
| Click on spectrum | Tune VFO A to the clicked frequency |
| **Tab** (in band buttons) | Move focus into the band button group |
| **← / →** (in band buttons) | Move to the previous/next band and switch immediately |
| Numeric entry button (**⑁**) next to MHz | Open the on-screen frequency keyboard for that VFO |
| **0–9** (frequency keyboard open) | Type the digit at the cursor position |
| **← →** (frequency keyboard open) | Move the cursor left or right |
| **Backspace** (frequency keyboard open) | Clear the current digit and move cursor back |
| **Delete** (frequency keyboard open) | Clear all digits |
| **↵ Enter** (frequency keyboard open) | Send the entered frequency to the radio |
| **Esc** (frequency keyboard open) | Close the keyboard without changing frequency |
| **Esc** (Memory panel open) | Close the Memory panel |

**Frequency display — changing the value digit by digit.** Every VFO frequency display is a "digit-pickable" control. You select a digit (it highlights yellow), then step it up or down. Three input methods reach the same set of actions, so pick whichever suits you:

| Input | Action | What happens |
|---|---|---|
| **Click** a digit | Select | That digit highlights yellow. The next step / arrow / button action acts on it. |
| **Mouse wheel** over a digit | Select + step | Wheels up = +1, wheels down = −1 on the digit under the cursor. |
| **Tab** into the freq display | Focus the display | A blue outline appears around the whole display. Now the keyboard keys below act on it. |
| **ArrowUp** / **ArrowDown** | Step selected digit by ±1 | If no digit is currently highlighted, the first press just highlights the kHz digit (4th from the right) — a second press then steps it. This avoids accidentally changing a digit you can't see is selected. |
| **PageUp** / **PageDown** | Step selected digit by ±10 | Carries propagate up — "9 + 1" rolls over into the next digit left. |
| **ArrowLeft** / **ArrowRight** | Move the selection cursor | Highlights the digit to the left / right. Does not change the frequency. |
| **Home** | Jump to the most-significant digit | Selection moves to the **leftmost** digit (tens of MHz). |
| **End** | Jump to the least-significant digit | Selection moves to the **rightmost** digit (Hz). |
| **▲ / ▼** buttons | Step the selected digit by ±1 | Only visible if Settings → Accessibility → **Show frequency up/down arrow buttons** is on. A click does the same as one ArrowUp / ArrowDown. If no digit is selected, the first click auto-selects the kHz digit and steps it in one go (buttons are a deliberate action — unlike the keyboard, they don't need a "show me the cursor" first press). **Press and hold to repeat** the same step every 500 ms until released — mouse, touch, and keyboard (Enter/Space) all work. |
| Click anywhere outside the display + arrow buttons | Deselect | The selection is cleared; the next ArrowUp will start over with the "first press picks the kHz digit" behaviour. |

A few extra notes:

- **Click-tuned changes are debounced** — when you stop wheeling / pressing for ~600 ms, the new frequency is sent to the radio. Holding ArrowUp for a sustained step (autorepeat) works fine; it sends one CAT command per ~600 ms of stillness rather than one per keystroke.
- **Selection persists** across polling cycles — you can press ArrowUp repeatedly and the selection stays on the same digit. The radio's confirmation of one step doesn't blow your selection away.
- **The selected digit highlights yellow** when an actual digit is selected. The whole display also gains a blue focus ring when it has keyboard focus (e.g. you tabbed into it).

**Browser zoom — make everything bigger or smaller.** YWC is a web page, so it honours your browser's standard zoom keyboard shortcuts. This is the easiest way to make controls more readable on a high-resolution monitor or to fit more on a small tablet screen:

| Key | Result |
|---|---|
| **Ctrl + +** (Ctrl and plus / equals) | Zoom in — make the whole page larger |
| **Ctrl + −** (Ctrl and minus) | Zoom out — make the whole page smaller |
| **Ctrl + 0** (Ctrl and zero) | Reset to 100% — back to the default size |
| **Ctrl + mouse wheel** | Smooth zoom in or out (over the page anywhere except the spectrum, which uses the wheel for tuning) |

The browser remembers your zoom level per site, so once you've set it, every YWC session opens at that size until you change it. Worth setting once if the default text is too small (or too large) for you — and especially worth knowing about for partially-sighted operators who don't otherwise know browsers can do this.

---
