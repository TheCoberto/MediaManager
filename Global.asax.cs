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
        }
    }
}
