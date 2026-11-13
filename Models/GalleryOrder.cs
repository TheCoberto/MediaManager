using System;
using System.Collections.Generic;

namespace MediaManager.Models
{
    /// <summary>
    /// The order a gallery shows its files in. Every order is a full, stable ordering of file names, so the
    /// gallery can ask for "the next files after X" and never repeat or skip one.
    ///
    /// Shuffled: each file's place comes from a hash of (seed, name), so the same seed always gives the same
    /// shuffle no matter when or how often the folder is re-read, and adding / removing a file doesn't move
    /// any other file. Each browser tab has its own seed (Refresh Gallery picks a new one).
    /// </summary>
    public static class GalleryOrder
    {
        public static IComparer<string> For(string sort, int seed)
        {
            if (string.Equals(sort, "name", StringComparison.OrdinalIgnoreCase))
                return new ByName(descending: false);
            if (string.Equals(sort, "name-desc", StringComparison.OrdinalIgnoreCase))
                return new ByName(descending: true);
            return new Shuffled(seed);
        }

        // Natural name order ("2" before "10"), with plain comparisons to break ties so no two
        // different names ever count as equal (that would make "after X" skip one).
        private sealed class ByName : IComparer<string>
        {
            private readonly bool descending;
            public ByName(bool descending) { this.descending = descending; }

            public int Compare(string a, string b)
            {
                int c = NaturalStringComparer.Instance.Compare(a, b);
                if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (c == 0) c = StringComparer.Ordinal.Compare(a, b);
                return descending ? -c : c;
            }
        }

        private sealed class Shuffled : IComparer<string>
        {
            private readonly int seed;
            // Sorting compares each name many times: work its key out once (one instance per request)
            private readonly Dictionary<string, ulong> keys = new Dictionary<string, ulong>(StringComparer.Ordinal);
            public Shuffled(int seed) { this.seed = seed; }

            public int Compare(string a, string b)
            {
                int c = CachedKey(a).CompareTo(CachedKey(b));
                if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (c == 0) c = StringComparer.Ordinal.Compare(a, b);
                return c;
            }

            private ulong CachedKey(string name)
            {
                if (!keys.TryGetValue(name, out ulong k))
                    keys[name] = k = Key(name);
                return k;
            }

            // FNV-1a 64-bit over the seed and the lower-case name: fast, and the same on every run
            // (string.GetHashCode isn't guaranteed to be).
            private ulong Key(string name)
            {
                const ulong prime = 1099511628211UL;
                ulong hash = 14695981039346656037UL;
                unchecked
                {
                    for (int i = 0; i < 4; i++)
                    {
                        hash ^= (byte)(seed >> (8 * i));
                        hash *= prime;
                    }
                    foreach (char ch in name.ToLowerInvariant())
                    {
                        hash ^= (byte)ch;
                        hash *= prime;
                        hash ^= (byte)(ch >> 8);
                        hash *= prime;
                    }
                    // final mix so similar names don't end up next to each other
                    hash ^= hash >> 33;
                    hash *= 0xff51afd7ed558ccdUL;
                    hash ^= hash >> 33;
                }
                return hash;
            }
        }
    }
}
