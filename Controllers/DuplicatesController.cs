using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Mvc;
using System.Web.SessionState;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Duplicates page: finds files with identical contents (Models/DuplicateFinder.cs) and moves the extra copies to
    /// the Recycle Bin. Owner only.
    ///
    /// Read-only session on purpose: the page polls Status while a scan runs, and a normal request would hold the
    /// session lock, making the rest of the site wait behind it. Deleting happens in MediaController.DeleteDuplicate,
    /// next to the other file tools.
    /// </summary>
    [AuthFilter]
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class DuplicatesController : Controller
    {
        /// <summary>Most groups sent to the page: past this, delete some and scan again.</summary>
        private const int MaxGroups = 1000;

        public ActionResult Index(string folder = "")
        {
            folder = MediaPaths.NormalizeFolder(folder);
            if (MediaPaths.ResolveFolder(folder) == null || !Directory.Exists(MediaPaths.ResolveFolder(folder)))
                return HttpNotFound();

            ViewBag.Folder = folder;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Start(string folder, bool subfolders = true)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            string path = MediaPaths.ResolveFolder(folder);
            if (path == null || !Directory.Exists(path))
                return Json(new { success = false, message = "Folder not found." });

            var scan = DuplicateFinder.Start(folder, path, subfolders);
            return Json(new { success = true, id = scan.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Stop(string id)
        {
            DuplicateFinder.Stop(id);
            return Json(new { success = true });
        }

        /// <summary>A scan's progress, and its results once it's done.</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult Status(string id)
        {
            var scan = DuplicateFinder.Get(id);
            if (scan == null)
                return Json(new { success = false, message = "That scan has expired. Start a new one." }, JsonRequestBehavior.AllowGet);

            object groups = null;
            int groupCount = 0;
            if (scan.Phase == "done")
            {
                groupCount = scan.Groups.Count;
                groups = scan.Groups
                    .Select(g => g.Where(System.IO.File.Exists).ToList())   // some may have been deleted since
                    .Where(g => g.Count > 1)
                    .Take(MaxGroups)
                    .Select(g => g.Select(Describe).ToList())
                    .ToList();
            }

            return Json(new
            {
                success = true,
                phase = scan.Phase,
                filesFound = scan.FilesFound,
                done = scan.Done,
                total = scan.Total,
                bytesDone = Interlocked.Read(ref scan.BytesDone),
                bytesTotal = Interlocked.Read(ref scan.BytesTotal),
                skipped = scan.Skipped,
                error = scan.Error,
                groupCount,
                maxGroups = MaxGroups,
                groups
            }, JsonRequestBehavior.AllowGet);
        }

        private object Describe(string path)
        {
            var info = new FileInfo(path);
            string relative = MediaPaths.ToRelative(path);
            int slash = relative.LastIndexOf('/');
            string folder = slash < 0 ? "" : relative.Substring(0, slash);
            return new
            {
                folder,
                file = info.Name,
                size = info.Length,
                modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                url = MediaPaths.UrlFor(relative),
                thumb = Thumbnails.UrlFor(Url, folder, info.Name, info),
                kind = MediaPaths.KindOf(info.Name)
            };
        }
    }
}
