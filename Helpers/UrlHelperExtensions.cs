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
    }
}
