using System.Web;

namespace MediaManager.Models
{
    /// <summary>
    /// Who is logged in. The owner password gives full control; the optional guest password
    /// (GuestPasswordHash in Site.config) only gives access to actions marked [GuestAllowed].
    /// </summary>
    public static class Roles
    {
        public const string Owner = "Owner";
        public const string Guest = "Guest";

        public static bool IsGuest(HttpSessionStateBase session)
        {
            return session != null && (session["Role"] as string) == Guest;
        }
    }
}
