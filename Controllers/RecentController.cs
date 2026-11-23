using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Recently Added page: the newest photos, videos, audio and documents across the whole library, by when they
    /// arrived in it (the file's created date: an upload, a copy onto the drive, a download). Owner only.
    /// </summary>
    [AuthFilter]
    public class RecentController : Controller
    {
        private const int MaxShown = 300;
        public static readonly int[] DayChoices = { 1, 7, 30, 90 };

        public class Item
        {
            public string Folder;
            public string Name;
            public string Kind;
            public long Size;
            public DateTime Added;
            public string Url;
            public string Thumb;   // thumbnail URL, or null (show the picture itself, or an icon)
            public int Views;      // Most Viewed only
        }

        /// <summary>Choices on the Most Viewed page; 0 = all time.</summary>
        public static readonly int[] ViewDayChoices = { 7, 30, 0 };

        /// <summary>Most Viewed page: the files whose pages were opened most (Models/MediaViews.cs).</summary>
        public ActionResult MostViewed(int days = 30)
        {
            if (!ViewDayChoices.Contains(days))
                days = 30;

            var top = MediaViews.Top(100, days == 0 ? (DateTime?)null : DateTime.Now.AddDays(-days));
            ViewBag.Days = days;
            return View(top.Select(t =>
            {
                string relative = (t.Folder.Length == 0 ? "" : t.Folder + "/") + t.FileName;
                string kind = MediaPaths.KindOf(t.FileName);
                return new Item
                {
                    Folder = t.Folder,
                    Name = t.FileName,
                    Kind = kind,
                    Added = t.LastAt,
                    Views = t.Views,
                    Url = MediaPaths.UrlFor(relative),
                    Thumb = kind == MediaKind.Image || kind == MediaKind.Video ? Thumbnails.UrlFor(Url, t.Folder, t.FileName) : null
                };
            }).ToList());
        }

        public ActionResult Index(int days = 7, bool refresh = false)
        {
            if (!DayChoices.Contains(days))
                days = 7;
            DateTime since = DateTime.Now.AddDays(-days);

            var snap = LibraryIndex.Get(refresh);
            var recent = snap.Files
                .Where(f => f.Kind != null && f.Created >= since)
                .OrderByDescending(f => f.Created)
                .ToList();

            ViewBag.Days = days;
            ViewBag.Total = recent.Count;
            ViewBag.BuiltAt = snap.BuiltAt;
            return View(recent.Take(MaxShown).Select(f =>
            {
                string relative = (f.Folder.Length == 0 ? "" : f.Folder + "/") + f.Name;
                return new Item
                {
                    Folder = f.Folder,
                    Name = f.Name,
                    Kind = f.Kind,
                    Size = f.Size,
                    Added = f.Created,
                    Url = MediaPaths.UrlFor(relative),
                    Thumb = f.Kind == MediaKind.Image || f.Kind == MediaKind.Video ? Thumbnails.UrlFor(Url, f.Folder, f.Name) : null
                };
            }).ToList());
        }
    }
}
