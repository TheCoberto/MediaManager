using Dapper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaManager.Models
{
    /// <summary>
    /// Who is logged in right now (dbo.ActiveLogins, the Activity page's "Logged in now" list), and logging one
    /// device out.
    ///
    /// Every login gets an id, carried in its media pass (Models\MediaAccess) and its session. Pages and media
    /// requests note when each login was last seen (at most once a minute per login, so videos don't mean a
    /// database write per request). Logging a device out marks its id as revoked: its pass is refused from then
    /// on, and AuthFilter drops its session, even across restarts.
    ///
    /// Nothing here throws: without the table (SQL\Schema.sql not applied yet) logins work as before, the list is
    /// just empty and logging one device out says it can't.
    /// </summary>
    public static class ActiveLogins
    {
        private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

        public sealed class Login
        {
            public string LoginId { get; set; }
            public string Role { get; set; }
            public string Ip { get; set; }
            public string Browser { get; set; }      // the browser's User-Agent
            public DateTime StartedAt { get; set; }  // UTC
            public DateTime LastSeenAt { get; set; } // UTC
        }

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);

        private static readonly ConcurrentDictionary<string, DateTime> lastTouch =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private static readonly object revokedLock = new object();
        private static HashSet<string> revoked; // null = not loaded yet
        private static long lastPruneTicks;

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static bool IsValidId(string id) => id != null && Regex.IsMatch(id, "^[0-9a-f]{32}$");

        /// <summary>A new login (LoginController).</summary>
        public static void Start(string id, string role, string ip, string browser)
        {
            lastTouch[id] = DateTime.UtcNow;
            Run(db => db.Execute(@"
                INSERT INTO ActiveLogins (LoginId, Role, Ip, Browser, StartedAt, LastSeenAt)
                VALUES (@Id, @Role, @Ip, @Browser, SYSUTCDATETIME(), SYSUTCDATETIME())",
                new { Id = id, Role = role, Ip = Cut(ip, 45), Browser = Cut(browser, 400) }));
        }

        /// <summary>
        /// This login is still in use (a page or a media file). Writes at most once a minute per login; adds the
        /// login back if it's missing (e.g. one from before this list existed).
        /// </summary>
        public static void Touch(string id, string role, string ip, string browser, DateTime startedUtc)
        {
            if (!IsValidId(id))
                return;
            var now = DateTime.UtcNow;
            if (lastTouch.TryGetValue(id, out var last) && now - last < TouchEvery)
                return;
            lastTouch[id] = now;

            Run(db =>
            {
                int updated = db.Execute(@"
                    UPDATE ActiveLogins SET LastSeenAt = SYSUTCDATETIME(), Ip = @Ip, Browser = @Browser
                    WHERE LoginId = @Id AND RevokedAt IS NULL",
                    new { Id = id, Ip = Cut(ip, 45), Browser = Cut(browser, 400) });
                if (updated == 0 && !IsRevoked(id))
                    db.Execute(@"
                        IF NOT EXISTS (SELECT 1 FROM ActiveLogins WHERE LoginId = @Id)
                        INSERT INTO ActiveLogins (LoginId, Role, Ip, Browser, StartedAt, LastSeenAt)
                        VALUES (@Id, @Role, @Ip, @Browser, @Started, SYSUTCDATETIME())",
                        new
                        {
                            Id = id, Role = role, Ip = Cut(ip, 45), Browser = Cut(browser, 400),
                            Started = startedUtc > new DateTime(2000, 1, 1) ? startedUtc : now
                        });
                PruneNowAndThen(db);
            });
        }

        /// <summary>Logged out on this device: forget the login.</summary>
        public static void End(string id)
        {
            if (!IsValidId(id))
                return;
            lastTouch.TryRemove(id, out _);
            Run(db => db.Execute("DELETE FROM ActiveLogins WHERE LoginId = @Id AND RevokedAt IS NULL", new { Id = id }));
        }

        /// <summary>Logs one device out (Activity page). Returns the login it ended, or null if it couldn't.</summary>
        public static Login Revoke(string id)
        {
            if (!IsValidId(id))
                return null;
            var login = Query(db => db.QueryFirstOrDefault<Login>(
                "SELECT * FROM ActiveLogins WHERE LoginId = @Id AND RevokedAt IS NULL", new { Id = id }), null);
            if (login == null)
                return null;

            bool saved = Query(db => db.Execute("UPDATE ActiveLogins SET RevokedAt = SYSUTCDATETIME() WHERE LoginId = @Id",
                new { Id = id }) > 0, false);
            if (!saved)
                return null;
            lock (revokedLock)
                Revoked().Add(id);
            lastTouch.TryRemove(id, out _);
            return login;
        }

        /// <summary>After "Log out all other devices": every other login is over (MediaAccess refuses them by time).</summary>
        public static void ForgetAllExcept(string keepId)
        {
            Run(db => db.Execute("DELETE FROM ActiveLogins WHERE RevokedAt IS NULL AND LoginId <> @Keep", new { Keep = keepId ?? "" }));
            foreach (var id in lastTouch.Keys.Where(k => k != keepId).ToList())
                lastTouch.TryRemove(id, out _);
        }

        /// <summary>Whether this login was logged out from the Activity page.</summary>
        public static bool IsRevoked(string id)
        {
            if (id == null)
                return false;
            lock (revokedLock)
                return Revoked().Contains(id);
        }

        /// <summary>Logins seen within the login time (Site.config LoginHours), most recently seen first.</summary>
        public static List<Login> Current()
        {
            return Query(db => db.Query<Login>(@"
                SELECT LoginId, Role, Ip, Browser, StartedAt, LastSeenAt FROM ActiveLogins
                WHERE RevokedAt IS NULL AND LastSeenAt >= DATEADD(MINUTE, -@Minutes, SYSUTCDATETIME())
                ORDER BY LastSeenAt DESC",
                new { Minutes = SiteSettings.LoginMinutes }).AsList(), new List<Login>());
        }

        /// <summary>"Chrome on Windows", "Safari on iPhone"... from a User-Agent.</summary>
        public static string DescribeBrowser(string ua)
        {
            if (string.IsNullOrEmpty(ua))
                return "Unknown browser";
            string browser =
                ua.Contains("Edg/") ? "Edge" :
                ua.Contains("OPR/") || ua.Contains("Opera") ? "Opera" :
                ua.Contains("SamsungBrowser") ? "Samsung Internet" :
                ua.Contains("Firefox/") || ua.Contains("FxiOS") ? "Firefox" :
                ua.Contains("Chrome/") || ua.Contains("CriOS") ? "Chrome" :
                ua.Contains("Safari/") ? "Safari" : "Browser";
            string os =
                ua.Contains("iPhone") ? "iPhone" :
                ua.Contains("iPad") ? "iPad" :
                ua.Contains("Android") ? "Android" :
                ua.Contains("Windows") ? "Windows" :
                ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "Mac" :
                ua.Contains("CrOS") ? "Chromebook" :
                ua.Contains("Linux") ? "Linux" : null;
            return os == null ? browser : browser + " on " + os;
        }

        // Revoked ids, loaded once (and kept up to date by Revoke). A revoked pass can't be renewed, so it's dead
        // within the login time; the rows are removed a day after that (PruneNowAndThen).
        private static HashSet<string> Revoked()
        {
            if (revoked == null)
            {
                revoked = new HashSet<string>(
                    Query(db => db.Query<string>("SELECT LoginId FROM ActiveLogins WHERE RevokedAt IS NOT NULL").AsList(), new List<string>()),
                    StringComparer.Ordinal);
            }
            return revoked;
        }

        private static void PruneNowAndThen(IDbConnection db)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastPruneTicks);
            if (now - last < TimeSpan.TicksPerHour || Interlocked.CompareExchange(ref lastPruneTicks, now, last) != last)
                return;
            db.Execute(@"
                DELETE FROM ActiveLogins
                WHERE (RevokedAt IS NULL AND LastSeenAt < DATEADD(MINUTE, -@Minutes, SYSUTCDATETIME()))
                   OR RevokedAt < DATEADD(MINUTE, -@Minutes - 1440, SYSUTCDATETIME())",
                new { Minutes = SiteSettings.LoginMinutes });
        }

        private static string Cut(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max);

        private static void Run(Action<IDbConnection> action) => Query(db => { action(db); return 0; }, 0);

        private static T Query<T>(Func<IDbConnection, T> query, T fallback)
        {
            try
            {
                using (var db = Db())
                    return query(db);
            }
            catch (Exception ex) when (ex is SqlException || ex is InvalidOperationException || ex is TimeoutException)
            {
                // Most likely the table isn't there yet (SQL\Schema.sql not applied)
                Debug.WriteLine("ActiveLogins: " + ex.Message);
                return fallback;
            }
        }
    }
}
