using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>The Guestbook (Models/Guestbook.cs): guests and the owner read and sign it; only the owner deletes.</summary>
    [AuthFilter]
    public class GuestbookController : Controller
    {
        [GuestAllowed]
        public ActionResult Index()
        {
            ViewBag.Name = Session["GuestbookName"] as string;
            return View(Guestbook.GetRecent());
        }

        [HttpPost]
        [GuestAllowed]
        [ValidateAntiForgeryToken]
        public ActionResult Sign(string name, string message)
        {
            bool isGuest = Roles.IsGuest(Session);
            name = (name ?? "").Trim();
            message = (message ?? "").Trim();
            if (name.Length == 0)
                name = isGuest ? "A guest" : "Owner";

            if (message.Length == 0)
                TempData["GuestbookError"] = "Please write a message.";
            else if (name.Length > Guestbook.MaxName || message.Length > Guestbook.MaxMessage)
                TempData["GuestbookError"] = $"That's too long (names up to {Guestbook.MaxName} characters, messages up to {Guestbook.MaxMessage}).";
            else
            {
                Guestbook.Add(name, message, isGuest ? Roles.Guest : Roles.Owner);
                Session["GuestbookName"] = name; // fill it in next time
                ActivityLog.Write(ActivityLog.Guestbook, name + " signed the guestbook: \""
                    + (message.Length > 100 ? message.Substring(0, 100) + "..." : message) + "\"");
            }
            return RedirectToAction("Index");
        }

        /// <summary>Owner only (not [GuestAllowed]).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(int id)
        {
            Guestbook.Delete(id);
            return RedirectToAction("Index");
        }
    }
}
