using System;

namespace MediaManager.Filters
{
    /// <summary>
    /// Marks an action (or whole controller) that guests may use. Everything else is owner-only,
    /// so new features are safe by default. Enforced by AuthFilter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public class GuestAllowedAttribute : Attribute
    {
    }
}
