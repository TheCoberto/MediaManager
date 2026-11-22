using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Tags (Models/Tags.cs): the Tags page (manage tags, browse files by tag) and the calls the View page and the
    /// Gallery make to tag files. Owner only.
    /// </summary>
    [AuthFilter]
    public class TagsController : Controller
    {
        public class FileView
        {
            public string Folder { get; set; }
            public string FileName { get; set; }
            public DateTime TaggedAt { get; set; }
            public List<string> TagNames { get; set; }
            public bool Exists { get; set; }
            public string Url { get; set; }
            public string ThumbUrl { get; set; }
        }

        /// <param name="tags">Tag ids to browse, comma-separated ("" = just manage tags).</param>
        /// <param name="all">Files with every one of those tags (otherwise any of them).</param>
        /// <param name="sort">"tagged" (newest first), "name" or "folder".</param>
        public ActionResult Index(string tags = "", bool all = false, string sort = "tagged")
        {
            var allTags = Tags.GetAll();
            var picked = (tags ?? "").Split(',').Select(s => int.TryParse(s, out int id) ? id : 0)
                .Where(id => id > 0 && allTags.Any(t => t.TagId == id)).Distinct().ToList();
            var names = allTags.ToDictionary(t => t.TagId, t => t.Name);

            var files = Tags.FilesWith(picked, all).Select(f =>
            {
                string path = MediaPaths.ResolveFile(f.Folder, f.FileName);
                bool exists = path != null && System.IO.File.Exists(path);
                return new FileView
                {
                    Folder = f.Folder,
                    FileName = f.FileName,
                    TaggedAt = f.TaggedAt,
                    TagNames = (f.TagIds ?? "").Split(',').Select(s => int.TryParse(s, out int id) && names.TryGetValue(id, out string n) ? n : null)
                        .Where(n => n != null).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    Exists = exists,
                    Url = MediaPaths.UrlFor((f.Folder.Length == 0 ? "" : f.Folder + "/") + f.FileName),
                    ThumbUrl = exists ? Thumbnails.UrlFor(Url, f.Folder, f.FileName) : null
                };
            });

            switch (sort)
            {
                case "name":
                    files = files.OrderBy(f => f.FileName, NaturalStringComparer.Instance);
                    break;
                case "folder":
                    files = files.OrderBy(f => f.Folder, NaturalStringComparer.Instance).ThenBy(f => f.FileName, NaturalStringComparer.Instance);
                    break;
                default:
                    sort = "tagged";
                    files = files.OrderByDescending(f => f.TaggedAt);
                    break;
            }

            ViewBag.AllTags = allTags;
            ViewBag.Picked = picked;
            ViewBag.MatchAll = all;
            ViewBag.Sort = sort;
            return View(files.ToList());
        }

        // ---------- tags ----------

        /// <summary>Every tag with its file count (for the tag boxes' suggestions).</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult List()
        {
            return Json(Tags.GetAll(), JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Create(string name)
        {
            var tag = Tags.GetOrCreate(name);
            return tag == null
                ? Json(new { success = false, message = $"Enter a tag name of up to {Tags.MaxNameLength} characters." })
                : Json(new { success = true, tag });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Rename(int id, string name)
        {
            if (Tags.CleanName(name) == null)
                return Json(new { success = false, message = $"Enter a tag name of up to {Tags.MaxNameLength} characters." });
            if (!Tags.Rename(id, name))
                return Json(new { success = false, message = "There's already a tag called \"" + Tags.CleanName(name) + "\"." });
            ActivityLog.Write(ActivityLog.Tags, "Renamed a tag to \"" + Tags.CleanName(name) + "\".");
            return Json(new { success = true, name = Tags.CleanName(name) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Delete(int id)
        {
            var tag = Tags.GetAll().FirstOrDefault(t => t.TagId == id);
            if (tag == null)
                return Json(new { success = false, message = "That tag doesn't exist any more." });
            Tags.Delete(id);
            ActivityLog.Write(ActivityLog.Tags, $"Deleted the tag \"{tag.Name}\" (it was on {tag.FileCount} file{(tag.FileCount == 1 ? "" : "s")}).");
            return Json(new { success = true });
        }

        // ---------- files ----------

        /// <summary>One file's tags.</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult ForFile(string folder, string file)
        {
            if (MediaPaths.ResolveFile(folder, file) == null)
                return Json(new { success = false, message = "Invalid file." }, JsonRequestBehavior.AllowGet);
            return Json(new { success = true, tags = Tags.For(folder, file) }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Puts a tag (by name: created if it's new) on one or more files.
        /// files: one per line, "folder|file name" ('|' can't be in a Windows file name).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Add(string name, string files)
        {
            var tag = Tags.GetOrCreate(name);
            if (tag == null)
                return Json(new { success = false, message = $"Enter a tag name of up to {Tags.MaxNameLength} characters." });

            int tagged = 0;
            var missing = new List<string>();
            foreach (var (folder, file) in ParseFiles(files))
            {
                string path = MediaPaths.ResolveFile(folder, file);
                if (path == null || !System.IO.File.Exists(path))
                {
                    missing.Add(file);
                    continue;
                }
                Tags.Add(tag.TagId, folder, file);
                tagged++;
            }

            if (tagged > 1)
                ActivityLog.Write(ActivityLog.Tags, $"Tagged {tagged} files \"{tag.Name}\".");
            return Json(new { success = true, tag, tagged, missing });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Remove(int id, string folder, string file)
        {
            if (MediaPaths.ResolveFile(folder, file) == null)
                return Json(new { success = false, message = "Invalid file." });
            Tags.Remove(id, folder, file);
            return Json(new { success = true });
        }

        private static IEnumerable<(string folder, string file)> ParseFiles(string files)
        {
            foreach (string line in (files ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int bar = line.LastIndexOf('|');
                if (bar >= 0)
                    yield return (MediaPaths.NormalizeFolder(line.Substring(0, bar)), line.Substring(bar + 1));
            }
        }
    }
}
