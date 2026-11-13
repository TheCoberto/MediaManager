using System;
using System.Globalization;
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
    /// refreshed by page and media requests, and removed at logout. Like the session, it runs out after the
    /// session's idle time (20 minutes by default). It also says whether the holder is the owner or a guest.
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
        }

        // Different for each IIS site, so a pass from one site is useless on another (even on the same machine
        // and host name), and the two sites' cookies don't overwrite each other when only the port differs.
        private static string SiteKey => HostingEnvironment.ApplicationID ?? "";

        private static readonly Lazy<string> cookieName = new Lazy<string>(() => "MediaAccess_" + ShortHash(SiteKey));

        public static string CookieName => cookieName.Value;

        /// <summary>Gives the browser a new pass for this role.</summary>
        public static void Issue(HttpContextBase context, string role, int timeoutMinutes)
        {
            if (timeoutMinutes < 1)
                timeoutMinutes = 20;

            string payload = string.Join("|", role,
                DateTime.UtcNow.AddMinutes(timeoutMinutes).Ticks.ToString(CultureInfo.InvariantCulture),
                timeoutMinutes.ToString(CultureInfo.InvariantCulture));

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

            var parts = payload.Split('|');
            if (parts.Length != 3
                || (parts[0] != Roles.Owner && parts[0] != Roles.Guest)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int timeout))
                return null;

            if (ticks <= DateTime.UtcNow.Ticks || ticks > DateTime.MaxValue.Ticks)
                return null;

            return new Pass { Role = parts[0], ExpiresUtc = new DateTime(ticks, DateTimeKind.Utc), TimeoutMinutes = timeout };
        }

        /// <summary>
        /// Makes sure a logged-in browser has a fresh pass for its role: issues one if it's missing, for another
        /// role, or more than half used up. (Renewing only then avoids a new cookie on every request.)
        /// </summary>
        public static void Refresh(HttpContextBase context, string role, int timeoutMinutes)
        {
            var pass = Read(context);
            if (pass == null || pass.Role != role || IsOld(pass))
                Issue(context, role, timeoutMinutes);
        }

        /// <summary>Extends a valid pass that's more than half used up (e.g. while a long video keeps loading).</summary>
        public static void RenewIfOld(HttpContextBase context, Pass pass)
        {
            if (IsOld(pass))
                Issue(context, pass.Role, pass.TimeoutMinutes);
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
