using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Linq;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>The owner's favorites (owner only: not marked [GuestAllowed]).</summary>
    [AuthFilter]
    public class FavoritesController : Controller
    {
        public class FavoriteView
        {
            public int FavoriteId { get; set; }
            public string Folder { get; set; }
            public string FileName { get; set; }
            public DateTime CreatedAt { get; set; }
            public bool Exists { get; set; }
            public string Url { get; set; }
            /// <summary>Thumbnail URL, or null to show the file itself.</summary>
            public string ThumbUrl { get; set; }
        }

        public ActionResult Index()
        {
            var items = Favorites.GetAll().Select(f =>
            {
                string path = MediaPaths.ResolveFile(f.Folder, f.FileName);
                bool exists = path != null && System.IO.File.Exists(path);
                return new FavoriteView
                {
                    FavoriteId = f.FavoriteId,
                    Folder = f.Folder,
                    FileName = f.FileName,
                    CreatedAt = f.CreatedAt,
                    Exists = exists,
                    Url = MediaPaths.UrlFor(f.Folder + "/" + f.FileName),
                    ThumbUrl = exists ? Thumbnails.UrlFor(Url, f.Folder, f.FileName) : null
                };
            }).ToList();

            return View(items);
        }

        /// <summary>Star / unstar a file.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Set(string folder, string file, bool favorite)
        {
            string path = MediaPaths.ResolveFile(folder, file);
            if (path == null)
                return Json(new { success = false, message = "Invalid file." });
            if (favorite && !System.IO.File.Exists(path))
                return Json(new { success = false, message = "File not found." });

            Favorites.Set(folder, file, favorite);
            ActivityLog.Write(ActivityLog.Favorites, favorite ? "Added to favorites." : "Removed from favorites.", folder, file);
            return Json(new { success = true, favorite });
        }

        /// <summary>Remove a favorite by id (used for favorites whose file has gone missing).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Remove(int id)
        {
            var gone = Favorites.Get(id);
            Favorites.RemoveById(id);
            if (gone != null)
                ActivityLog.Write(ActivityLog.Favorites, "Removed a missing file from favorites: "
                    + (string.IsNullOrEmpty(gone.Folder) ? "" : gone.Folder + "/") + gone.FileName + ".");
            return Json(new { success = true });
        }
    }
}
