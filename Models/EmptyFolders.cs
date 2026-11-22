using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MediaManager.Models
{
    /// <summary>
    /// Finds folders with nothing worth keeping (Empty Folders page):
    ///   - empty: no files at all below them, or only Windows / Mac clutter (Thumbs.db, desktop.ini, .DS_Store).
    ///     A branch of empty folders is reported once, as its top folder.
    ///   - no media: files, but none the site shows (no photos, videos, audio or documents), so the Gallery
    ///     shows them as empty. Listed only; what's in them is left for the owner to look at.
    /// Folders the site skips (Constants.ExcludedDirectories: the Recycle Bin, .thumbs, system folders...) count
    /// as content, so a folder holding one is never reported, and they're never looked inside. A folder that can't
    /// be read counts as full.
    /// </summary>
    public static class EmptyFolders
    {
        /// <summary>Files that don't count: Windows / Mac make these by themselves.</summary>
        public static readonly string[] Clutter = { "Thumbs.db", "ehthumbs.db", "desktop.ini", ".DS_Store" };

        private const int ExampleCount = 3;

        public class NoMediaFolder
        {
            public string Folder;          // relative to the media root
            public int FileCount;
            public long Bytes;
            public List<string> Examples;  // a few file names
        }

        public class Result
        {
            public List<string> Empty = new List<string>();      // relative to the media root
            public List<NoMediaFolder> NoMedia = new List<NoMediaFolder>();
            public int FoldersChecked;
        }

        /// <summary>One folder and everything below it.</summary>
        private class Node
        {
            public string Path;
            public bool HasFiles;    // anything other than clutter, a folder the site skips, or unreadable
            public bool HasMedia;
            public int FileCount;
            public long Bytes;
            public List<string> Examples = new List<string>();
            public List<Node> Children = new List<Node>();
        }

        /// <summary>Checks everything below a folder (physical path); the folder itself is never reported.</summary>
        public static Result Find(string folderPath) => Find(folderPath, MediaPaths.ToRelative);

        /// <param name="toRelative">Physical path to the "a/b" form the page shows (replaceable for tests outside IIS).</param>
        internal static Result Find(string folderPath, Func<string, string> toRelative)
        {
            var result = new Result();
            var top = Walk(folderPath, result);
            foreach (var child in top.Children)
                Report(child, result, toRelative);
            result.Empty.Sort(NaturalStringComparer.Instance);
            result.NoMedia.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Folder, b.Folder));
            return result;
        }

        /// <summary>True if nothing below the folder counts as content (checked again just before deleting it).</summary>
        public static bool IsEmpty(string folderPath) => !Walk(folderPath, new Result()).HasFiles;

        public static bool IsClutter(string fileName) =>
            Clutter.Any(c => string.Equals(c, fileName, StringComparison.OrdinalIgnoreCase));

        private static Node Walk(string path, Result result)
        {
            result.FoldersChecked++;
            var node = new Node { Path = path };
            try
            {
                foreach (string file in Directory.EnumerateFiles(path))
                {
                    string name = System.IO.Path.GetFileName(file);
                    if (IsClutter(name))
                        continue;
                    node.HasFiles = true;
                    node.FileCount++;
                    try { node.Bytes += new FileInfo(file).Length; } catch (IOException) { }
                    if (MediaPaths.IsSupportedMedia(file))
                        node.HasMedia = true;
                    else if (node.Examples.Count < ExampleCount)
                        node.Examples.Add(name);
                }

                foreach (string dir in Directory.EnumerateDirectories(path))
                {
                    if (MediaPaths.IsExcludedFolder(System.IO.Path.GetFileName(dir)))
                    {
                        node.HasFiles = true; // e.g. $RECYCLE.BIN or .thumbs: not ours to judge
                        continue;
                    }
                    var child = Walk(dir, result);
                    node.Children.Add(child);
                    node.HasFiles |= child.HasFiles;
                    node.HasMedia |= child.HasMedia;
                    node.FileCount += child.FileCount;
                    node.Bytes += child.Bytes;
                    node.Examples.AddRange(child.Examples.Take(ExampleCount - node.Examples.Count));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                node.HasFiles = true; // can't look inside: leave it alone
                node.HasMedia = true;
            }
            return node;
        }

        /// <summary>Reports the folder if it tops an empty / no-media branch; otherwise looks at its subfolders.</summary>
        private static void Report(Node node, Result result, Func<string, string> toRelative)
        {
            if (!node.HasFiles)
            {
                result.Empty.Add(toRelative(node.Path));
                return;
            }
            if (!node.HasMedia && node.FileCount == 0)
                return; // only folders the site skips (like $RECYCLE.BIN) below it: leave it out
            if (!node.HasMedia)
            {
                result.NoMedia.Add(new NoMediaFolder
                {
                    Folder = toRelative(node.Path),
                    FileCount = node.FileCount,
                    Bytes = node.Bytes,
                    Examples = node.Examples
                });
                return;
            }
            foreach (var child in node.Children)
                Report(child, result, toRelative);
        }
    }
}
