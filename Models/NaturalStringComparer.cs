using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MediaManager.Models
{
    /// <summary>
    /// Sorts names the way Windows Explorer does: case-insensitive, with numbers compared by value
    /// ("IMG_2" before "IMG_10").
    /// </summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public int Compare(string x, string y)
        {
            return StrCmpLogicalW(x ?? "", y ?? "");
        }
    }
}
