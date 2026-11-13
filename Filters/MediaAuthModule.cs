using MediaManager.Models;
using System;
using System.Linq;
using System.Web;

namespace MediaManager.Filters
{
    /// <summary>
    /// Keeps the files under /media behind the login.
    ///
    /// IIS serves those files itself (so videos seek and stream properly), which means AuthFilter never
    /// sees them. This module runs for every request, including those static files (it's registered in
    /// Web.config without a preCondition), and lets a /media request through only with a valid media pass
    /// (Models\MediaAccess): the owner can see everything; guests only the guest uploads folder.
    /// </summary>
    public class MediaAuthModule : IHttpModule
    {
        public void Init(HttpApplication application)
        {
            application.AuthorizeRequest += (sender, e) => Check(((HttpApplication)sender).Context);
        }

        public void Dispose()
        {
        }

        private static void Check(HttpContext context)
        {
            // App-relative and already decoded/normalized by IIS, e.g. "~/media/Videos/cat.mp4"
            string path = context.Request.AppRelativeCurrentExecutionFilePath;
            if (!IsUnder(path, MediaPaths.VirtualRoot))
                return;

            // Never let http.sys keep a copy it could hand out later without asking us.
            context.Response.DisableKernelCache();

            var http = new HttpContextWrapper(context);
            var pass = MediaAccess.Read(http);
            if (pass == null)
            {
                Deny(context, 401, "Log in to see this file.");
                return;
            }

            if (pass.Role == Roles.Guest && !GuestMayOpen(path))
            {
                Deny(context, 403, "Guests can't see this file.");
                return;
            }

            MediaAccess.RenewIfOld(http, pass);
        }

        private static bool GuestMayOpen(string path)
        {
            // IIS has already resolved "..", but be strict anyway since this is the guest boundary.
            if (path.Contains("\\") || path.Split('/').Any(seg => seg == ".." || seg == "."))
                return false;

            string guestFolder = MediaPaths.NormalizeFolder(SiteSettings.ImageUploadFolder);
            string guestRoot = guestFolder.Length == 0 ? MediaPaths.VirtualRoot : MediaPaths.VirtualRoot + "/" + guestFolder;
            return path.StartsWith(guestRoot + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True if path is the folder itself or anything inside it (case-insensitive, like Windows paths).</summary>
        private static bool IsUnder(string path, string folder)
        {
            return path != null
                && (path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static void Deny(HttpContext context, int status, string message)
        {
            var request = context.Request;
            var response = context.Response;

            // Opening a file directly in a tab (not as an <img>/<video> on a page): go to the login page and
            // come back to the file afterwards.
            bool isPageNavigation = request.HttpMethod == "GET"
                && (request.AcceptTypes ?? new string[0]).Any(t => t.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));

            if (status == 401 && isPageNavigation)
            {
                string login = VirtualPathUtility.ToAbsolute("~/Login");
                response.Redirect(login + "?returnUrl=" + HttpUtility.UrlEncode(request.RawUrl), false);
            }
            else
            {
                response.Clear();
                response.StatusCode = status;
                response.TrySkipIisCustomErrors = true;
                response.ContentType = "text/plain";
                response.Write(message);
            }

            context.ApplicationInstance.CompleteRequest(); // IIS never gets to serve the file
        }
    }
}
