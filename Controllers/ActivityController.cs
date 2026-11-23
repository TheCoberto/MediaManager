using MediaManager.Filters;
using MediaManager.Models;
using System.Data.SqlClient;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>Activity page: what the site has been doing (Models/ActivityLog.cs). Owner only.</summary>
    [AuthFilter]
    public class ActivityController : Controller
    {
        private const int PageSize = 200;

        /// <param name="before">Show entries older than this one (the page's "Older" link).</param>
        /// <param name="errors">Errors only.</param>
        public ActionResult Index(int? before = null, bool errors = false)
        {
            ViewBag.Errors = errors;
            ViewBag.PageSize = PageSize;
            try
            {
                // The "Last 7 days" summary, on the first page only
                if (before == null && !errors)
                    ViewBag.Summary = ActivityLog.SummaryFor(7);
                return View(ActivityLog.Recent(PageSize, before, errors));
            }
            catch (SqlException ex)
            {
                // Most likely the database hasn't been updated with SQL\Schema.sql yet
                ViewBag.LoadError = ex.Message;
                return View(new System.Collections.Generic.List<ActivityLog.Entry>());
            }
        }

        /// <summary>
        /// Ends every login on every other device, owner and guests (a lost phone, a password that got out).
        /// This browser gets a new login, so you stay logged in here.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SignOutEverywhere()
        {
            if (MediaAccess.SignOutEverywhere(HttpContext, Roles.Owner, SiteSettings.LoginMinutes, out string error))
            {
                ActivityLog.Write(ActivityLog.Login, "Logged out all other devices: they have to log in again.");
                TempData["SignOutMessage"] = "Done. Every other device, and any guest, has to log in again.";
            }
            else
            {
                ActivityLog.Error(ActivityLog.Login, "Log out all other devices failed: " + error);
                TempData["SignOutError"] = "Couldn't log out the other devices: " + error;
            }
            return RedirectToAction("Index");
        }
    }
}
