using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;

namespace MediaManager
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);

            // Server warnings / errors (Trace.TraceWarning / TraceError) go to the Activity page too
            System.Diagnostics.Trace.Listeners.Add(new Models.ActivityLogTraceListener());

            // Don't announce the MVC version (X-AspNetMvc-Version)
            MvcHandler.DisableMvcResponseHeader = true;

            // X-Frame-Options comes from Web.config for every answer; without this, pages with a form token
            // (Html.AntiForgeryToken) would send it a second time ("SAMEORIGIN,SAMEORIGIN").
            System.Web.Helpers.AntiForgeryConfig.SuppressXFrameOptionsHeader = true;
        }

        protected void Application_BeginRequest()
        {
            // On HTTPS, tell the browser to only ever use HTTPS for this site (for a year). Not sent over plain HTTP,
            // where browsers ignore it anyway.
            if (Request.IsSecureConnection)
                Response.AppendHeader("Strict-Transport-Security", "max-age=31536000");
        }

        protected void Application_PreSendRequestHeaders()
        {
            // Don't announce the web server's name and version ("Server: Microsoft-IIS/10.0")
            Response.Headers.Remove("Server");
        }
    }
}
