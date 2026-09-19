// host-presence.js — tell the host a browser is here.
//
// The host exits ~30 seconds after its last SignalR connection drops, so that
// closing the last tab closes the app (AutoShutdownWhenNoBrowsers, default on;
// forced off in containers). Pages that use _Layout get that connection for
// free: site.js opens one and keeps it.
//
// Pages that set `Layout = null` do not. Remote Audio was one, and a listener
// sitting on it with no other tab open had the host exit underneath them
// mid-transmission. Any such page must either open its own /radioHub
// connection — Radio Display does, for scope and VFO state — or call this,
// which opens one for no reason but to be counted.
//
// Requires signalr.min.js to have been loaded already:
//
//   <script src="~/lib/microsoft-signalr/signalr.min.js"></script>
//   <script type="module">
//       import { keepHostAlive } from "/js/ui/host-presence.js";
//       keepHostAlive();
//   </script>
//
// YWC-local on purpose: its entire content is this app's hub URL and its
// shutdown contract, so it is not a candidate for core/.

let connection = null;

/**
 * Open (once) a connection to /radioHub purely so the host counts this tab as
 * a browser being present. Safe to call more than once; later calls are no-ops.
 *
 * @returns {object|null} the underlying HubConnection, or null if the SignalR
 *          client was not loaded — a page with no live state is still usable
 *          without it, so this warns rather than throws.
 */
export function keepHostAlive() {
    if (connection) return connection;

    if (typeof signalR === "undefined") {
        console.warn(
            "host-presence: signalr.min.js is not loaded, so this page will " +
            "not hold the host open. Add the script tag before this module.");
        return null;
    }

    connection = new signalR.HubConnectionBuilder()
        .withUrl("/radioHub")
        .withAutomaticReconnect()
        .build();

    // The hub replays a state snapshot to every client on connect. Nothing
    // here wants it, but ignoring it costs nothing and registering no handler
    // at all would log an unhandled-message warning on every update.
    connection.on("RadioStateUpdate", () => { });

    connection.start().catch(err => {
        // Not fatal: the page's own job carries on. Worth a line, because the
        // symptom if it stays broken is the app quitting while in use.
        console.warn("host-presence: could not connect to /radioHub", err);
    });

    return connection;
}
