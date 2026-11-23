using MediaManager.Models;
using System;
using System.Configuration;
using System.Web;
using System.Web.Configuration;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    public class LoginController : Controller
    {
        // Owner password: LoginPasswordHash in Site.config. Optional guest password: GuestPasswordHash.
        // Make a hash with deploy\New-PasswordHash.ps1.
        private const string NoPasswordMessage =
            "This site has no password set yet. Add LoginPasswordHash to its Site.config (see the README), then restart the site.";

        public ActionResult Index()
        {
            if (SiteSettings.LoginPasswordHash == null)
                ViewBag.Error = NoPasswordMessage;
            return View();
        }

        /// <param name="returnUrl">The page to go back to (from the query string: the login form posts to its own URL).</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Index(string password, string returnUrl)
        {
            var ownerHash = SiteSettings.LoginPasswordHash;
            if (ownerHash == null)
            {
                ViewBag.Error = NoPasswordMessage;
                return View();
            }

            // While this device is paused for too many wrong passwords, don't even check the password.
            string client = Request.UserHostAddress ?? "";
            var wait = LoginThrottle.RemainingPause(client);
            if (wait > TimeSpan.Zero)
            {
                ViewBag.Error = $"Too many wrong passwords. Wait {LoginThrottle.Describe(wait)}, then try again.";
                return View();
            }

            if (string.IsNullOrEmpty(password))
            {
                ViewBag.Error = "Enter the password.";
                return View();
            }

            if (PasswordHasher.Verify(password, ownerHash))
            {
                LogIn(client, Roles.Owner);
                ActivityLog.Write(ActivityLog.Login, "Owner logged in from " + client + ".");
                // Back to where you were when the login expired (only pages on this site)
                return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                    ? (ActionResult)Redirect(returnUrl)
                    : RedirectToAction("Index", "Home");
            }

            var guestHash = SiteSettings.GuestPasswordHash;
            if (guestHash != null && PasswordHasher.Verify(password, guestHash))
            {
                LogIn(client, Roles.Guest);
                ActivityLog.Write(ActivityLog.Login, "Guest logged in from " + client + ".");
                return RedirectToAction("ImageViewAll", "Images");
            }

            var pause = LoginThrottle.RecordFailure(client, out int triesLeft);
            ActivityLog.Error(ActivityLog.Login, ActivityLog.WrongPasswordPrefix + client +(pause > TimeSpan.Zero ? " (paused for " + LoginThrottle.Describe(pause) + ")" : "") + ".");
            if (pause > TimeSpan.Zero)
                ViewBag.Error = $"Wrong password. Too many wrong passwords: wait {LoginThrottle.Describe(pause)}, then try again.";
            else if (triesLeft <= 2)
                ViewBag.Error = $"Invalid password. {triesLeft} more {(triesLeft == 1 ? "try" : "tries")} before a short pause.";
            else
                ViewBag.Error = "Invalid password. Try again.";
            return View();
        }

        // Log out is a POST (the menu's Log out link sends a small hidden form), so another site can't log you out
        // with a link or a picture. An old bookmark to this address just goes to the home page.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Logout()
        {
            if (Session["Auth"] != null)
                ActivityLog.Write(ActivityLog.Login, (Roles.IsGuest(Session) ? "Guest" : "Owner") + " logged out.");
            ActiveLogins.End(Session["LoginId"] as string ?? MediaAccess.Read(HttpContext)?.LoginId);
            EndSession();
            MediaAccess.Clear(HttpContext);
            return RedirectToAction("Index");
        }

        [HttpGet]
        [ActionName("Logout")]
        public ActionResult LogoutGet() => RedirectToAction("Index", "Home");

        private void LogIn(string client, string role)
        {
            LoginThrottle.Reset(client);

            // A new session for the new login: the session the browser had before (which someone else could have
            // planted) is thrown away and its cookie dropped, so the next page starts a fresh one. That page's
            // AuthFilter logs it in from the media pass issued here, the same way it does after a restart.
            EndSession();
            string loginId = ActiveLogins.NewId();
            MediaAccess.Issue(HttpContext, role, SiteSettings.LoginMinutes, loginId: loginId); // Site.config LoginHours (default 12 hours)
            ActiveLogins.Start(loginId, role, client, Request.UserAgent);                      // "Logged in now" list
            HttpContext.Items[ActivityLog.RoleItem] = role;                   // the session can't say it any more
        }

        private void EndSession()
        {
            Session.Clear();
            Session.Abandon();
            // Without this the browser would send the old ID back, and ASP.NET would reuse it for the new session.
            Response.Cookies.Add(new HttpCookie(SessionCookieName, "") { Expires = DateTime.UtcNow.AddYears(-1), HttpOnly = true });
        }

        private static readonly string SessionCookieName =
            (ConfigurationManager.GetSection("system.web/sessionState") as SessionStateSection)?.CookieName ?? "ASP.NET_SessionId";
    }
}
