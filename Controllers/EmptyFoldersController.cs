using MediaManager.Filters;
using MediaManager.Models;
using System.IO;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>
    /// Empty Folders page (Models/EmptyFolders.cs). Owner only. Deleting happens in
    /// MediaController.DeleteEmptyFolders, next to the other folder tools.
    /// </summary>
    [AuthFilter]
    public class EmptyFoldersController : Controller
    {
        /// <param name="folder">Where to look ('' = the whole library).</param>
        /// <param name="scan">Look now (the page opens without scanning, as a big library takes a moment).</param>
        public ActionResult Index(string folder = "", bool scan = false)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            string path = MediaPaths.ResolveFolder(folder);
            if (path == null || !Directory.Exists(path))
                return HttpNotFound();

            ViewBag.Folder = folder;
            return View(scan ? EmptyFolders.Find(path) : null);
        }
    }
}
