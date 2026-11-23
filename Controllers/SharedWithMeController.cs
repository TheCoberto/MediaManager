using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Folders the owner shared with guests (Models/SharedFolders.cs): a read-only browser for guests (and a
    /// preview of what they see for the owner). Sharing is turned on and off from the owner's Gallery page.
    /// </summary>
    [AuthFilter]
    public class SharedWithMeController : Controller
    {
        private const int PageSize = 40;

        public class SharedFile
        {
            public string FileName { get; set; }
            public string Url { get; set; }
            public string ThumbUrl { get; set; }
        }

        public class SharedFolderView
        {
            public string Folder { get; set; }
            /// <summary>Breadcrumb parts from the shared folder down: (name, folder path).</summary>
            public List<Tuple<string, string>> Crumbs { get; set; }
            public List<string> Subfolders { get; set; }
            public List<SharedFile> Files { get; set; }
            public int TotalFiles { get; set; }
            public int Page { get; set; }
            public int PageCount { get; set; }
        }

        /// <summary>The list of shared folders.</summary>
        [GuestAllowed]
        public ActionResult Index()
        {
            var folders = SharedFolders.GetAll().Where(f => MediaPaths.ResolveFolder(f) is string p && Directory.Exists(p)).ToList();
            return View(folders);
        }

        /// <param name="f">A hidden-name link's token, in place of folder (Models/HiddenNames.cs).</param>
        [GuestAllowed]
        public ActionResult Folder(string folder, string f = null, int page = 1)
        {
            if (f != null && (folder = HiddenNames.Decode(f)) == null)
                return HttpNotFound();

            folder = MediaPaths.NormalizeFolder(folder);
            string path = MediaPaths.ResolveFolder(folder);
            if (!SharedFolders.IsShared(folder) || path == null || !Directory.Exists(path))
                return HttpNotFound();

            var files = MediaPaths.GetMediaFiles(path, includeSubfolders: false);
            int pageCount = Math.Max(1, (files.Count + PageSize - 1) / PageSize);
            page = Math.Min(Math.Max(1, page), pageCount);

            var model = new SharedFolderView
            {
                Folder = folder,
                Crumbs = Crumbs(folder),
                TotalFiles = files.Count,
                Page = page,
                PageCount = pageCount,
                Subfolders = SafeSubfolders(path),
                Files = files.Skip((page - 1) * PageSize).Take(PageSize).Select(p =>
                {
                    string name = Path.GetFileName(p);
                    return new SharedFile
                    {
                        FileName = name,
                        Url = MediaPaths.VersionedUrl(folder + "/" + name, new FileInfo(p)),
                        ThumbUrl = Thumbnails.UrlFor(Url, folder, name)
                    };
                }).ToList()
            };
            return View(model);
        }

        /// <summary>From the topmost shared folder containing this one down to it, so guests can't climb above it.</summary>
        private static List<Tuple<string, string>> Crumbs(string folder)
        {
            var parts = folder.Split('/');
            var crumbs = new List<Tuple<string, string>>();
            for (int i = 0; i < parts.Length; i++)
            {
                string sub = string.Join("/", parts.Take(i + 1));
                if (crumbs.Count > 0 || SharedFolders.IsShared(sub))
                    crumbs.Add(Tuple.Create(parts[i], sub));
            }
            return crumbs;
        }

        private static List<string> SafeSubfolders(string path)
        {
            try
            {
                return Directory.GetDirectories(path)
                    .Select(Path.GetFileName)
                    .Where(n => !MediaPaths.IsExcludedFolder(n))
                    .OrderBy(n => n, NaturalStringComparer.Instance)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        /// <summary>Owner only (not [GuestAllowed]): share / stop sharing a folder.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Set(string folder, bool shared)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            string path = MediaPaths.ResolveFolder(folder);
            if (folder.Length == 0 || path == null || !Directory.Exists(path))
                return Json(new { success = false, message = "Unknown folder." });

            SharedFolders.Set(folder, shared);
            ActivityLog.Write(ActivityLog.Login, (shared ? "Shared with guests: " : "Stopped sharing with guests: ") + folder + ".");
            return Json(new { success = true, shared, sharedByParent = !shared && SharedFolders.IsShared(folder) });
        }
    }
}
