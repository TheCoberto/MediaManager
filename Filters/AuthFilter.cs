using MediaManager.Models;
using System.Web.Mvc;
using System.Web.Routing;

namespace MediaManager.Filters
{
    public class AuthFilter : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            var http = filterContext.HttpContext;

            if (http.Session["Auth"] == null)
            {
                if (http.Request.IsAjaxRequest())
                {
                    // Buttons / background requests: a clear answer the page can show (instead of the login page's
                    // HTML, which used to come out as "Error: undefined").
                    http.Response.StatusCode = 401;
                    http.Response.TrySkipIisCustomErrors = true; // keep our JSON, not IIS's own 401 page
                    filterContext.Result = new JsonResult
                    {
                        Data = new { success = false, loginRequired = true, message = "Your login has expired. Log in again, then try that once more." },
                        JsonRequestBehavior = JsonRequestBehavior.AllowGet
                    };
                    return;
                }

                // Pages: go to the login page and come back here afterwards
                string returnUrl = http.Request.HttpMethod == "GET" ? http.Request.RawUrl : null;
                filterContext.Result = new RedirectToRouteResult(new RouteValueDictionary
                {
                    { "controller", "Login" }, { "action", "Index" }, { "returnUrl", returnUrl }
                });
                return;
            }

            // Keep the /media pass (Models\MediaAccess) alive as long as the session is, and recreate it if it's
            // missing, e.g. a browser that was logged in before media needed a login.
            bool isGuest = Roles.IsGuest(http.Session);
            MediaAccess.Refresh(http, isGuest ? Roles.Guest : Roles.Owner, http.Session.Timeout);

            // Guests may only use actions marked [GuestAllowed].
            if (isGuest && !IsGuestAllowed(filterContext.ActionDescriptor))
            {
                if (http.Request.HttpMethod == "GET" && !http.Request.IsAjaxRequest())
                {
                    // Pages they can't see just send them to the guest home page.
                    filterContext.Result = new RedirectToRouteResult(
                        new RouteValueDictionary { { "controller", "Images" }, { "action", "ImageViewAll" } });
                }
                else
                {
                    filterContext.Result = new HttpStatusCodeResult(403, "Guests can't do that.");
                }
            }
        }

        private static bool IsGuestAllowed(ActionDescriptor action)
        {
            return action.IsDefined(typeof(GuestAllowedAttribute), true)
                || action.ControllerDescriptor.IsDefined(typeof(GuestAllowedAttribute), true);
        }
    }
}
