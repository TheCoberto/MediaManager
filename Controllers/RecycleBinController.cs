using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.IO;
using System.Web;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>The site's Recycle Bin (owner only: not marked [GuestAllowed]).</summary>
    [AuthFilter]
    public class RecycleBinController : Controller
    {
        public ActionResult Index()
        {
            return View(RecycleBin.GetAll());
        }

        /// <summary>Image preview for the Recycle Bin page (recycled files aren't reachable via /media).</summary>
        public ActionResult Preview(int id)
        {
            var item = RecycleBin.Get(id);
            var path = item == null ? null : RecycleBin.StoredFullPath(item);
            if (path == null || !item.IsImage || !System.IO.File.Exists(path))
                return HttpNotFound();

            return File(path, MimeMapping.GetMimeMapping(item.FileName));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Restore(int id)
        {
            try
            {
                TempData["Message"] = "Restored to " + RecycleBin.Restore(id);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                TempData["Error"] = "Couldn't restore: " + ex.Message;
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteForever(int id)
        {
            try
            {
                RecycleBin.DeleteForever(id);
                TempData["Message"] = "Deleted forever.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TempData["Error"] = "Couldn't delete it (a file may be open in another program): " + ex.Message;
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Empty()
        {
            try
            {
                int count = RecycleBin.Empty();
                TempData["Message"] = $"Recycle Bin emptied ({count} item{(count == 1 ? "" : "s")} deleted forever).";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TempData["Error"] = "Some items couldn't be deleted (a file may be open in another program): " + ex.Message;
            }
            return RedirectToAction("Index");
        }
    }
}
