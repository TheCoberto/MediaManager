using MediaManager.Models;
using System.IO;
using System.Web.Hosting;
using System.Web.Mvc;

namespace MediaManager
{
    public static class UrlHelperExtensions
    {
        /// <summary>
        /// Like Url.Content, plus "?v=" and the file's last-modified time, so browsers fetch a script again after a
        /// deploy changes it instead of running the copy they cached (phones keep those for days, and a page
        /// calling a function the old script doesn't have just stops). Bundles get this from System.Web.Optimization;
        /// use this for files loaded with their own &lt;script&gt; tag. Read on each request (one file-system
        /// lookup) because a deploy that only changes scripts doesn't restart the app.
        /// </summary>
        public static string Versioned(this UrlHelper url, string contentPath)
        {
            string href = url.Content(contentPath);
            string file = HostingEnvironment.MapPath(contentPath);
            return file != null && File.Exists(file)
                ? href + "?v=" + File.GetLastWriteTimeUtc(file).Ticks.ToString("x")
                : href;
        }

        // Links to a folder's or a file's page. With Feature:HideNames (Models/HiddenNames.cs) they carry a token
        // ("f", and "w" for Storage Random's folder) instead of the names; the actions take either form.

        /// <summary>The Gallery page of a folder ("a/b").</summary>
        public static string GalleryLink(this UrlHelper url, string folder)
        {
            return url.FolderLink("Gallery", "Media", folder);
        }

        /// <summary>Any action that takes a folder (and an "f" token in its place): Gallery, Duplicates, a folder's ZIP.</summary>
        public static string FolderLink(this UrlHelper url, string action, string controller, string folder)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            return HiddenNames.Enabled
                ? url.Action(action, controller, new { f = HiddenNames.Encode(folder) })
                : url.Action(action, controller, new { folder });
        }

        /// <summary>The View page of a file ("a/b" + "c.jpg"; folder "" for a file at the very top).</summary>
        public static string ViewLink(this UrlHelper url, string folder, string file)
        {
            return HiddenNames.Enabled
                ? url.Action("ViewMedia", "Media", new { f = HiddenNames.Encode(JoinPath(folder, file)) })
                : url.Action("ViewMedia", "Media", new { folder, file });
        }

        /// <summary>
        /// Storage Random: a random file (file null), or that file; within: only pick from this folder (null or "" for everywhere).
        /// </summary>
        public static string RandomLink(this UrlHelper url, string folder, string file, string within)
        {
            within = MediaPaths.NormalizeFolder(within);
            if (!HiddenNames.Enabled)
                return url.Action("StorageRandom", "Media", new
                {
                    folder = file == null ? null : folder,
                    file,
                    within = within.Length > 0 ? within : null
                });

            return url.Action("StorageRandom", "Media", new
            {
                f = file == null ? null : HiddenNames.Encode(JoinPath(folder, file)),
                w = within.Length > 0 ? HiddenNames.Encode(within) : null
            });
        }

        private static string JoinPath(string folder, string file)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            return folder.Length == 0 ? file : folder + "/" + file;
        }
    }
}
