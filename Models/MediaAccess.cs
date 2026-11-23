using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Web.Security;

namespace MediaManager.Models
{
    /// <summary>
    /// The "media pass": an encrypted, tamper-proof cookie that lets the browser load files under /media.
    ///
    /// IIS serves /media files itself (fast, with seeking in videos), and the login session isn't available
    /// for those requests, so Filters\MediaAuthModule checks this cookie instead. It's issued at login,
    /// refreshed by page and media requests, and removed at logout. It runs out after the login's idle time
    /// (Site.config LoginHours, 12 hours by default), and goes when the browser closes. It also says whether the
    /// holder is the owner or a guest, and Filters\AuthFilter uses it to log the browser back in when the site's
    /// memory (where the login session lives) has been wiped by a deploy or recycle.
    /// </summary>
    public static class MediaAccess
    {
        private const string Purpose = "MediaManager.MediaAccess.v1";

        /// <summary>Who holds a valid pass, and until when.</summary>
        public sealed class Pass
        {
            public string Role { get; set; }
            public DateTime ExpiresUtc { get; set; }
            public int TimeoutMinutes { get; set; }
            /// <summary>When this login started (kept as the pass is renewed). MinValue for passes made before this was recorded.</summary>
            public DateTime LoggedInUtc { get; set; }
            /// <summary>This login's id (Models\ActiveLogins), or null for passes made before logins had ids.</summary>
            public string LoginId { get; set; }
        }

        // ---- Sign out everywhere ----
        // The owner can end every login on every device (Activity page). Logins that started before that moment
        // stop working: their passes are refused here, and AuthFilter drops their sessions. The moment is kept in
        // a hidden file in the media folder (like the hidden-names key), so a restart or deploy doesn't undo it.

        public const string SignedOutFileName = ".signed-out-at";

        private static readonly object signOutLock = new object();
        private static DateTime? signedOutBeforeUtc; // null = not read from the file yet

        /// <summary>Logins that started before this are no longer valid (MinValue = never signed out everywhere).</summary>
        public static DateTime SignedOutBeforeUtc
        {
            get
            {
                lock (signOutLock)
                {
                    if (signedOutBeforeUtc == null)
                    {
                        DateTime value = DateTime.MinValue;
                        try
                        {
                            string path = SignedOutPath;
                            if (path != null && File.Exists(path)
                                && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                                && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                                value = new DateTime(ticks, DateTimeKind.Utc);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            System.Diagnostics.Trace.TraceWarning("Couldn't read " + SignedOutFileName + ": " + ex.Message);
                        }
                        signedOutBeforeUtc = value;
                    }
                    return signedOutBeforeUtc.Value;
                }
            }
        }

        /// <summary>
        /// Ends every login that started before now, on every device, then gives this browser a new login so the
        /// owner who pressed the button stays logged in. Returns false (and changes nothing) if the time couldn't
        /// be saved.
        /// </summary>
        public static bool SignOutEverywhere(HttpContextBase context, string keepRole, int timeoutMinutes, out string error)
        {
            error = null;
            DateTime now = DateTime.UtcNow;
            lock (signOutLock)
            {
                try
                {
                    string path = SignedOutPath ?? throw new IOException("The media folder isn't set up.");
                    if (File.Exists(path))
                        File.SetAttributes(path, FileAttributes.Normal);
                    File.WriteAllText(path, now.Ticks.ToString(CultureInfo.InvariantCulture));
                    File.SetAttributes(path, FileAttributes.Hidden);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                    return false;
                }
                signedOutBeforeUtc = now;
            }

            context.Session["AuthAt"] = now.Ticks;
            string keepId = context.Session["LoginId"] as string;
            Issue(context, keepRole, timeoutMinutes, now, keepId);
            ActiveLogins.ForgetAllExcept(keepId);
            return true;
        }

        /// <summary>Whether a session that logged in at these ticks (Session["AuthAt"]) is still allowed.</summary>
        public static bool IsStillSignedIn(object authAtTicks)
        {
            DateTime before = SignedOutBeforeUtc;
            return before == DateTime.MinValue || (authAtTicks is long ticks && ticks >= before.Ticks);
        }

        private static string SignedOutPath
        {
            get
            {
                try
                {
                    return Path.Combine(MediaPaths.Root, SignedOutFileName);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
                {
                    return null; // no /media virtual directory
                }
            }
        }

        // Different for each IIS site, so a pass from one site is useless on another (even on the same machine
        // and host name), and the two sites' cookies don't overwrite each other when only the port differs.
        private static string SiteKey => HostingEnvironment.ApplicationID ?? "";

        private static readonly Lazy<string> cookieName = new Lazy<string>(() => "MediaAccess_" + ShortHash(SiteKey));

        public static string CookieName => cookieName.Value;

        /// <summary>Gives the browser a new pass for this role.</summary>
        /// <param name="loggedInUtc">When the login started: now for a new login, or the old pass's time when renewing.</param>
        /// <param name="loginId">The login's id (Models\ActiveLogins); a new one if null.</param>
        public static void Issue(HttpContextBase context, string role, int timeoutMinutes, DateTime? loggedInUtc = null, string loginId = null)
        {
            if (timeoutMinutes < 1)
                timeoutMinutes = 20;

            string payload = string.Join("|", role,
                DateTime.UtcNow.AddMinutes(timeoutMinutes).Ticks.ToString(CultureInfo.InvariantCulture),
                timeoutMinutes.ToString(CultureInfo.InvariantCulture),
                (loggedInUtc ?? DateTime.UtcNow).Ticks.ToString(CultureInfo.InvariantCulture),
                ActiveLogins.IsValidId(loginId) ? loginId : ActiveLogins.NewId());

            byte[] sealedBytes = MachineKey.Protect(Encoding.UTF8.GetBytes(payload), Purpose, SiteKey);
            context.Response.Cookies.Set(NewCookie(context, HttpServerUtility.UrlTokenEncode(sealedBytes)));
        }

        /// <summary>The browser's valid pass, or null if it has none, it's been tampered with, or it has run out.</summary>
        public static Pass Read(HttpContextBase context)
        {
            var cookie = context.Request.Cookies[CookieName];
            if (cookie == null || string.IsNullOrEmpty(cookie.Value))
                return null;

            string payload;
            try
            {
                var bytes = HttpServerUtility.UrlTokenDecode(cookie.Value);
                if (bytes == null)
                    return null;
                payload = Encoding.UTF8.GetString(MachineKey.Unprotect(bytes, Purpose, SiteKey));
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                return null;
            }

            // role | expires | timeout [| logged in [| login id]]. Passes from before "logged in" was added have 3 parts
            // and count as logged in long ago: fine until someone signs out everywhere. Passes without an id get one
            // the next time they're renewed (AuthFilter).
            var parts = payload.Split('|');
            long loggedIn = 0;
            string loginId = parts.Length == 5 ? parts[4] : null;
            if ((parts.Length < 3 || parts.Length > 5)
                || (loginId != null && !ActiveLogins.IsValidId(loginId))
                || (parts[0] != Roles.Owner && parts[0] != Roles.Guest)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int timeout)
                || (parts.Length >= 4 && !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out loggedIn)))
                return null;

            if (ticks <= DateTime.UtcNow.Ticks || ticks > DateTime.MaxValue.Ticks || loggedIn > DateTime.MaxValue.Ticks)
                return null;

            // Signed out everywhere after this login started
            if (loggedIn < SignedOutBeforeUtc.Ticks)
                return null;

            // This device was logged out from the Activity page
            if (ActiveLogins.IsRevoked(loginId))
                return null;

            return new Pass
            {
                Role = parts[0],
                ExpiresUtc = new DateTime(ticks, DateTimeKind.Utc),
                TimeoutMinutes = timeout,
                LoggedInUtc = new DateTime(loggedIn, DateTimeKind.Utc),
                LoginId = loginId
            };
        }

        /// <summary>
        /// Makes sure a logged-in browser has a fresh pass for its role: issues one if it's missing, for another
        /// role, or more than half used up. (Renewing only then avoids a new cookie on every request.)
        /// </summary>
        public static void Refresh(HttpContextBase context, string role, int timeoutMinutes)
        {
            var pass = Read(context);
            string sessionId = context.Session?["LoginId"] as string;
            if (pass == null || pass.Role != role || IsOld(pass) || (sessionId != null && pass.LoginId != sessionId))
                Issue(context, role, timeoutMinutes, SessionLoggedIn(context) ?? pass?.LoggedInUtc, sessionId ?? pass?.LoginId);
        }

        /// <summary>Extends a valid pass that's more than half used up (e.g. while a long video keeps loading).</summary>
        public static void RenewIfOld(HttpContextBase context, Pass pass)
        {
            if (IsOld(pass))
                Issue(context, pass.Role, pass.TimeoutMinutes, pass.LoggedInUtc, pass.LoginId);
        }

        // When this browser's session logged in (Session["AuthAt"], set by AuthFilter), if it has one
        private static DateTime? SessionLoggedIn(HttpContextBase context)
        {
            return context.Session?["AuthAt"] is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;
        }

        /// <summary>Logout: removes the pass.</summary>
        public static void Clear(HttpContextBase context)
        {
            var cookie = NewCookie(context, "");
            cookie.Expires = DateTime.UtcNow.AddYears(-1);
            context.Response.Cookies.Set(cookie);
        }

        private static bool IsOld(Pass pass)
        {
            return (pass.ExpiresUtc - DateTime.UtcNow).TotalMinutes < pass.TimeoutMinutes / 2.0;
        }

        private static HttpCookie NewCookie(HttpContextBase context, string value)
        {
            // No Expires: the browser forgets it when it closes. HttpOnly: page scripts can't read it.
            return new HttpCookie(CookieName, value)
            {
                HttpOnly = true,
                Secure = context.Request.IsSecureConnection,
                SameSite = SameSiteMode.Lax,
                Path = context.Request.ApplicationPath ?? "/"
            };
        }

        private static string ShortHash(string s)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
