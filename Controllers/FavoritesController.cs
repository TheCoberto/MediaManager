using MediaManager.Filters;
using MediaManager.Models;
using MediaManager.Models.Constants;
using System;
using System.IO;
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
        }

        public ActionResult Index()
        {
            var items = Favorites.GetAll().Select(f =>
            {
                string path = MediaPaths.ResolveFile(f.Folder, f.FileName);
                return new FavoriteView
                {
                    FavoriteId = f.FavoriteId,
                    Folder = f.Folder,
                    FileName = f.FileName,
                    CreatedAt = f.CreatedAt,
                    Exists = path != null && System.IO.File.Exists(path),
                    Url = MediaPaths.UrlFor(f.Folder + "/" + f.FileName)
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
            return Json(new { success = true, favorite });
        }

        /// <summary>Remove a favorite by id (used for favorites whose file has gone missing).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Remove(int id)
        {
            Favorites.RemoveById(id);
            return Json(new { success = true });
        }
    }
}
