using MediaManager.Filters;
using MediaManager.Models;
using System.Web.Mvc;
using System.Web.SessionState;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Playable MP4 copies of videos browsers can't play (Models/PlayableCopy.cs). Owner only.
    ///
    /// Read-only session on purpose: the page polls Status while a copy is made, and a normal request would hold
    /// the session lock, making the rest of the site wait behind it.
    /// </summary>
    [AuthFilter]
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class PlayableController : Controller
    {
        /// <summary>Whether the copy is ready (with its URL), being made (with progress), failed, or not started.</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult Status(string folder, string file)
        {
            string path = Resolve(folder, file, out string error);
            if (path == null)
                return Json(new { success = false, message = error }, JsonRequestBehavior.AllowGet);
            return Json(Answer(PlayableCopy.GetStatus(path, folder, file)), JsonRequestBehavior.AllowGet);
        }

        /// <summary>Starts making the copy (retry: even if it failed recently).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Start(string folder, string file, bool retry = false)
        {
            string path = Resolve(folder, file, out string error);
            if (path == null)
                return Json(new { success = false, message = error });
            return Json(Answer(PlayableCopy.Start(path, folder, file, retry)));
        }

        private static string Resolve(string folder, string file, out string error)
        {
            error = null;
            string path = MediaPaths.ResolveFile(folder, file);
            if (path == null || !System.IO.File.Exists(path))
                error = "File not found.";
            else if (!PlayableCopy.IsNeeded(file))
                error = "This type of video plays in the browser as it is.";
            return error == null ? path : null;
        }

        private static object Answer(PlayableCopy.Status s) =>
            new { success = true, state = s.State, url = s.Url, percent = s.Percent, message = s.Message };
    }
}
