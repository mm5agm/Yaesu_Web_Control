namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Programs connected to YWC's CAT ports: WSJT-X and other Hamlib
    /// programs on the rigctld port, N1MM on its own port. While any is
    /// connected, closing the last browser tab doesn't close YWC, because
    /// those programs would lose the radio (W2JAZ, 2026-11-27).
    /// </summary>
    public sealed class CatClientPresence
    {
        private int _rigctld;
        private int _n1mm;

        public void RigctldConnected() => Interlocked.Increment(ref _rigctld);
        public void RigctldDisconnected() => DecrementToZero(ref _rigctld);
        public void N1mmConnected() => Interlocked.Increment(ref _n1mm);
        public void N1mmDisconnected() => DecrementToZero(ref _n1mm);

        public int Rigctld => Volatile.Read(ref _rigctld);
        public int N1mm => Volatile.Read(ref _n1mm);
        public bool Any => Rigctld > 0 || N1mm > 0;

        /// <summary>For the log: "1 rigctld program (WSJT-X or a logger), 1 N1MM connection".</summary>
        public string Describe()
        {
            var parts = new List<string>();
            int r = Rigctld, n = N1mm;
            if (r > 0) parts.Add(r == 1 ? "1 rigctld program (WSJT-X or a logger)" : $"{r} rigctld programs (WSJT-X or loggers)");
            if (n > 0) parts.Add(n == 1 ? "1 N1MM connection" : $"{n} N1MM connections");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static void DecrementToZero(ref int count)
        {
            while (true)
            {
                int now = Volatile.Read(ref count);
                if (now <= 0) return;
                if (Interlocked.CompareExchange(ref count, now - 1, now) == now) return;
            }
        }
    }
}
