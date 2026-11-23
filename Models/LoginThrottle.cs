using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace MediaManager.Models
{
    /// <summary>
    /// Slows down password guessing. Each device (IP address) gets a few free tries; after that every
    /// wrong password means a pause before the next try is even checked: 30 s, then 1, 2, 4, 8 minutes,
    /// up to 15 minutes. A correct password, or an hour without wrong passwords, starts it over.
    ///
    /// Across the whole site too: once there have been SiteWideLimit wrong passwords in the last hour, from any
    /// mix of devices (someone spreading guesses over many addresses), every device gets just one free try
    /// before its pauses start. The right password still works as soon as a device's pause is over, so this
    /// slows guessing down without locking the owner out.
    ///
    /// Kept in memory, and rebuilt from the activity log's wrong-password entries (with their IP addresses)
    /// after a restart, so a publish or app pool recycle doesn't hand out fresh tries.
    /// </summary>
    public static class LoginThrottle
    {
        public const int FreeTries = 5;
        public const int SiteWideLimit = 30;
        private const int SiteWideFreeTries = 1;

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

        // Every wrong password in the last hour, from any device, oldest first (for the site-wide limit)
        private static readonly Queue<DateTime> recent = new Queue<DateTime>();
        private static DateTime siteWideLoggedUtc;   // when the site-wide limit was last written to the activity log

        private static readonly object seedLock = new object();
        private static bool seeded;

        /// <summary>How long this device still has to wait before its next try is checked (zero = it can try now).</summary>
        public static TimeSpan RemainingPause(string client)
        {
            EnsureSeeded();
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

        /// <summary>Whether there have been SiteWideLimit or more wrong passwords in the last hour, across all devices.</summary>
        public static bool SiteWideLimitReached
        {
            get
            {
                EnsureSeeded();
                lock (recent)
                {
                    DropOld(DateTime.UtcNow);
                    return recent.Count >= SiteWideLimit;
                }
            }
        }

        /// <summary>
        /// Counts a wrong password. Returns the pause that now applies (zero if none yet) and, while there's
        /// no pause, how many tries are left before the first one.
        /// </summary>
        public static TimeSpan RecordFailure(string client, out int triesLeft)
        {
            EnsureSeeded();
            var now = DateTime.UtcNow;
            Prune(now);

            bool siteWide;
            bool logSiteWide = false;
            lock (recent)
            {
                DropOld(now);
                recent.Enqueue(now);
                siteWide = recent.Count >= SiteWideLimit;
                if (siteWide && now - siteWideLoggedUtc > ForgetAfter)
                {
                    siteWideLoggedUtc = now;
                    logSiteWide = true;
                }
            }
            if (logSiteWide)
                ActivityLog.Error(ActivityLog.Login, $"{SiteWideLimit} or more wrong passwords in the last hour, from any device: "
                    + "every device now gets one try before it has to wait. (Logged at most once an hour.)");

            var e = entries.GetOrAdd(client ?? "", _ => new Entry());
            lock (e)
            {
                if (now - e.LastFailureUtc > ForgetAfter)
                    e.Failures = 0;

                e.Failures++;
                e.LastFailureUtc = now;
                return PauseFor(e, siteWide ? SiteWideFreeTries : FreeTries, now, out triesLeft);
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

        // The free-th wrong password: 30 s; each one after that doubles it, up to the longest pause.
        private static TimeSpan PauseFor(Entry e, int free, DateTime lastUtc, out int triesLeft)
        {
            if (e.Failures < free)
            {
                triesLeft = free - e.Failures;
                return TimeSpan.Zero;
            }

            int doublings = Math.Min(e.Failures - free, 10);
            var pause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, FirstPause.Ticks << doublings));
            e.PausedUntilUtc = lastUtc + pause;
            triesLeft = 0;
            return pause;
        }

        /// <summary>
        /// After a restart: picks up the last hour's wrong passwords from the activity log, so devices that were
        /// paused stay paused and the site-wide count carries on. Tried once; a log that can't be read just means
        /// starting from nothing, as before.
        /// </summary>
        private static void EnsureSeeded()
        {
            if (seeded)
                return;
            lock (seedLock)
            {
                if (seeded)
                    return;
                seeded = true;
                try
                {
                    var now = DateTime.UtcNow;
                    var rows = ActivityLog.WrongPasswordsSince(now - ForgetAfter);
                    int total = rows.Sum(r => r.Count);
                    lock (recent)
                    {
                        // Only the last time per device is known; that's close enough for an hour-long window
                        foreach (var r in rows.OrderBy(r => r.LastUtc))
                            for (int i = 0; i < r.Count; i++)
                                recent.Enqueue(r.LastUtc);
                    }
                    int free = total >= SiteWideLimit ? SiteWideFreeTries : FreeTries;
                    foreach (var r in rows)
                    {
                        var e = new Entry { Failures = r.Count, LastFailureUtc = r.LastUtc };
                        PauseFor(e, free, r.LastUtc, out _);
                        entries.TryAdd(r.Ip, e);
                    }
                }
                catch (Exception ex) when (ex is SqlException || ex is InvalidOperationException || ex is TimeoutException)
                {
                    System.Diagnostics.Debug.WriteLine("LoginThrottle: couldn't read the activity log: " + ex.Message);
                }
            }
        }

        private static void DropOld(DateTime now)
        {
            while (recent.Count > 0 && now - recent.Peek() > ForgetAfter)
                recent.Dequeue();
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
