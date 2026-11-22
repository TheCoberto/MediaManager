using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MediaManager.Models
{
    /// <summary>
    /// Every file in the library with its size and dates, read in one walk of the media folder and kept for a few
    /// minutes: what the Storage and Recently Added pages work from. Folders the site skips
    /// (Constants.ExcludedDirectories) aren't listed, except the site's own (.recycle, .thumbs, .playable, .uploads),
    /// whose total sizes are kept separately so the Storage page can show what they take.
    /// </summary>
    public static class LibraryIndex
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

        /// <summary>The site's own folders at the top of the media folder, and what to call them.</summary>
        public static readonly Dictionary<string, string> SiteFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { RecycleBin.FolderName, "Recycle Bin" },
            { Thumbnails.FolderName, "Thumbnails" },
            { PlayableCopy.FolderName, "Playable copies of AVI videos" },
            { ChunkedUpload.FolderName, "Uploads in progress" },
        };

        public class Entry
        {
            public string Folder;     // relative to the media root, "a/b" ('' = top)
            public string Name;
            public long Size;
            public DateTime Created;  // when it arrived in the library (local time)
            public DateTime Modified;
            public string Kind;       // MediaKind's "image" / "video" / "audio" / "document", or null for other files
        }

        public class Snapshot
        {
            public List<Entry> Files = new List<Entry>();
            public Dictionary<string, long> SiteFolderBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public int UnreadableFolders;
            public DateTime BuiltAt;
        }

        private static Snapshot current;
        private static readonly object BuildLock = new object();

        /// <summary>The index, built now if it's missing, old or <paramref name="refresh"/> is asked for.</summary>
        public static Snapshot Get(bool refresh = false)
        {
            var snap = Volatile.Read(ref current);
            if (!refresh && snap != null && DateTime.Now - snap.BuiltAt < KeepFor)
                return snap;

            lock (BuildLock)
            {
                snap = Volatile.Read(ref current);
                if (!refresh && snap != null && DateTime.Now - snap.BuiltAt < KeepFor)
                    return snap; // built by another request while this one waited
                snap = Build(MediaPaths.Root, MediaPaths.ToRelative);
                Volatile.Write(ref current, snap);
                return snap;
            }
        }

        /// <summary>Drop the index (after big changes through the site, so the pages don't show the old picture).</summary>
        public static void Forget() => Volatile.Write(ref current, null);

        /// <param name="toRelative">Physical path to "a/b" form (replaceable for tests outside IIS).</param>
        internal static Snapshot Build(string root, Func<string, string> toRelative)
        {
            var snap = new Snapshot();
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            bool atTop = true;

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string folder = atTop ? "" : toRelative(dir.FullName);
                try
                {
                    foreach (var f in dir.EnumerateFiles())
                        snap.Files.Add(new Entry
                        {
                            Folder = folder,
                            Name = f.Name,
                            Size = f.Length,
                            Created = f.CreationTime,
                            Modified = f.LastWriteTime,
                            Kind = MediaPaths.KindOf(f.Name)
                        });

                    foreach (var sub in dir.EnumerateDirectories())
                    {
                        if (atTop && SiteFolders.ContainsKey(sub.Name))
                            snap.SiteFolderBytes[sub.Name] = FolderBytes(sub);
                        else if (!MediaPaths.IsExcludedFolder(sub.Name))
                            pending.Push(sub);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    snap.UnreadableFolders++;
                }
                atTop = false;
            }

            snap.BuiltAt = DateTime.Now;
            return snap;
        }

        private static long FolderBytes(DirectoryInfo dir)
        {
            try
            {
                return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>True if a file's folder is <paramref name="folder"/> or inside it ('' = everything).</summary>
        public static bool IsUnder(string fileFolder, string folder) =>
            folder.Length == 0
            || fileFolder.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || fileFolder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }
}
