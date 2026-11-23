using MediaManager.Filters;
using MediaManager.Models;
using MediaManager.Models.Constants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

        /// <param name="f">A hidden-name link's token, in place of folder (Models/HiddenNames.cs).</param>
        public ActionResult Gallery(string folder, string f = null)
        {
            if (f != null && (folder = HiddenNames.Decode(f)) == null)
                return HttpNotFound();

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

            // A readable link (old bookmark, typed in) on a site that hides names: go to the hidden one instead
            if (f == null && HiddenNames.Enabled)
                return Redirect(Url.GalleryLink(folder));

            ViewBag.Folder = folder;
            ViewBag.Subfolders = GetSubfolders(MediaPaths.ResolveFolder(folder));
            // "Share with guests" button: only on sites that have a guest login
            ViewBag.GuestLogin = SiteSettings.GuestPasswordHash != null;
            ViewBag.SharedDirectly = SharedFolders.IsSharedDirectly(folder);
            ViewBag.Shared = SharedFolders.IsShared(folder);
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
        /// <param name="tag">Only files with this tag (the Gallery's tag filter).</param>
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public JsonResult GetMedia(string folder, string after = null, int pageSize = 10, string search = "", string sort = "random",
            int seed = 0, bool subfolders = false, int? tag = null)
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

            // Apply the tag filter: tagged files are stored as folder + name, so match on the path from the media root
            if (tag != null)
            {
                var tagged = new HashSet<string>(
                    Tags.FilesWith(new[] { tag.Value }, matchAll: false).Select(t => (t.Folder.Length == 0 ? "" : t.Folder + "/") + t.FileName),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(f => tagged.Contains(folder + "/" + f)).ToList();
            }

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
            var tagsByFolder = byFolder.ToDictionary(g => g.Key, g => Tags.NamesIn(g.Key, g.Select(i => i.Name).ToList()),
                StringComparer.OrdinalIgnoreCase);

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
                    Tags = tagsByFolder[i.Folder].TryGetValue(i.Name, out var names) ? names : new List<string>(),
                    Size = info?.Length ?? 0,                                // for the "N selected · size" bar
                    Thumb = info == null ? null : Thumbnails.UrlFor(Url, i.Folder, i.Name, info), // null: show the file itself
                    Url = MediaPaths.UrlFor(i.Folder + "/" + i.Name),       // the file itself
                    FolderLink = Url.GalleryLink(i.Folder)                  // its folder's Gallery (for "in Kittens")
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

        /// <summary>
        /// The links for a folder, or a file in it, for page scripts that only have the names (after a Rename, say)
        /// on a site that hides names (Models/HiddenNames.cs): the scripts can't make the tokens themselves.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Links(string folder, string file = null, string within = null)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            if (MediaPaths.ResolveFolder(folder) == null || (file != null && !MediaPaths.IsPlainFileName(file)))
                return Json(new { success = false, message = "Invalid folder or file." });

            return Json(new
            {
                success = true,
                gallery = Url.GalleryLink(folder),
                view = file == null ? null : Url.ViewLink(folder, file),
                url = file == null ? null : MediaPaths.UrlFor(folder + "/" + file),
                random = file == null || !SiteSettings.StorageRandomEnabled ? null : Url.RandomLink(folder, file, within)
            });
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
        public async Task<JsonResult> AddMediaChunk(string uploadId, long offset, long totalSize, string fileName, string newFileName, string folder, HttpPostedFileBase chunk)
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

            string source = await MakeSeekableAsync(r.PartPath, safeFolder, targetName);

            try
            {
                return PlaceAddMedia(safeFolder, folderPath, targetName, target => System.IO.File.Move(source, target));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ChunkedUpload.Discard(uploadId);
                ActivityLog.Error(ActivityLog.Upload, "Couldn't save the upload: " + ex.Message, safeFolder, targetName);
                return Json(new { success = false, message = "Couldn't save the file: " + ex.Message });
            }
        }

        /// <summary>Add Media: an upload was cancelled part way. Throws away the pieces received so far.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult CancelUpload(string uploadId)
        {
            ChunkedUpload.Discard(uploadId); // ignores anything that isn't an upload id
            return Json(new { success = true });
        }

        /// <summary>
        /// A finished upload that's a fragmented MP4 or a WebM (see Models/VideoIndex.cs) is rewritten so desktop
        /// players can seek in it. Returns the file to put in place: the rewritten one, or the upload as it came
        /// if it didn't need it or the rewrite didn't work (no ffmpeg, a damaged file).
        /// </summary>
        private static async Task<string> MakeSeekableAsync(string partPath, string folder, string name)
        {
            string ext = Path.GetExtension(name);
            if (!VideoIndex.NeedsFix(partPath, ext))
                return partPath;

            string fixedPath = partPath + ".fixed";
            string error = await VideoIndex.RewriteAsync(partPath, fixedPath, ext);
            if (error != null)
            {
                ActivityLog.Error(ActivityLog.Upload, "Kept as uploaded: couldn't make it seekable: " + error, folder, name);
                return partPath;
            }
            ActivityLog.Write(ActivityLog.Fix, "Rewrote the upload so it seeks in every player.", folder, name);

            try
            {
                System.IO.File.Delete(partPath);
            }
            catch (IOException)
            {
                // ".part" files are cleaned up after a day anyway
            }
            return fixedPath;
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

            // Unless the name typed ends in a file type the site knows, add the uploaded file's one
            // ("Trip v1.2" -> "Trip v1.2.mp4", not a file of type ".2"). Windows drops trailing dots anyway.
            targetName = newFileName.Trim().TrimEnd('.');
            if (!Constants.SupportedExtensions.Contains(Path.GetExtension(targetName).ToLowerInvariant()))
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

            string placed = Path.Combine(folderPath, name);
            place(placed);
            InvalidateFolderCache(safeFolder);
            ActivityLog.Write(ActivityLog.Upload, "Uploaded (" + ActivityLog.FormatBytes(new FileInfo(placed).Length) + ")"
                + (name == requestedName ? "." : ", saved under a new name because \"" + requestedName + "\" already existed."), safeFolder, name);

            string message = name == requestedName
                ? "\"" + name + "\" uploaded to " + safeFolder + "."
                : "Saved as \"" + name + "\" in " + safeFolder + " (\"" + requestedName + "\" already existed).";
            return Json(new { success = true, complete = true, fileName = name, renamed = name != requestedName, message });
        }

        public ActionResult UploadFailed()
        {
            return View("~/Views/Shared/UploadFailed.cshtml");
        }

        /// <param name="f">A hidden-name link's token, in place of folder + file (Models/HiddenNames.cs).</param>
        public ActionResult ViewMedia(string folder, string file, string f = null)
        {
            if (f != null && !HiddenNames.TryDecodeFile(f, out folder, out file))
                return HttpNotFound();

            // Files at the very top of the library ('' folder) open too, e.g. from Duplicates or Recently Added;
            // the view hides the tools that need a folder.
            string fullPath = MediaPaths.ResolveFile(folder, file);
            if (fullPath == null || !System.IO.File.Exists(fullPath))
                return HttpNotFound();

            if (f == null && HiddenNames.Enabled)
                return Redirect(Url.ViewLink(folder, file));

            // File details for the info panel (dimensions/duration are filled in by the browser)
            var info = new FileInfo(fullPath);

            folder = MediaPaths.NormalizeFolder(folder);
            ViewBag.Folder = folder;
            ViewBag.File = file;
            ViewBag.Url = MediaPaths.VersionedUrl(MediaPaths.ToRelative(fullPath), info);

            // Pictures: what the file really is (apps save other formats under ".jpg" names), and a JPEG the server
            // makes to show instead if the browser can't show the file itself
            if (MediaPaths.KindOf(file) == MediaKind.Image)
            {
                var format = FileFormat.Detect(fullPath);
                ViewBag.RealFormat = FileFormat.NameMismatch(format, file) ? format : null;
                ViewBag.Fallback = Thumbnails.UrlFor(Url, folder, file, info, evenIfSmall: true);
            }
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

            MediaViews.Record(HttpContext, folder, info.Name);
            ViewBag.Views = MediaViews.CountFor(folder, info.Name);

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
            Tags.Forget(folder, file);
            MediaViews.Forget(folder, file);
            Flags.Forget(folder, file);
            Thumbnails.Forget(folder, file);
            PlayableCopy.Forget(folder, file);
            InvalidateFolderCache(folder);
            ActivityLog.Write(ActivityLog.Delete, "Moved to the Recycle Bin.", folder, file);

            return Json(new { success = true });
        }

        /// <summary>
        /// Duplicates page: moves an extra copy to the Recycle Bin, like DeleteMedia, but only if the copy being kept
        /// is still there and both still match (same size, same start and end), in case either changed since the scan.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult DeleteDuplicate(string folder, string file, string keepFolder, string keepFile)
        {
            string fullPath = MediaPaths.ResolveFile(folder, file);
            string keepPath = MediaPaths.ResolveFile(keepFolder, keepFile);
            if (fullPath == null || keepPath == null)
                return Json(new { success = false, message = "Invalid parameters." });
            if (string.IsNullOrWhiteSpace(folder))
                return Json(new { success = false, message = "Files at the very top of the library can't be deleted here. Keep this one instead." });
            if (string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = "That's the copy being kept." });
            if (!System.IO.File.Exists(fullPath))
                return Json(new { success = false, message = "File not found (already deleted?)." });
            if (!System.IO.File.Exists(keepPath))
                return Json(new { success = false, message = "The copy being kept isn't there any more, so this one wasn't deleted." });
            if (!DuplicateFinder.StillMatch(fullPath, keepPath))
                return Json(new { success = false, message = "The two files don't match any more, so nothing was deleted. Scan again." });

            RecycleBin.Recycle(fullPath, RecycleBin.SourceGallery); // restorable from the Recycle Bin page
            Favorites.Set(folder, file, false);
            Tags.Forget(folder, file);
            MediaViews.Forget(folder, file);
            Flags.Forget(folder, file);
            Thumbnails.Forget(folder, file);
            PlayableCopy.Forget(folder, file);
            InvalidateFolderCache(folder);
            ActivityLog.Write(ActivityLog.Duplicates, "Moved to the Recycle Bin as a copy of "
                + MediaPaths.NormalizeFolder(keepFolder) + "/" + keepFile + ".", folder, file);

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
            Tags.Rename(folder, oldName, newName);
            MediaViews.Rename(folder, oldName, newName);
            Flags.Move(folder, oldName, folder, newName);
            Thumbnails.Forget(folder, oldName);
            PlayableCopy.Forget(folder, oldName);
            InvalidateFolderCache(folder);
            ActivityLog.Write(ActivityLog.Rename, "Renamed from \"" + oldName + "\".", folder, newName);

            return Json(new { success = true, newName });
        }

        /// <summary>
        /// Copies a file into a folder, which may be its own (the copy then gets a free name). Favorites, flags and
        /// tags stay with the original. Never overwrites: a name clash works like MoveMedia's (conflict / keepBoth).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult CopyMedia(string folder, string file, string newFolder, bool keepBoth = false)
        {
            string sourcePath = MediaPaths.ResolveFile(folder, file);
            string destinationFolder = MediaPaths.ResolveFolder(newFolder);

            if (sourcePath == null || string.IsNullOrWhiteSpace(newFolder))
                return Json(new { success = false, message = "Invalid parameters." });
            if (destinationFolder == null)
                return Json(new { success = false, message = "Destination folder is invalid." });
            if (!System.IO.File.Exists(sourcePath))
                return Json(new { success = false, message = "File not found." });

            string newName = FreeName(destinationFolder, file);
            if (newName != file && !keepBoth)
                return Json(new
                {
                    success = false,
                    conflict = true,
                    suggestedName = newName,
                    message = "\"" + file + "\" already exists in \"" + MediaPaths.NormalizeFolder(newFolder) + "\"."
                });

            try
            {
                Directory.CreateDirectory(destinationFolder);
                System.IO.File.Copy(sourcePath, Path.Combine(destinationFolder, newName), overwrite: false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // e.g. the drive is full; a half-written copy is removed by Windows when File.Copy fails
                ActivityLog.Error(ActivityLog.Copy, "Couldn't copy to " + MediaPaths.NormalizeFolder(newFolder) + ": " + ex.Message, folder, file);
                return Json(new { success = false, message = "Couldn't copy the file: " + ex.Message });
            }

            InvalidateFolderCache(newFolder);
            ActivityLog.Write(ActivityLog.Copy, "Copied from " + (MediaPaths.NormalizeFolder(folder) is string from && from.Length > 0 ? from : "(top folder)")
                + "/" + file + ".", newFolder, newName);
            return Json(new { success = true, newName });
        }

        /// <summary>The first free "name (n).ext" in a folder (the name itself if nothing's called that).</summary>
        private static string FreeName(string folderPath, string file)
        {
            string name = file;
            string baseName = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            for (int n = 2; System.IO.File.Exists(Path.Combine(folderPath, name)); n++)
                name = $"{baseName} ({n}){ext}";
            return name;
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
            Tags.Move(folder, file, newFolder, newName);
            MediaViews.Move(folder, file, newFolder, newName);
            Flags.Move(folder, file, newFolder, newName);
            Thumbnails.Forget(folder, file);
            PlayableCopy.Forget(folder, file);

            InvalidateFolderCache(folder);
            InvalidateFolderCache(newFolder);
            ActivityLog.Write(ActivityLog.Move, "Moved here from " + (MediaPaths.NormalizeFolder(folder) is string from && from.Length > 0 ? from : "(top folder)")
                + (newName != file ? " as \"" + newName + "\" (\"" + file + "\" was taken)." : "."), newFolder, newName);

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
                ActivityLog.Write(ActivityLog.Delete, message + " (folder " + folder + ")");
                return Json(new { success = true, message, parent });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                // e.g. a file inside is open in another program: the move is all-or-nothing, so nothing changed
                ActivityLog.Error(ActivityLog.Delete, "Couldn't delete the folder " + folder + ": " + ex.Message);
                return Json(new { success = false, message = "Couldn't delete the folder (nothing was changed): " + ex.Message });
            }
        }

        /// <summary>
        /// Empty Folders page: deletes folders that are still empty (nothing below them but other empty folders and
        /// clutter like Thumbs.db; see Models/EmptyFolders.cs). Checked again here, so a folder that got a file since
        /// the scan is left alone. Nothing of value is in them, so they're deleted outright, not recycled.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        /// <param name="folders">One folder per line (relative to the media root).</param>
        public JsonResult DeleteEmptyFolders(string folders)
        {
            var deleted = new List<string>();
            var skipped = new List<string>();
            foreach (string folder in (folders ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string path = ResolveDeletableFolder(folder, out string error);
                if (path == null)
                {
                    skipped.Add(folder + ": " + error);
                    continue;
                }
                if (!EmptyFolders.IsEmpty(path))
                {
                    skipped.Add(folder + ": it isn't empty any more");
                    continue;
                }
                try
                {
                    Directory.Delete(path, true); // only empty folders and clutter files inside
                    deleted.Add(MediaPaths.NormalizeFolder(folder));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    skipped.Add(folder + ": " + ex.Message);
                }
            }

            if (deleted.Count > 0)
            {
                InvalidateAllMediaCaches();
                ActivityLog.Write(ActivityLog.Delete, "Deleted " + deleted.Count + " empty folder" + (deleted.Count == 1 ? "" : "s") + ": "
                    + string.Join(", ", deleted.Take(20)) + (deleted.Count > 20 ? ", ..." : "") + ".");
            }
            if (skipped.Count > 0)
                ActivityLog.Error(ActivityLog.Delete, "Left " + skipped.Count + " folder" + (skipped.Count == 1 ? "" : "s") + " alone: "
                    + string.Join("; ", skipped.Take(10)) + (skipped.Count > 10 ? "; ..." : "") + ".");

            return Json(new { success = true, deleted, skipped });
        }

        /// <summary>Forget every cached folder listing (after a whole folder tree changes).</summary>
        private static void InvalidateAllMediaCaches()
        {
            LibraryIndex.Forget(); // Storage / Recently Added
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
            LibraryIndex.Forget(); // Storage / Recently Added
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
        /// <param name="f">A hidden-name link's token, in place of folder + file (Models/HiddenNames.cs).</param>
        /// <param name="w">A hidden-name link's token, in place of within.</param>
        public ActionResult StorageRandom(string folder, string file, string within, string f = null, string w = null)
        {
            if (!SiteSettings.StorageRandomEnabled)
                return HttpNotFound();

            if ((w != null && (within = HiddenNames.Decode(w)) == null)
                || (f != null && !HiddenNames.TryDecodeFile(f, out folder, out file)))
                return HttpNotFound();

            within = MediaPaths.NormalizeFolder(within);
            string sameScope = Url.RandomLink(null, null, within);

            // Pictures, videos and audio only: random documents aren't much use here
            var allFiles = GetAllFilesCached().Where(p => MediaPaths.KindOf(p) != MediaKind.Document).ToList();
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
                    return Redirect(sameScope);
                }

                string relative = MediaPaths.ToRelative(picked);
                int slash = relative.LastIndexOf('/');
                return Redirect(Url.RandomLink(slash < 0 ? "" : relative.Substring(0, slash), Path.GetFileName(picked), within));
            }

            folder = MediaPaths.NormalizeFolder(folder);
            string fullPath = MediaPaths.ResolveFile(folder, file);
            if (fullPath == null || !MediaPaths.IsSupportedMedia(fullPath) || !System.IO.File.Exists(fullPath))
            {
                TempData["Message"] = "That file isn't there any more, so here's another one.";
                return Redirect(sameScope);
            }

            if (f == null && HiddenNames.Enabled)
                return Redirect(Url.RandomLink(folder, file, within));

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
            ViewBag.Url = MediaPaths.VersionedUrl(model.RelativePath, info);
            ViewBag.Within = within;
            ViewBag.PoolCount = pool?.Count ?? allFiles.Count;
            ViewBag.IsFavorite = folder.Length > 0 && Favorites.IsFavorite(folder, info.Name);
            MediaViews.Record(HttpContext, folder, info.Name);
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
            return Redirect(Url.RandomLink(null, null, within));
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
                        ActivityLog.Error(ActivityLog.Fix, "Fix File failed: ffmpeg: " + stderrOutput, folder, fileName);
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
                PlayableCopy.Forget(folder, fileName);

                ActivityLog.Write(ActivityLog.Fix, "Fix File converted it and cleared the Corrupted flag.", folder, fileName);
                return Json(new { success = true, message = "File fixed and re-verified." });
            }
            catch (Exception ex)
            {
                ActivityLog.Error(ActivityLog.Fix, "Fix File failed: " + ex.Message, folder, fileName);
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Rewrites a video so desktop players can seek in it (Models/VideoIndex.cs): no re-encoding, same name and
        /// dates. onlyIfNeeded (Gallery, several files): leaves files alone that are already fine, answering fixed = false.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> FixSeeking(string folder, string fileName, bool onlyIfNeeded = false)
        {
            string serverPath = MediaPaths.ResolveFile(folder, fileName);
            if (serverPath == null || !System.IO.File.Exists(serverPath))
                return Json(new { success = false, message = "File not found." });

            string ext = Path.GetExtension(serverPath);
            if (!VideoIndex.CanFix(ext))
                return Json(new { success = false, message = "Only MP4, MOV and WebM videos can be fixed this way." });

            if (onlyIfNeeded && !VideoIndex.NeedsFix(serverPath, ext))
                return Json(new { success = true, @fixed = false, message = "Already seekable." });

            string name = Path.GetFileName(serverPath);
            string error = await VideoIndex.FixInPlaceAsync(serverPath);
            if (error != null)
            {
                ActivityLog.Error(ActivityLog.Fix, "Fix Seeking didn't change it: " + error, folder, name);
                return Json(new { success = false, message = error });
            }

            ActivityLog.Write(ActivityLog.Fix, "Fix Seeking rewrote it so it seeks in every player.", folder, name);
            Thumbnails.Forget(folder, name);
            PlayableCopy.Forget(folder, name);
            InvalidateFolderCache(folder);
            return Json(new { success = true, @fixed = true, message = "Fixed: you can skip through it in any player now." });
        }

        /// <summary>
        /// View page's Save Rotated Copy: writes "name (rotated).ext" next to the file, turned <paramref name="degrees"/>
        /// clockwise from how the browser shows it (Models/RotatedCopy.cs). The original isn't changed.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> SaveRotatedCopy(string folder, string fileName, int degrees)
        {
            string sourcePath = MediaPaths.ResolveFile(folder, fileName);
            if (sourcePath == null || !System.IO.File.Exists(sourcePath))
                return Json(new { success = false, message = "File not found." });

            int? clockwise = RotatedCopy.Normalize(degrees);
            if (clockwise == null)
                return Json(new { success = false, message = "It can only be turned in quarter turns." });
            if (clockwise == 0)
                return Json(new { success = false, message = "It isn't rotated, so there's nothing to save." });

            string name = Path.GetFileName(sourcePath);
            string ext = RotatedCopy.CopyExtension(name);
            if (ext == null)
                return Json(new { success = false, message = "Only pictures and videos can be rotated." });

            string directory = Path.GetDirectoryName(sourcePath);
            string newName = FreeName(directory, Path.GetFileNameWithoutExtension(name) + " (rotated)" + ext);
            string error = await RotatedCopy.MakeAsync(sourcePath, Path.Combine(directory, newName), clockwise.Value);
            if (error != null)
            {
                ActivityLog.Error(ActivityLog.Copy, "Couldn't save a rotated copy: " + error, folder, name);
                return Json(new { success = false, message = error });
            }

            InvalidateFolderCache(folder);
            ActivityLog.Write(ActivityLog.Copy, "Rotated copy (" + clockwise + "° clockwise) of " + name + ".", folder, newName);
            return Json(new { success = true, newName });
        }

        /// <summary>
        /// View page's Save Snapshot: saves what's showing as a full-size JPEG next to the file, turned
        /// <paramref name="degrees"/> clockwise like it's shown. Videos: the frame at <paramref name="seconds"/>, as
        /// "name (frame m-ss.fff).jpg" (Models/VideoSnapshot.cs). Pictures: the picture, as "name (snapshot).jpg"
        /// (Models/RotatedCopy.cs; an animated GIF's first frame).
        /// seconds is a string so "12.5" reads the same whatever the server's language settings.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> SaveSnapshot(string folder, string fileName, string seconds = null, int degrees = 0)
        {
            string videoPath = MediaPaths.ResolveFile(folder, fileName);
            if (videoPath == null || !System.IO.File.Exists(videoPath))
                return Json(new { success = false, message = "File not found." });

            int? clockwise = RotatedCopy.Normalize(degrees);
            if (clockwise == null)
                return Json(new { success = false, message = "It can only be turned in quarter turns." });

            string kind = MediaPaths.KindOf(fileName);
            if (kind == MediaKind.Image)
                return await SavePictureSnapshot(folder, videoPath, clockwise.Value);
            if (kind != MediaKind.Video)
                return Json(new { success = false, message = "Snapshots are for pictures and videos." });
            if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double at) || at < 0 || double.IsInfinity(at))
                return Json(new { success = false, message = "Invalid time." });

            string name = Path.GetFileName(videoPath);
            string directory = Path.GetDirectoryName(videoPath);
            string newName = FreeName(directory, VideoSnapshot.NameFor(name, at));
            string error = await VideoSnapshot.MakeAsync(videoPath, Path.Combine(directory, newName), at, clockwise.Value);
            if (error != null)
            {
                ActivityLog.Error(ActivityLog.Copy, "Couldn't save a snapshot: " + error, folder, name);
                return Json(new { success = false, message = error });
            }

            InvalidateFolderCache(folder);
            ActivityLog.Write(ActivityLog.Copy, "Snapshot of " + name + " at " + at.ToString("0.000", CultureInfo.InvariantCulture) + "s.", folder, newName);
            return Json(new { success = true, newName });
        }

        private async Task<JsonResult> SavePictureSnapshot(string folder, string picturePath, int clockwise)
        {
            string name = Path.GetFileName(picturePath);
            string directory = Path.GetDirectoryName(picturePath);
            string newName = FreeName(directory, Path.GetFileNameWithoutExtension(name) + " (snapshot).jpg");
            string error = await RotatedCopy.MakeAsync(picturePath, Path.Combine(directory, newName), clockwise);
            if (error != null)
            {
                ActivityLog.Error(ActivityLog.Copy, "Couldn't save a snapshot: " + error, folder, name);
                return Json(new { success = false, message = error });
            }

            InvalidateFolderCache(folder);
            ActivityLog.Write(ActivityLog.Copy, "Snapshot of " + name + (clockwise == 0 ? "" : ", turned " + clockwise + "° clockwise") + ".", folder, newName);
            return Json(new { success = true, newName });
        }
    }
}
