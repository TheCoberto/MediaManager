using MediaManager.Models;
using System.Web.Mvc;

namespace MediaManager.Filters
{
    /// <summary>
    /// Returns 404 for a whole controller/action when the site has the feature switched off
    /// (Feature:Name in Site.config). Runs after the login check.
    /// </summary>
    public class FeatureAttribute : ActionFilterAttribute
    {
        private readonly string _feature;
        private readonly bool _enabledByDefault;

        public FeatureAttribute(string feature, bool enabledByDefault)
        {
            _feature = feature;
            _enabledByDefault = enabledByDefault;
            Order = 1; // AuthFilter (default order -1) runs first
        }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            if (!SiteSettings.IsFeatureEnabled(_feature, _enabledByDefault))
                filterContext.Result = new HttpNotFoundResult();
        }
    }
}
