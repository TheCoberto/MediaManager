using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Linq;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    [AuthFilter]
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            // Every folder is rendered; the page hides those under a hidden top-level folder
            // so they can be shown again instantly from the dropdown.
            ViewBag.Folders = MediaPaths.GetFolders()
                .Select(MediaPaths.ToRelative) // relative paths like Folder/SubFolder
                .ToList();
            ViewBag.TopLevelFolders = MediaPaths.GetTopLevelFolders();
            ViewBag.HiddenFolders = HiddenFolders.GetAll();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult SetFolderHidden(string folder, bool hidden)
        {
            if (!MediaPaths.GetTopLevelFolders().Contains(folder ?? "", StringComparer.OrdinalIgnoreCase))
                return Json(new { success = false, message = "Unknown folder." });

            HiddenFolders.Set(folder, hidden);
            return Json(new { success = true });
        }
    }
}
