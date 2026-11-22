using MediaManager.Filters;
using MediaManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Mvc;

namespace MediaManager.Controllers
{
    /// <summary>Storage page: what takes the space, folder by folder (Models/LibraryIndex.cs). Owner only.</summary>
    [AuthFilter]
    public class StorageController : Controller
    {
        private const int BiggestFiles = 20;

        public class FolderRow
        {
            public string Name;      // shown name ("(files here)" for the folder's own files)
            public string Folder;    // relative path to drill into, or null for loose files
            public long Bytes;
            public int Files;
        }

        public class KindRow
        {
            public string Label;
            public long Bytes;
            public int Files;
        }

        public class Overview
        {
            public string Folder;
            public long Bytes;
            public int Files;
            public List<FolderRow> Folders;
            public List<KindRow> Kinds;
            public List<LibraryIndex.Entry> Biggest;
            public List<KindRow> SiteFolders;   // top level only
            public long? DriveFree, DriveTotal; // top level only
            public string DriveName;
            public int UnreadableFolders;
            public DateTime BuiltAt;
        }

        public ActionResult Index(string folder = "", bool refresh = false)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            string path = MediaPaths.ResolveFolder(folder);
            if (path == null || !Directory.Exists(path))
                return HttpNotFound();

            var snap = LibraryIndex.Get(refresh);
            var files = snap.Files.Where(f => LibraryIndex.IsUnder(f.Folder, folder)).ToList();

            // Each direct subfolder's total, plus the files directly in this folder
            var folders = files
                .GroupBy(f => ChildOf(folder, f.Folder), StringComparer.OrdinalIgnoreCase)
                .Select(g => new FolderRow
                {
                    Name = g.Key ?? "(files directly in this folder)",
                    Folder = g.Key == null ? null : (folder.Length == 0 ? g.Key : folder + "/" + g.Key),
                    Bytes = g.Sum(f => f.Size),
                    Files = g.Count()
                })
                .OrderByDescending(r => r.Bytes)
                .ToList();

            var kinds = files
                .GroupBy(f => f.Kind ?? "other")
                .Select(g => new KindRow { Label = KindLabel(g.Key), Bytes = g.Sum(f => f.Size), Files = g.Count() })
                .OrderByDescending(k => k.Bytes)
                .ToList();

            var model = new Overview
            {
                Folder = folder,
                Bytes = files.Sum(f => f.Size),
                Files = files.Count,
                Folders = folders,
                Kinds = kinds,
                Biggest = files.OrderByDescending(f => f.Size).Take(BiggestFiles).ToList(),
                UnreadableFolders = snap.UnreadableFolders,
                BuiltAt = snap.BuiltAt
            };

            if (folder.Length == 0)
            {
                model.SiteFolders = LibraryIndex.SiteFolders
                    .Where(s => snap.SiteFolderBytes.ContainsKey(s.Key))
                    .Select(s => new KindRow { Label = s.Value + " (" + s.Key + ")", Bytes = snap.SiteFolderBytes[s.Key] })
                    .OrderByDescending(s => s.Bytes)
                    .ToList();
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(MediaPaths.Root));
                    model.DriveName = drive.Name;
                    model.DriveFree = drive.AvailableFreeSpace;
                    model.DriveTotal = drive.TotalSize;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    // e.g. a network share: no drive figures
                }
            }

            return View(model);
        }

        /// <summary>The first folder below <paramref name="folder"/> on the way to <paramref name="fileFolder"/>; null if it's the folder itself.</summary>
        private static string ChildOf(string folder, string fileFolder)
        {
            string rest = folder.Length == 0 ? fileFolder : fileFolder.Length > folder.Length ? fileFolder.Substring(folder.Length + 1) : "";
            if (rest.Length == 0)
                return null;
            int slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        private static string KindLabel(string kind)
        {
            switch (kind)
            {
                case MediaKind.Image: return "Photos & pictures";
                case MediaKind.Video: return "Videos";
                case MediaKind.Audio: return "Audio";
                case MediaKind.Document: return "Documents";
                default: return "Other files (not shown in galleries)";
            }
        }
    }
}
