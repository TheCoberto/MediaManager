using System;
using System.Collections.Concurrent;

namespace MediaManager.Models
{
    /// <summary>
    /// Slows down password guessing. Each device (IP address) gets a few free tries; after that every
    /// wrong password means a pause before the next try is even checked: 30 s, then 1, 2, 4, 8 minutes,
    /// up to 15 minutes. A correct password, or an hour without wrong passwords, starts it over.
    ///
    /// Kept in memory, so restarting the site (publish, app pool recycle) also clears it.
    /// </summary>
    public static class LoginThrottle
    {
        public const int FreeTries = 5;

        private static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

        private sealed class Entry
        {
            public int Failures;
            public DateTime LastFailureUtc;
            public DateTime PausedUntilUtc;
        }

        private static readonly ConcurrentDictionary<string, Entry> entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long this device still has to wait before its next try is checked (zero = it can try now).</summary>
        public static TimeSpan RemainingPause(string client)
        {
            if (entries.TryGetValue(client ?? "", out var e))
            {
                lock (e)
                {
                    var left = e.PausedUntilUtc - DateTime.UtcNow;
                    if (left > TimeSpan.Zero)
                        return left;
                }
            }
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Counts a wrong password. Returns the pause that now applies (zero if none yet) and, while there's
        /// no pause, how many tries are left before the first one.
        /// </summary>
        public static TimeSpan RecordFailure(string client, out int triesLeft)
        {
            var now = DateTime.UtcNow;
            Prune(now);

            var e = entries.GetOrAdd(client ?? "", _ => new Entry());
            lock (e)
            {
                if (now - e.LastFailureUtc > ForgetAfter)
                    e.Failures = 0;

                e.Failures++;
                e.LastFailureUtc = now;

                if (e.Failures < FreeTries)
                {
                    triesLeft = FreeTries - e.Failures;
                    return TimeSpan.Zero;
                }

                // 5th wrong password: 30 s; each one after that doubles it, up to the longest pause.
                int doublings = Math.Min(e.Failures - FreeTries, 10);
                var pause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, FirstPause.Ticks << doublings));
                e.PausedUntilUtc = now + pause;
                triesLeft = 0;
                return pause;
            }
        }

        /// <summary>Correct password: forget this device's wrong ones.</summary>
        public static void Reset(string client)
        {
            entries.TryRemove(client ?? "", out _);
        }

        /// <summary>"30 seconds", "2 minutes", ...</summary>
        public static string Describe(TimeSpan t)
        {
            if (t.TotalSeconds < 60)
            {
                int s = Math.Max(1, (int)Math.Ceiling(t.TotalSeconds));
                return s == 1 ? "1 second" : s + " seconds";
            }
            int m = (int)Math.Ceiling(t.TotalMinutes);
            return m == 1 ? "1 minute" : m + " minutes";
        }

        // Keeps memory bounded if lots of different addresses try.
        private static void Prune(DateTime now)
        {
            if (entries.Count < 1000)
                return;

            foreach (var kv in entries)
            {
                if (now - kv.Value.LastFailureUtc > ForgetAfter && kv.Value.PausedUntilUtc < now)
                    entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
