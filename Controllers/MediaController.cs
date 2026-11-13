using MediaManager.Filters;
using MediaManager.Models;
using MediaManager.Models.Constants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    [AuthFilter]
    public class MediaController : Controller
    {
        private const string AllFilesCacheKey = "MediaCache__AllFiles";

        public ActionResult Gallery(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return RedirectToAction("Index", "Home");

            // Accept an absolute path under the root too ("E:/Videos/Cats" -> "Videos/Cats")
            folder = folder.Replace("\\", "/");
            var root = MediaPaths.Root.Replace("\\", "/");
            if (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                folder = folder.Substring(root.Length);

            folder = MediaPaths.NormalizeFolder(folder);
            if (MediaPaths.ResolveFolder(folder) == null)
                return HttpNotFound();

            ViewBag.Folder = folder;
            ViewBag.MediaBaseUrl = MediaPaths.UrlFor(folder);
            ViewBag.Subfolders = GetSubfolders(MediaPaths.ResolveFolder(folder));
            return View("Gallery");
        }

        public class SubfolderInfo
        {
            public string Name { get; set; }
            /// <summary>Files directly in it (not counting its own subfolders).</summary>
            public int FileCount { get; set; }
            public bool HasSubfolders { get; set; }
        }

        /// <summary>The folders directly inside a folder (skipping excluded ones), in name order.</summary>
        private static List<SubfolderInfo> GetSubfolders(string folderPath)
        {
            try
            {
                return Directory.GetDirectories(folderPath)
                    .Where(d => !MediaPaths.IsExcludedFolder(Path.GetFileName(d)))
                    .OrderBy(d => Path.GetFileName(d), NaturalStringComparer.Instance)
                    .Select(d =>
                    {
                        var info = new SubfolderInfo { Name = Path.GetFileName(d) };
                        try
                        {
                            info.FileCount = Directory.EnumerateFiles(d).Count(MediaPaths.IsSupportedMedia);
                            info.HasSubfolders = Directory.EnumerateDirectories(d)
                                .Any(s => !MediaPaths.IsExcludedFolder(Path.GetFileName(s)));
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                        return info;
                    })
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<SubfolderInfo>();
            }
        }

        /// <summary>
        /// One batch of a gallery, in a stable order, continuing after <paramref name="after"/> (the last file
        /// the page has) instead of by page number, so nothing repeats or gets skipped while you scroll, even
        /// when files are deleted / moved / added meanwhile or the server re-reads the folder.
        /// </summary>
        /// <param name="sort">"random" (shuffled by <paramref name="seed"/>), "name" (A to Z) or "name-desc" (Z to A).</param>
        /// <param name="seed">The page's shuffle number: the same seed always gives the same shuffle.</param>
        /// <param name="subfolders">Also include everything in the folder's subfolders (the "Include subfolders" switch).</param>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult GetMedia(string folder, string after = null, int pageSize = 10, string search = "", string sort = "random",
            int seed = 0, bool subfolders = false)
        {
            var empty = Json(new { Files = new List<object>(), TotalCount = 0, HasMore = false }, JsonRequestBehavior.AllowGet);

            string folderPath = MediaPaths.ResolveFolder(folder);
            if (string.IsNullOrWhiteSpace(folder) || folderPath == null || !Directory.Exists(folderPath))
                return empty;
            folder = MediaPaths.NormalizeFolder(folder);
            pageSize = Math.Max(1, Math.Min(pageSize, 100));

            // Paths relative to this folder ("a.jpg", or "Kittens/a.jpg" with subfolders), cached for a while.
            // No order here: that's worked out per request below.
            string cacheKey = BuildMediaCacheKey(folder) + (subfolders ? AllSubfoldersSuffix : "");
            var cache = MemoryCache.Default;
            var files = cache.Get(cacheKey) as List<string>;

            if (files == null)
            {
                string root = folderPath.TrimEnd('\\') + "\\";
                files = MediaPaths.GetMediaFiles(folderPath, subfolders)
                    .Select(f => f.Substring(root.Length).Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                cache.Set(cacheKey, files, DateTimeOffset.Now.AddMinutes(10));
            }

            // Apply search (with subfolders, a subfolder's name matches too)
            var filtered = string.IsNullOrEmpty(search)
                ? files
                : files.Where(f => f.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            int totalCount = filtered.Count;

            // A full, stable order; then everything after the last file the page already has. The cursor is
            // just a path, so it still works if that file has since been deleted or renamed.
            var order = GalleryOrder.For(sort, seed);
            var remaining = filtered.Where(f => after == null || order.Compare(f, after) > 0)
                .OrderBy(f => f, order)
                .ToList();
            var paged = remaining.Take(pageSize).ToList();

            // Split "Kittens/a.jpg" into the subfolder and the file name; each item knows its own folder
            var items = paged.Select(p =>
            {
                int slash = p.LastIndexOf('/');
                string sub = slash < 0 ? "" : p.Substring(0, slash);
                return new { Path = p, Sub = sub, Name = p.Substring(slash + 1), Folder = sub.Length == 0 ? folder : folder + "/" + sub };
            }).ToList();

            // Look up corrupted / favorite status for just this page's files, folder by folder
            var byFolder = items.GroupBy(i => i.Folder, StringComparer.OrdinalIgnoreCase).ToList();
            var corruptedByFolder = byFolder.ToDictionary(g => g.Key, g => Flags.CorruptedIn(g.Key, g.Select(i => i.Name).ToList()),
                StringComparer.OrdinalIgnoreCase);
            var favoritesByFolder = byFolder.ToDictionary(g => g.Key, g => Favorites.GetFileNamesIn(g.Key), StringComparer.OrdinalIgnoreCase);

            var result = items.Select(i =>
            {
                var info = FileInfoOrNull(Path.Combine(folderPath, i.Path.Replace('/', '\\')));
                return new
                {
                    i.Path,                // the gallery's key and paging cursor
                    FileName = i.Name,
                    Subfolder = i.Sub,     // "" = this folder
                    i.Folder,              // the file's own folder, for its buttons
                    Corrupted = corruptedByFolder[i.Folder].Contains(i.Name),
                    Favorite = favoritesByFolder[i.Folder].Contains(i.Name),
                    Size = info?.Length ?? 0,                                // for the "N selected · size" bar
                    Thumb = info == null ? null : Thumbnails.UrlFor(Url, i.Folder, i.Name, info) // null: show the file itself
                };
            });

            return Json(new { Files = result, TotalCount = totalCount, HasMore = remaining.Count > paged.Count }, JsonRequestBehavior.AllowGet);
        }

        private static FileInfo FileInfoOrNull(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info : null; // moved / deleted since the list was cached
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Every folder under the media root ("a/b" form, sorted), for the Move dialog's folder picker.</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult GetFolders()
        {
            var folders = MediaPaths.GetFolders()
                .Select(MediaPaths.ToRelative)
                .OrderBy(f => f, NaturalStringComparer.Instance)
                .ToList();
            return Json(folders, JsonRequestBehavior.AllowGet);
        }

        public JsonResult GetMediaCount(string folder)
        {
            var folderPath = MediaPaths.ResolveFolder(folder);
            if (string.IsNullOrWhiteSpace(folder) || folderPath == null || !Directory.Exists(folderPath))
                return Json(0, JsonRequestBehavior.AllowGet);

            var count = Directory.GetFiles(folderPath).Count(MediaPaths.IsSupportedMedia);
            return Json(count, JsonRequestBehavior.AllowGet);
        }

        public ActionResult AddMedia()
        {
            return View();
        }

        /// <summary>One-request upload (kept for small files / older pages). The Add Media page uses AddMediaChunk.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddMedia(HttpPostedFileBase file, string newFileName, string folder)
        {
            if (file == null || file.ContentLength <= 0)
                return Json(new { success = false, message = "No file was uploaded." });
            if (file.ContentLength > SiteSettings.MaxUploadBytes)
                return Json(new { success = false, message = "This file is too big. The limit is " + ChunkedUpload.FormatLimit(SiteSettings.MaxUploadBytes) + "." });

            string error = CheckAddMedia(file.FileName, newFileName, folder, out var safeFolder, out var folderPath, out var targetName);
            if (error != null)
                return Json(new { success = false, message = error });

            return PlaceAddMedia(safeFolder, folderPath, targetName, file.SaveAs);
        }

        /// <summary>
        /// Add Media upload, one piece at a time (any size up to MaxUploadMB). Every piece carries the same
        /// details; the name and folder are checked on each one, so a bad name fails before the data is sent.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult AddMediaChunk(string uploadId, long offset, long totalSize, string fileName, string newFileName, string folder, HttpPostedFileBase chunk)
        {
            string error = CheckAddMedia(fileName, newFileName, folder, out var safeFolder, out var folderPath, out var targetName);
            if (error != null)
            {
                ChunkedUpload.Discard(uploadId);
                return Json(new { success = false, message = error });
            }

            var r = ChunkedUpload.Append(uploadId, offset, totalSize, SiteSettings.MaxUploadBytes, chunk);
            if (!r.Ok)
                return Json(new { success = false, message = r.Error, receivedSoFar = r.ReceivedSoFar });
            if (!r.Complete)
                return Json(new { success = true, complete = false });

            try
            {
                return PlaceAddMedia(safeFolder, folderPath, targetName, target => System.IO.File.Move(r.PartPath, target));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ChunkedUpload.Discard(uploadId);
                return Json(new { success = false, message = "Couldn't save the file: " + ex.Message });
            }
        }

        /// <summary>Checks an Add Media upload's name, type and folder. Returns an error message, or null if it's fine.</summary>
        private static string CheckAddMedia(string originalName, string newFileName, string folder,
            out string safeFolder, out string folderPath, out string targetName)
        {
            safeFolder = folderPath = targetName = null;

            if (string.IsNullOrWhiteSpace(newFileName) || newFileName.Trim().Length >= 75)
                return "File name is missing or too long (max 74 characters).";
            if (newFileName.Contains("#") || newFileName.Contains("/") || newFileName.Contains("\\"))
                return "File name cannot contain #, /, or \\.";
            if (string.IsNullOrWhiteSpace(folder))
                return "Folder is required.";

            safeFolder = MediaPaths.NormalizeFolder(folder).TrimStart('~', '/');
            folderPath = MediaPaths.ResolveFolder(safeFolder);
            if (safeFolder.Length == 0 || folderPath == null)
                return "Folder is invalid.";
            // Not into the site's own hidden folders (checked on the resolved path: Windows drops trailing dots)
            if (MediaPaths.ToRelative(folderPath).Split('/').Any(p =>
                    string.Equals(p, RecycleBin.FolderName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, ChunkedUpload.FolderName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, Thumbnails.FolderName, StringComparison.OrdinalIgnoreCase)))
                return "Folder is invalid.";

            string uploadExt = Path.GetExtension(originalName ?? "").ToLowerInvariant();
            if (!Constants.SupportedExtensions.Contains(uploadExt))
                return "Unsupported file type: " + (uploadExt.Length > 0 ? uploadExt : "(no extension)") + ".";

            // If no extension was typed, keep the uploaded file's one
            targetName = newFileName.Trim();
            if (!targetName.Contains("."))
                targetName += uploadExt;

            return MediaPaths.IsPlainFileName(targetName) ? null : "File name is invalid.";
        }

        /// <summary>Puts an upload in its folder under a name that's free ("cat.jpg" -> "cat (2).jpg"), never overwriting.</summary>
        private JsonResult PlaceAddMedia(string safeFolder, string folderPath, string requestedName, Action<string> place)
        {
            Directory.CreateDirectory(folderPath);

            string name = requestedName;
            string baseName = Path.GetFileNameWithoutExtension(requestedName);
            string ext = Path.GetExtension(requestedName);
            for (int n = 2; System.IO.File.Exists(Path.Combine(folderPath, name)); n++)
                name = $"{baseName} ({n}){ext}";

            place(Path.Combine(folderPath, name));
            InvalidateFolderCache(safeFolder);

            string message = name == requestedName
                ? "\"" + name + "\" uploaded to " + safeFolder + "."
                : "Saved as \"" + name + "\" in " + safeFolder + " (\"" + requestedName + "\" already existed).";
            return Json(new { success = true, complete = true, fileName = name, renamed = name != requestedName, message });
        }

        public ActionResult UploadFailed()
        {
            return View("~/Views/Shared/UploadFailed.cshtml");
        }

        public ActionResult ViewMedia(string folder, string file)
        {
            string fullPath = MediaPaths.ResolveFile(folder, file);
            if (string.IsNullOrWhiteSpace(folder) || fullPath == null || !System.IO.File.Exists(fullPath))
                return HttpNotFound();

            ViewBag.Folder = MediaPaths.NormalizeFolder(folder);
            ViewBag.File = file;
            ViewBag.Url = MediaPaths.UrlFor(folder + "/" + file);

            // File details for the info panel (dimensions/duration are filled in by the browser)
            var info = new FileInfo(fullPath);
            var model = new FileViewModel
            {
                FileName = info.Name,
                FullPath = info.FullName,
                RelativePath = MediaPaths.ToRelative(info.FullName),
                FileSize = info.Length,
                FileType = MimeMapping.GetMimeMapping(info.Name),
                FileExtension = info.Extension,
                CreatedDate = info.CreationTime,
                LastModified = info.LastWriteTime,
                FilesInDirectory = Directory.GetFiles(info.DirectoryName).Count(MediaPaths.IsSupportedMedia)
            };

            // Previous / Next within the folder, in name order (like Windows Explorer)
            var siblings = Directory.GetFiles(info.DirectoryName)
                .Where(MediaPaths.IsSupportedMedia)
                .Select(Path.GetFileName)
                .OrderBy(n => n, NaturalStringComparer.Instance)
                .ToList();
            int index = siblings.FindIndex(n => string.Equals(n, info.Name, StringComparison.OrdinalIgnoreCase));
            ViewBag.Prev = index > 0 ? siblings[index - 1] : null;
            ViewBag.Next = index >= 0 && index < siblings.Count - 1 ? siblings[index + 1] : null;
            ViewBag.Position = index + 1;

            ViewBag.IsFavorite = Favorites.IsFavorite(folder, info.Name);
            ViewBag.IsCorrupted = Flags.Get(folder, info.Name).Corrupted;

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult DeleteMedia(string folder, string file)
        {
            string fullPath = MediaPaths.ResolveFile(folder, file);
            if (string.IsNullOrWhiteSpace(folder) || fullPath == null)
                return Json(new { success = false, message = "Invalid parameters." });

            if (!System.IO.File.Exists(fullPath))
                return Json(new { success = false, message = "File not found." });

            RecycleBin.Recycle(fullPath, RecycleBin.SourceGallery); // restorable from the Recycle Bin page
            Favorites.Set(folder, file, false);
            Flags.Forget(folder, file);
            Thumbnails.Forget(folder, file);
            InvalidateFolderCache(folder);

            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult RenameMedia(string folder, string oldName, string newName)
        {
            newName = newName?.Trim();
            string oldPath = MediaPaths.ResolveFile(folder, oldName);
            string newPath = MediaPaths.ResolveFile(folder, newName);

            if (string.IsNullOrWhiteSpace(folder) || oldPath == null)
                return Json(new { success = false, message = "Invalid parameters." });
            if (newPath == null)
                return Json(new { success = false, message = "New name is invalid (no slashes or special characters)." });

            if (!System.IO.File.Exists(oldPath))
                return Json(new { success = false, message = "Original file not found." });

            if (System.IO.File.Exists(newPath))
                return Json(new { success = false, message = "A file with that name already exists." });

            System.IO.File.Move(oldPath, newPath);
            Favorites.Rename(folder, oldName, newName);
            Flags.Move(folder, oldName, folder, newName);
            Thumbnails.Forget(folder, oldName);
            InvalidateFolderCache(folder);

            return Json(new { success = true, newName });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        /// <param name="keepBoth">
        /// If the destination already has a file with this name: false = refuse and say so (conflict = true,
        /// with the name it would get); true = move it under that free name ("cat (2).jpg"). Never overwrites.
        /// </param>
        public JsonResult MoveMedia(string folder, string file, string newFolder, bool keepBoth = false)
        {
            string oldPath = MediaPaths.ResolveFile(folder, file);
            string destinationFolder = MediaPaths.ResolveFolder(newFolder);

            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(newFolder) || oldPath == null)
                return Json(new { success = false, message = "Invalid parameters." });
            if (destinationFolder == null)
                return Json(new { success = false, message = "Destination folder is invalid." });

            if (!System.IO.File.Exists(oldPath))
                return Json(new { success = false, message = "File not found." });

            if (string.Equals(MediaPaths.ResolveFolder(folder)?.TrimEnd('\\'), destinationFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = "The file is already in that folder." });

            // Same name already there: suggest the first free "name (n).ext" and only use it if asked to
            string newName = file;
            string baseName = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            for (int n = 2; System.IO.File.Exists(Path.Combine(destinationFolder, newName)); n++)
                newName = $"{baseName} ({n}){ext}";

            if (newName != file && !keepBoth)
                return Json(new
                {
                    success = false,
                    conflict = true,
                    suggestedName = newName,
                    message = "\"" + file + "\" already exists in \"" + MediaPaths.NormalizeFolder(newFolder) + "\"."
                });

            if (!Directory.Exists(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            System.IO.File.Move(oldPath, Path.Combine(destinationFolder, newName));
            Favorites.Move(folder, file, newFolder, newName);
            Flags.Move(folder, file, newFolder, newName);
            Thumbnails.Forget(folder, file);

            InvalidateFolderCache(folder);
            InvalidateFolderCache(newFolder);

            return Json(new { success = true, newName });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult ClearMediaCache(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return Json(new { success = false });

            InvalidateFolderCache(folder);
            return Json(new { success = true });
        }

        // ---------- Folder delete ----------

        /// <summary>
        /// Physical path of a folder that may be deleted, or null with a reason. Never the media root,
        /// the Recycle Bin, or anything inside the Recycle Bin.
        /// </summary>
        private static string ResolveDeletableFolder(string folder, out string error)
        {
            error = null;
            folder = MediaPaths.NormalizeFolder(folder);
            string path = folder.Length == 0 ? null : MediaPaths.ResolveFolder(folder);

            if (path == null || path.TrimEnd('\\').Length + 1 <= MediaPaths.Root.Length)
                error = "That folder can't be deleted.";
            // Check the resolved path (Windows drops trailing dots/spaces, so ".recycle." would mean ".recycle")
            else if (MediaPaths.ToRelative(path).Split('/').Any(part => string.Equals(part, RecycleBin.FolderName, StringComparison.OrdinalIgnoreCase)))
                error = "The Recycle Bin can't be deleted from here.";
            else if (!Directory.Exists(path))
                error = "Folder not found.";

            return error == null ? path : null;
        }

        /// <summary>What's in a folder (everything below it too), for the delete confirmation.</summary>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult FolderInfo(string folder)
        {
            string path = ResolveDeletableFolder(folder, out string error);
            if (path == null)
                return Json(new { success = false, message = error }, JsonRequestBehavior.AllowGet);

            var files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            int subfolders = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Count();
            long bytes = files.Sum(f => f.Length);

            return Json(new
            {
                success = true,
                name = Path.GetFileName(path.TrimEnd('\\')),
                fileCount = files.Count,
                subfolderCount = subfolders,
                size = FileViewModel.FormatFileSize(bytes),
                isEmpty = files.Count == 0
            }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Deletes a folder. An empty one (no files anywhere inside) is removed outright; anything else is moved,
        /// whole, into the Recycle Bin. A folder with files needs <paramref name="confirmName"/> = its name.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult DeleteFolder(string folder, string confirmName)
        {
            string path = ResolveDeletableFolder(folder, out string error);
            if (path == null)
                return Json(new { success = false, message = error });

            folder = MediaPaths.NormalizeFolder(folder);
            string name = Path.GetFileName(path.TrimEnd('\\'));
            int slash = folder.LastIndexOf('/');
            string parent = slash < 0 ? "" : folder.Substring(0, slash);

            try
            {
                var files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                string message;
                if (files.Count == 0)
                {
                    Directory.Delete(path, true); // only empty folders inside
                    message = "Deleted the empty folder \"" + name + "\".";
                }
                else
                {
                    if (!string.Equals((confirmName ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                        return Json(new { success = false, message = "The name you typed doesn't match the folder name, so nothing was deleted." });

                    RecycleBin.RecycleFolder(path, files.Count, files.Sum(f => f.Length));
                    message = "Moved \"" + name + "\" (" + files.Count + " file" + (files.Count == 1 ? "" : "s") + ") to the Recycle Bin.";
                }

                InvalidateAllMediaCaches();
                return Json(new { success = true, message, parent });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                // e.g. a file inside is open in another program: the move is all-or-nothing, so nothing changed
                return Json(new { success = false, message = "Couldn't delete the folder (nothing was changed): " + ex.Message });
            }
        }

        /// <summary>Forget every cached folder listing (after a whole folder tree changes).</summary>
        private static void InvalidateAllMediaCaches()
        {
            var cache = MemoryCache.Default;
            foreach (var key in cache.Select(kv => kv.Key).Where(k => k.StartsWith("MediaCache_", StringComparison.Ordinal)).ToList())
                cache.Remove(key);
        }

        private static string BuildMediaCacheKey(string folder)
        {
            return "MediaCache_" + MediaPaths.NormalizeFolder(folder).ToLowerInvariant();
        }

        /// <summary>Cache key suffix for a folder's list that includes all its subfolders.</summary>
        private const string AllSubfoldersSuffix = "|all";

        /// <summary>
        /// A folder's contents changed: forget its file list, and the "with subfolders" list of it and of every
        /// folder above it (a change in Animals/Cats/Kittens shows up in Animals/Cats and Animals too).
        /// </summary>
        private static void InvalidateFolderCache(string folder)
        {
            var cache = MemoryCache.Default;
            string normalized = MediaPaths.NormalizeFolder(folder);
            cache.Remove(BuildMediaCacheKey(normalized));

            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i <= parts.Length; i++)
                cache.Remove(BuildMediaCacheKey(string.Join("/", parts.Take(i))) + AllSubfoldersSuffix);

            cache.Remove(AllFilesCacheKey);
        }

        // ---------- Storage Random ----------

        /// <summary>
        /// With no file: picks a random file (from everything, or only under <paramref name="within"/>) and
        /// redirects to its own URL, so Back / refresh / Rename keep working on a specific file.
        /// With folder + file: shows that file with the usual media tools.
        /// </summary>
        public ActionResult StorageRandom(string folder, string file, string within)
        {
            if (!SiteSettings.StorageRandomEnabled)
                return HttpNotFound();

            within = MediaPaths.NormalizeFolder(within);
            object withinRoute = within.Length > 0 ? new { within } : null;

            // Pictures, videos and audio only: random documents aren't much use here
            var allFiles = GetAllFilesCached().Where(f => MediaPaths.KindOf(f) != MediaKind.Document).ToList();
            var pool = FilesWithin(allFiles, within);

            if (string.IsNullOrEmpty(file))
            {
                if (pool == null)
                    return RedirectToAction("StorageRandom"); // bad "within" folder: fall back to everything

                if (!pool.Any())
                {
                    ViewBag.Within = within;
                    return View("StorageRandom", null);
                }

                string picked = pool[Constants.RNG.Next(pool.Count)];
                if (!System.IO.File.Exists(picked))
                {
                    // List is stale (file was moved/deleted outside the app): rebuild and pick again.
                    MemoryCache.Default.Remove(AllFilesCacheKey);
                    return RedirectToAction("StorageRandom", withinRoute);
                }

                string relative = MediaPaths.ToRelative(picked);
                int slash = relative.LastIndexOf('/');
                return RedirectToAction("StorageRandom", new
                {
                    folder = slash < 0 ? null : relative.Substring(0, slash),
                    file = Path.GetFileName(picked),
                    within = within.Length > 0 ? within : null
                });
            }

            folder = MediaPaths.NormalizeFolder(folder);
            string fullPath = MediaPaths.ResolveFile(folder, file);
            if (fullPath == null || !MediaPaths.IsSupportedMedia(fullPath) || !System.IO.File.Exists(fullPath))
            {
                TempData["Message"] = "That file isn't there any more, so here's another one.";
                return RedirectToAction("StorageRandom", withinRoute);
            }

            var info = new FileInfo(fullPath);
            var model = new FileViewModel
            {
                RelativePath = MediaPaths.ToRelative(fullPath),
                FullPath = info.FullName,
                FileName = info.Name,
                FileSize = info.Length,
                LastModified = info.LastWriteTime,
                CreatedDate = info.CreationTime,
                FileType = MimeMapping.GetMimeMapping(info.Name),
                FileExtension = info.Extension,
                LastAccessed = info.LastAccessTime,
                FilesInDirectory = Directory.GetFiles(info.DirectoryName).Count(MediaPaths.IsSupportedMedia),
                TotalFiles = allFiles.Count
            };

            ViewBag.Folder = folder;
            ViewBag.File = info.Name;
            ViewBag.Url = MediaPaths.UrlFor(model.RelativePath);
            ViewBag.Within = within;
            ViewBag.PoolCount = pool?.Count ?? allFiles.Count;
            ViewBag.IsFavorite = folder.Length > 0 && Favorites.IsFavorite(folder, info.Name);
            ViewBag.IsCorrupted = Flags.Get(folder, info.Name).Corrupted;

            return View("StorageRandom", model);
        }

        private static List<string> GetAllFilesCached()
        {
            var cache = MemoryCache.Default;
            var allFiles = cache.Get(AllFilesCacheKey) as List<string>;
            if (allFiles == null)
            {
                allFiles = MediaPaths.GetAllMediaFiles();
                cache.Set(AllFilesCacheKey, allFiles, DateTimeOffset.Now.AddMinutes(10));
            }
            return allFiles;
        }

        /// <summary>Files under <paramref name="within"/> (all files if empty), or null if the folder is invalid.</summary>
        private static List<string> FilesWithin(List<string> allFiles, string within)
        {
            if (string.IsNullOrEmpty(within))
                return allFiles;

            string withinPath = MediaPaths.ResolveFolder(within);
            if (withinPath == null)
                return null;

            string prefix = withinPath.TrimEnd('\\') + "\\";
            return allFiles.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // ---------- Deletion / corruption flags ----------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult MarkForDeletion(string folder, string fileName, string within)
        {
            if (!SiteSettings.StorageRandomEnabled) // only used by the Storage Random page
                return HttpNotFound();
            if (MediaPaths.ResolveFile(folder, fileName) == null)
                return new HttpStatusCodeResult(400, "Invalid file details.");

            var current = Flags.Get(folder, fileName);
            Flags.Set(folder, fileName, current.Corrupted, true);

            TempData["Message"] = "\"" + fileName + "\" marked for deletion.";
            within = MediaPaths.NormalizeFolder(within);
            return RedirectToAction("StorageRandom", within.Length > 0 ? new { within } : null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult MarkAsCorrupted(string folder, string fileName)
        {
            if (MediaPaths.ResolveFile(folder, fileName) == null)
                return Json(new { success = false, message = "Invalid file details." });

            var current = Flags.Get(folder, fileName);
            Flags.Set(folder, fileName, true, current.ToBeDeleted);
            return Json(new { success = true, message = "File marked as corrupted." });
        }

        /// <summary>
        /// Clears a file's Corrupted / Marked-for-deletion flags (e.g. it was flagged by mistake).
        /// <paramref name="nameOnly"/>: clear an old flag saved by name only, whose file isn't anywhere now.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult UnmarkFile(string folder, string fileName, bool nameOnly = false)
        {
            if (!MediaPaths.IsPlainFileName(fileName) || (!nameOnly && MediaPaths.ResolveFile(folder, fileName) == null))
                return Json(new { success = false, message = "Invalid file details." });

            if (nameOnly)
                Flags.ClearNameOnly(fileName);
            else
                Flags.Set(folder, fileName, false, false);

            return Json(new { success = true, message = "Flag cleared." });
        }

        /// <summary>Every file currently flagged Corrupted or Marked for Deletion, with where it is now.</summary>
        public ActionResult Flagged()
        {
            return View(Flags.GetFlagged(GetAllFilesCached()));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> FixCorruptedFile(string fileName, string folder)
        {
            string serverPath = MediaPaths.ResolveFile(folder, fileName);
            if (string.IsNullOrWhiteSpace(folder) || serverPath == null)
                return Json(new { success = false, message = "Invalid file details." });

            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (!Constants.VideoExtensions.Contains(ext))
                return Json(new { success = false, message = "Fix File only supports video files." });

            // Gate: only allow fixing files actually flagged corrupted
            var flags = Flags.Get(folder, fileName);
            if (!flags.Corrupted)
                return Json(new { success = false, message = "File is not marked as corrupted." });

            if (!System.IO.File.Exists(serverPath))
                return Json(new { success = false, message = "File not found on disk." });

            string directory = Path.GetDirectoryName(serverPath);
            string nameNoExt = Path.GetFileNameWithoutExtension(serverPath);
            string tempOutput = Path.Combine(directory, nameNoExt + "_fixed.mp4");
            string backupPath = serverPath + ".bak";

            string args = $"-y -i \"{serverPath}\" -c:v libx264 -profile:v high -level 4.0 " +
                          $"-pix_fmt yuv420p -c:a aac -movflags +faststart \"{tempOutput}\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SiteSettings.FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                string stderrOutput;

                using (var process = Process.Start(psi))
                {
                    stderrOutput = await process.StandardError.ReadToEndAsync();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        if (System.IO.File.Exists(tempOutput)) System.IO.File.Delete(tempOutput);
                        return Json(new { success = false, message = "FFmpeg failed: " + stderrOutput });
                    }
                }

                if (!System.IO.File.Exists(tempOutput))
                    return Json(new { success = false, message = "FFmpeg reported success but output file is missing." });

                // Backup original, promote fixed version, clean up backup
                System.IO.File.Move(serverPath, backupPath);

                try
                {
                    System.IO.File.Move(tempOutput, serverPath);
                }
                catch
                {
                    if (System.IO.File.Exists(backupPath)) System.IO.File.Move(backupPath, serverPath);
                    throw;
                }

                System.IO.File.Delete(backupPath);

                // Clear the corrupted flag (a Marked-for-deletion flag stays), and remake the thumbnail
                Flags.Set(folder, fileName, false, flags.ToBeDeleted);
                Thumbnails.Forget(folder, fileName);

                return Json(new { success = true, message = "File fixed and re-verified." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
