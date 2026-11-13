using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.SessionState;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Thumbnails (Models/Thumbnails.cs). Owner only.
    ///
    /// Read-only session on purpose: a normal request holds the session lock until it finishes, so a page's
    /// thumbnails would load one at a time instead of side by side.
    /// </summary>
    [AuthFilter]
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class ThumbnailsController : Controller
    {
        /// <summary>
        /// A file's thumbnail (JPEG), or a redirect to the file itself if it can't have one.
        /// The URL carries a version (v) that changes with the file, so the browser may keep it for a long time.
        /// </summary>
        public async Task<ActionResult> Get(string folder, string file)
        {
            string path = MediaPaths.ResolveFile(folder, file);
            if (path == null || !MediaPaths.IsSupportedMedia(path) || !System.IO.File.Exists(path))
                return HttpNotFound();

            string thumb = await Thumbnails.GetOrCreateAsync(path, folder, file);
            if (thumb == null)
                return Redirect(MediaPaths.UrlFor(MediaPaths.NormalizeFolder(folder) + "/" + file));

            Response.Cache.SetCacheability(HttpCacheability.Private);
            Response.Cache.SetMaxAge(TimeSpan.FromDays(30));
            return File(thumb, "image/jpeg");
        }
    }
}
