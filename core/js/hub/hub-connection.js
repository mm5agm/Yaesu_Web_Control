// Radio Web Control - hub connection
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Builds every /radioHub connection the same way. Loaded as a plain script
// straight after signalr.min.js, so it is available to classic scripts
// (site.js) and to modules alike - modules are deferred, so they run after
// this. Served at /js/hub/hub-connection.js.
//
// The server half lives in each app's Program.cs: ClientTimeoutInterval must
// be longer than SERVER_TIMEOUT_MS below, and KeepAliveInterval well under it.
//
// Why this exists, measured in Yaesu Web Control on 2026-09-19:
//
//   12:49:16  Shutdown countdown cancelled (browser connected)   <- an About page
//             ... 24 minutes, tab open and visible on screen ...
//   13:13:40  Last live browser connection dropped
//   13:14:10  No clients reconnected -- stopping application
//
// The About page was never touched and never closed. Its connection went
// quiet, the server stopped counting it, and 30 seconds later the host exited
// underneath a page that still looked fine. Clicking a link then gave
// ERR_CONNECTION_REFUSED.
//
// Two separate defaults conspired:
//
//   * The server dropped a client that had sent nothing for 30 seconds
//     (HubOptions.ClientTimeoutInterval). Everything that keeps a browser
//     "alive" -- SignalR's own ping and our 5-second Heartbeat -- is a JS
//     timer, and a browser is free to throttle or freeze timers in a tab it
//     considers background. The apps' Program.cs allows two minutes, which is
//     longer than any throttling a browser applies.
//
//   * withAutomaticReconnect() with no argument retries at 0s, 2s, 10s and
//     30s and then gives up for good. So the drop was permanent: even had the
//     host still been running, that page would never have come back.
//
// Both ends are fixed here and in Program.cs. Closing a tab still shuts the
// host down promptly -- that path is a clean socket close, which the server
// sees at once, and is not affected by the timeout above.

(function () {
    "use strict";

    // Retry forever. The point is not speed of recovery but that there is no
    // attempt count after which the page is dead; a laptop shut for an hour
    // should still find its way back.
    var RETRY_DELAYS_MS = [0, 2000, 5000, 10000];
    var RETRY_CAP_MS = 15000;

    // Must stay below the server's ClientTimeoutInterval (2 minutes) or the
    // client will declare the server gone while the server is happy.
    var SERVER_TIMEOUT_MS = 100000;
    var KEEPALIVE_MS = 15000;

    var retryPolicy = {
        nextRetryDelayInMilliseconds: function (ctx) {
            var i = ctx.previousRetryCount;
            return i < RETRY_DELAYS_MS.length ? RETRY_DELAYS_MS[i] : RETRY_CAP_MS;
        }
    };

    /**
     * Build a hub connection that will not quietly give up.
     *
     * @param {string} url hub path, e.g. "/radioHub"
     * @returns {object} an unstarted HubConnection
     */
    window.rwcHubConnection = function (url) {
        var conn = new signalR.HubConnectionBuilder()
            .withUrl(url)
            .withAutomaticReconnect(retryPolicy)
            .build();

        conn.serverTimeoutInMilliseconds = SERVER_TIMEOUT_MS;
        conn.keepAliveIntervalInMilliseconds = KEEPALIVE_MS;

        // A frozen tab cannot run the reconnect timer either, so coming back
        // to the tab is itself a reason to try. Cheap, and it is the moment
        // the user is most likely to be about to click something.
        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState !== "visible") return;
            if (conn.state !== signalR.HubConnectionState.Disconnected) return;
            conn.start().catch(function () { /* the retry policy has it */ });
        });

        return conn;
    };
})();
