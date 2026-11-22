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
                return View(ActivityLog.Recent(PageSize, before, errors));
            }
            catch (SqlException ex)
            {
                // Most likely the database hasn't been updated with SQL\Schema.sql yet
                ViewBag.LoadError = ex.Message;
                return View(new System.Collections.Generic.List<ActivityLog.Entry>());
            }
        }
    }
}
