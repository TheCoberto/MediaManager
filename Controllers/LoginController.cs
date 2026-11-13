using MediaManager.Models;
using System;
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
                // Back to where you were when the login expired (only pages on this site)
                return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                    ? (ActionResult)Redirect(returnUrl)
                    : RedirectToAction("Index", "Home");
            }

            var guestHash = SiteSettings.GuestPasswordHash;
            if (guestHash != null && PasswordHasher.Verify(password, guestHash))
            {
                LogIn(client, Roles.Guest);
                return RedirectToAction("ImageViewAll", "Images");
            }

            var pause = LoginThrottle.RecordFailure(client, out int triesLeft);
            if (pause > TimeSpan.Zero)
                ViewBag.Error = $"Wrong password. Too many wrong passwords: wait {LoginThrottle.Describe(pause)}, then try again.";
            else if (triesLeft <= 2)
                ViewBag.Error = $"Invalid password. {triesLeft} more {(triesLeft == 1 ? "try" : "tries")} before a short pause.";
            else
                ViewBag.Error = "Invalid password. Try again.";
            return View();
        }

        public ActionResult Logout()
        {
            Session.Clear();
            MediaAccess.Clear(HttpContext);
            return RedirectToAction("Index");
        }

        private void LogIn(string client, string role)
        {
            LoginThrottle.Reset(client);
            Session["Auth"] = true;
            Session["Role"] = role;
            MediaAccess.Issue(HttpContext, role, Session.Timeout); // lets the browser load /media files
        }
    }
}
