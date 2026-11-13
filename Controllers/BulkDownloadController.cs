using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.IO;
using System.Linq;
using System.Web.Mvc;
using System.Web.SessionState;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Several files at once as one ZIP (Models/ZipDownload.cs): a whole folder, the files ticked in a
    /// gallery, or all favorites. Owner only.
    ///
    /// Read-only session on purpose: a normal request holds the session lock until it finishes, so a long
    /// download would make every other page of the site wait for it.
    /// </summary>
    [AuthFilter]
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class BulkDownloadController : Controller
    {
        /// <summary>File counts and sizes for the "Download folder" dialog.</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult FolderSummary(string folder)
        {
            string path = ResolveFolder(folder);
            if (path == null)
                return Json(new { success = false, message = "Folder not found." }, JsonRequestBehavior.AllowGet);

            var here = MediaPaths.GetMediaFiles(path, false);
            var all = MediaPaths.GetMediaFiles(path, true);
            return Json(new
            {
                success = true,
                name = Path.GetFileName(path.TrimEnd('\\')),
                files = here.Count,
                size = FileViewModel.FormatFileSize(TotalSize(here)),
                withSubfoldersFiles = all.Count,
                withSubfoldersSize = FileViewModel.FormatFileSize(TotalSize(all))
            }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>Every file in a folder (and optionally its subfolders, keeping their folder structure).</summary>
        public ActionResult Folder(string folder, bool subfolders = false)
        {
            string path = ResolveFolder(folder);
            if (path == null)
                return HttpNotFound();

            string root = path.TrimEnd('\\') + "\\";
            var items = MediaPaths.GetMediaFiles(path, subfolders).Select(f => new ZipDownload.Item
            {
                FullPath = f,
                ZipPath = f.Substring(root.Length).Replace('\\', '/')
            });

            ZipDownload.Send(HttpContext, Path.GetFileName(path.TrimEnd('\\')) + ".zip", items);
            return new EmptyResult();
        }

        /// <summary>
        /// The files ticked in a gallery. <paramref name="files"/> is one path per line, relative to the folder:
        /// "a.jpg", or "Kittens/a.jpg" when the gallery includes subfolders (kept as folders inside the ZIP).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Selected(string folder, string files)
        {
            string path = ResolveFolder(folder);
            if (path == null)
                return HttpNotFound();
            string root = path.TrimEnd('\\') + "\\";

            var names = (files ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(MediaPaths.NormalizeFolder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    int slash = p.LastIndexOf('/');
                    string sub = slash < 0 ? "" : p.Substring(0, slash);
                    string full = MediaPaths.ResolveFile(MediaPaths.NormalizeFolder(folder) + (sub.Length > 0 ? "/" + sub : ""), p.Substring(slash + 1));
                    return new { rel = p, full };
                })
                // only files inside this folder (or its subfolders)
                .Where(x => x.full != null && x.full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && MediaPaths.IsSupportedMedia(x.full))
                .Select(x => new ZipDownload.Item { FullPath = x.full, ZipPath = x.full.Substring(root.Length).Replace('\\', '/') })
                .ToList();
            if (names.Count == 0)
                return HttpNotFound();

            string zipName = Path.GetFileName(path.TrimEnd('\\')) + " (" + names.Count + " file" + (names.Count == 1 ? "" : "s") + ").zip";
            ZipDownload.Send(HttpContext, zipName, names);
            return new EmptyResult();
        }

        /// <summary>All favorites that still exist, in their folders ("Cats/Kittens/a.jpg").</summary>
        public ActionResult Favorites()
        {
            var items = Models.Favorites.GetAll()
                .Select(f => new { f, full = MediaPaths.ResolveFile(f.Folder, f.FileName) })
                .Where(x => x.full != null && System.IO.File.Exists(x.full))
                .Select(x => new ZipDownload.Item
                {
                    FullPath = x.full,
                    ZipPath = (string.IsNullOrEmpty(x.f.Folder) ? "" : MediaPaths.NormalizeFolder(x.f.Folder) + "/") + x.f.FileName
                })
                .GroupBy(i => i.ZipPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .ToList();

            ZipDownload.Send(HttpContext, "Favorites.zip", items);
            return new EmptyResult();
        }

        /// <summary>Physical path of an existing folder under the media root (not the root itself, not the site's hidden folders).</summary>
        private static string ResolveFolder(string folder)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            string path = folder.Length == 0 ? null : MediaPaths.ResolveFolder(folder);
            if (path == null || !Directory.Exists(path))
                return null;

            bool hidden = MediaPaths.ToRelative(path).Split('/').Any(p =>
                string.Equals(p, RecycleBin.FolderName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, ChunkedUpload.FolderName, StringComparison.OrdinalIgnoreCase));
            return hidden || path.TrimEnd('\\').Length + 1 <= MediaPaths.Root.Length ? null : path;
        }

        private static long TotalSize(System.Collections.Generic.IEnumerable<string> files)
        {
            long total = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch (IOException) { }
            }
            return total;
        }
    }
}
