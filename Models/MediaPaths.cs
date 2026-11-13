using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using AppConstants = MediaManager.Models.Constants.Constants;

namespace MediaManager.Models
{
    /// <summary>
    /// All media lives under the IIS virtual directory "/media". Each IIS site points that
    /// virtual directory at its own root folder, which is the only thing that differs between sites.
    /// Every path that comes from a request must go through Resolve() so it can't escape the root.
    /// </summary>
    public static class MediaPaths
    {
        public const string VirtualRoot = "~/media";

        /// <summary>Physical media root, always ending in a backslash (e.g. "E:\" or "D:\Media\").</summary>
        public static string Root
        {
            get
            {
                var path = Path.GetFullPath(HostingEnvironment.MapPath(VirtualRoot));
                return path.EndsWith("\\") ? path : path + "\\";
            }
        }

        /// <summary>Normalizes a folder from a request to "a/b/c" form (forward slashes, no leading/trailing slash).</summary>
        public static string NormalizeFolder(string folder)
        {
            return (folder ?? "").Trim().Replace('\\', '/').Trim('/');
        }

        /// <summary>Physical path of a folder relative to the media root, or null if it would escape the root.</summary>
        public static string ResolveFolder(string folder)
        {
            var root = Root;
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, NormalizeFolder(folder).Replace('/', '\\')));
            }
            catch (Exception)
            {
                return null;
            }

            var withSlash = full.EndsWith("\\") ? full : full + "\\";
            return withSlash.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>Physical path of a file in a folder, or null if either part is invalid.</summary>
        public static string ResolveFile(string folder, string fileName)
        {
            if (!IsPlainFileName(fileName))
                return null;

            var dir = ResolveFolder(folder);
            return dir == null ? null : Path.Combine(dir, fileName);
        }

        /// <summary>True for a bare file name with no path parts or invalid characters.</summary>
        public static bool IsPlainFileName(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && name == Path.GetFileName(name)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && name != "." && name != "..";
        }

        public static bool IsSupportedMedia(string path)
        {
            return AppConstants.SupportedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>"image", "video", "audio", "document", or null for an unsupported file.</summary>
        public static string KindOf(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (AppConstants.ImageExtensions.Contains(ext)) return MediaKind.Image;
            if (AppConstants.VideoExtensions.Contains(ext)) return MediaKind.Video;
            if (AppConstants.AudioExtensions.Contains(ext)) return MediaKind.Audio;
            if (AppConstants.DocumentExtensions.Contains(ext)) return MediaKind.Document;
            return null;
        }

        public static bool IsExcludedFolder(string folderName)
        {
            return AppConstants.ExcludedDirectories.Any(ex => string.Equals(ex, folderName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Names of the first-level folders under the media root (sorted), skipping excluded folders.</summary>
        public static List<string> GetTopLevelFolders()
        {
            try
            {
                return Directory.GetDirectories(Root)
                    .Select(Path.GetFileName)
                    .Where(name => !IsExcludedFolder(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Physical paths of every folder under the media root (depth-first), skipping excluded folders.</summary>
        public static List<string> GetFolders()
        {
            var result = new List<string>();
            AddFolders(Root, result);
            return result;
        }

        private static void AddFolders(string path, List<string> result)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(path);
            }
            catch (Exception)
            {
                return; // inaccessible folder
            }

            foreach (var dir in dirs)
            {
                if (IsExcludedFolder(Path.GetFileName(dir)))
                    continue;

                result.Add(dir);
                AddFolders(dir, result);
            }
        }

        /// <summary>
        /// Supported files in one folder (physical path), optionally with everything in its subfolders too
        /// (skipping excluded folders), in name order.
        /// </summary>
        public static List<string> GetMediaFiles(string folderPath, bool includeSubfolders)
        {
            var folders = new List<string> { folderPath };
            if (includeSubfolders)
                AddFolders(folderPath, folders);

            var files = new List<string>();
            foreach (var folder in folders)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(folder).Where(IsSupportedMedia)
                        .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance));
                }
                catch (Exception)
                {
                    // inaccessible folder
                }
            }
            return files;
        }

        /// <summary>Physical paths of every supported media file under the root, skipping excluded folders.</summary>
        public static List<string> GetAllMediaFiles()
        {
            var folders = new List<string> { Root };
            folders.AddRange(GetFolders());

            var files = new List<string>();
            foreach (var folder in folders)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(folder).Where(IsSupportedMedia));
                }
                catch (Exception)
                {
                    // inaccessible folder
                }
            }
            return files;
        }

        /// <summary>Browser URL for a media file, e.g. "a/b/my file.mp4" -> "/media/a/b/my%20file.mp4".</summary>
        public static string UrlFor(string relativePath)
        {
            var segments = NormalizeFolder(relativePath)
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString);
            return System.Web.VirtualPathUtility.ToAbsolute(VirtualRoot) + "/" + string.Join("/", segments);
        }

        /// <summary>Converts a physical path under the root to "a/b/file.ext" form.</summary>
        public static string ToRelative(string fullPath)
        {
            var root = Root;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length).Replace('\\', '/').Trim('/')
                : fullPath.Replace('\\', '/');
        }
    }
}
