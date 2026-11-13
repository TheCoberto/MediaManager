using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;

namespace MediaManager.Models
{
    public class RecycleBinItem
    {
        public int RecycleId { get; set; }
        public string OriginalFolder { get; set; }
        public string FileName { get; set; }
        public string StoredPath { get; set; }
        public long FileSize { get; set; }
        public DateTime DeletedAt { get; set; }
        public string Source { get; set; }
        public string ImageDescription { get; set; }

        /// <summary>A whole folder (FileName is the folder's name, FileSize the total of everything in it).</summary>
        public bool IsFolder { get; set; }

        /// <summary>Number of files inside a recycled folder.</summary>
        public int? FileCount { get; set; }

        public string FileSizeFormatted => FileViewModel.FormatFileSize(FileSize);
        public bool IsImage => !IsFolder && new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }
            .Contains(Path.GetExtension(FileName ?? "").ToLowerInvariant());
    }

    /// <summary>
    /// The site's own Recycle Bin. Deleting moves a file (or a whole folder) to "&lt;media root&gt;\.recycle\&lt;id&gt;\&lt;name&gt;"
    /// (hidden, and blocked from the web in Web.config) and records where it came from, so it can be restored.
    /// </summary>
    public static class RecycleBin
    {
        public const string FolderName = ".recycle";
        public const string SourceGallery = "Gallery";
        public const string SourceImages = "Images";

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);

        /// <summary>Moves a file (anywhere under the media root) into the Recycle Bin.</summary>
        public static void Recycle(string fullPath, string source, string imageDescription = null)
        {
            var root = MediaPaths.Root;
            var folderFull = Path.GetDirectoryName(fullPath).TrimEnd('\\') + "\\";
            string originalFolder = MediaPaths.ToRelative(folderFull);
            string fileName = Path.GetFileName(fullPath);

            string recycleRoot = Path.Combine(root, FolderName);
            var dirInfo = Directory.CreateDirectory(recycleRoot);
            if ((dirInfo.Attributes & FileAttributes.Hidden) == 0)
                dirInfo.Attributes |= FileAttributes.Hidden;

            string id = Guid.NewGuid().ToString("N");
            string storedDir = Path.Combine(recycleRoot, id);
            Directory.CreateDirectory(storedDir);
            string storedFull = Path.Combine(storedDir, fileName);

            long size = new FileInfo(fullPath).Length;
            File.Move(fullPath, storedFull);

            try
            {
                using (var db = Db())
                {
                    db.Execute(@"
                        INSERT INTO RecycleBin (OriginalFolder, FileName, StoredPath, FileSize, Source, ImageDescription)
                        VALUES (@OriginalFolder, @FileName, @StoredPath, @FileSize, @Source, @ImageDescription)",
                        new
                        {
                            OriginalFolder = originalFolder,
                            FileName = fileName,
                            StoredPath = FolderName + "/" + id + "/" + fileName,
                            FileSize = size,
                            Source = source,
                            ImageDescription = imageDescription
                        });
                }
            }
            catch
            {
                // Couldn't record it: put the file back so nothing goes missing.
                File.Move(storedFull, fullPath);
                Directory.Delete(storedDir);
                throw;
            }
        }

        /// <summary>
        /// Moves a whole folder (anywhere under the media root, but not the root itself) into the Recycle Bin
        /// as one item. It's a rename on the same drive, so it's instant and all-or-nothing.
        /// </summary>
        public static void RecycleFolder(string folderFullPath, int fileCount, long totalBytes)
        {
            var root = MediaPaths.Root;
            string full = Path.GetFullPath(folderFullPath).TrimEnd('\\');
            if (!(full + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase) || (full + "\\").Length <= root.Length)
                throw new InvalidOperationException("That folder can't be deleted.");

            string originalFolder = MediaPaths.ToRelative(Path.GetDirectoryName(full));
            string folderName = Path.GetFileName(full);

            string recycleRoot = Path.Combine(root, FolderName);
            var dirInfo = Directory.CreateDirectory(recycleRoot);
            if ((dirInfo.Attributes & FileAttributes.Hidden) == 0)
                dirInfo.Attributes |= FileAttributes.Hidden;

            string id = Guid.NewGuid().ToString("N");
            string storedDir = Path.Combine(recycleRoot, id);
            Directory.CreateDirectory(storedDir);
            string storedFull = Path.Combine(storedDir, folderName);

            try
            {
                Directory.Move(full, storedFull);
            }
            catch
            {
                TryDeleteDir(storedDir);
                throw;
            }

            try
            {
                using (var db = Db())
                {
                    db.Execute(@"
                        INSERT INTO RecycleBin (OriginalFolder, FileName, StoredPath, FileSize, Source, IsFolder, FileCount)
                        VALUES (@OriginalFolder, @FileName, @StoredPath, @FileSize, @Source, 1, @FileCount)",
                        new
                        {
                            OriginalFolder = originalFolder,
                            FileName = folderName,
                            StoredPath = FolderName + "/" + id + "/" + folderName,
                            FileSize = totalBytes,
                            Source = SourceGallery,
                            FileCount = fileCount
                        });
                }
            }
            catch
            {
                // Couldn't record it: put the folder back so nothing goes missing.
                Directory.Move(storedFull, full);
                TryDeleteDir(storedDir);
                throw;
            }
        }

        public static List<RecycleBinItem> GetAll()
        {
            using (var db = Db())
                return db.Query<RecycleBinItem>("SELECT * FROM RecycleBin ORDER BY DeletedAt DESC, RecycleId DESC").ToList();
        }

        public static RecycleBinItem Get(int id)
        {
            using (var db = Db())
                return db.QueryFirstOrDefault<RecycleBinItem>("SELECT * FROM RecycleBin WHERE RecycleId = @id", new { id });
        }

        /// <summary>Physical path of the recycled copy, or null if the record is invalid.</summary>
        public static string StoredFullPath(RecycleBinItem item)
        {
            var dirPart = Path.GetDirectoryName(item.StoredPath.Replace('/', '\\'));
            if (dirPart == null || !dirPart.StartsWith(FolderName + "\\", StringComparison.OrdinalIgnoreCase))
                return null;
            return MediaPaths.ResolveFile(dirPart, item.FileName);
        }

        /// <summary>Moves the file back to its original folder. Returns the restored relative path.</summary>
        public static string Restore(int id)
        {
            var item = Get(id) ?? throw new InvalidOperationException("That item is no longer in the Recycle Bin.");
            string stored = StoredFullPath(item);
            if (stored == null || (item.IsFolder ? !Directory.Exists(stored) : !File.Exists(stored)))
                throw new InvalidOperationException("The recycled " + (item.IsFolder ? "folder" : "file") + " is missing on disk.");

            string folder = MediaPaths.ResolveFolder(item.OriginalFolder)
                ?? throw new InvalidOperationException("The original folder is no longer valid.");
            Directory.CreateDirectory(folder);

            if (item.IsFolder)
                return RestoreFolder(item, stored, folder);

            // Don't overwrite a file that has since taken its place: "a.jpg" -> "a (restored).jpg", "a (restored 2).jpg"...
            string baseName = Path.GetFileNameWithoutExtension(item.FileName);
            string ext = Path.GetExtension(item.FileName);
            string name = item.FileName;
            for (int n = 1; File.Exists(Path.Combine(folder, name)); n++)
                name = n == 1 ? $"{baseName} (restored){ext}" : $"{baseName} (restored {n}){ext}";

            string target = Path.Combine(folder, name);
            File.Move(stored, target);
            TryDeleteDir(Path.GetDirectoryName(stored));

            using (var db = Db())
            {
                if (item.Source == SourceImages)
                {
                    // Put it back on the Images wall too
                    db.Execute("ImageInsert", new { Title = name, Description = item.ImageDescription ?? "", FilePath = target, FileExt = ext.ToLowerInvariant() },
                        commandType: CommandType.StoredProcedure);
                }
                db.Execute("DELETE FROM RecycleBin WHERE RecycleId = @id", new { id });
            }

            return (string.IsNullOrEmpty(item.OriginalFolder) ? "" : item.OriginalFolder + "/") + name;
        }

        private static string RestoreFolder(RecycleBinItem item, string stored, string parent)
        {
            // Never merge into a folder that has since taken its place: "Cats" -> "Cats (restored)", "Cats (restored 2)"...
            string name = item.FileName;
            for (int n = 1; Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)); n++)
                name = n == 1 ? $"{item.FileName} (restored)" : $"{item.FileName} (restored {n})";

            Directory.Move(stored, Path.Combine(parent, name));
            TryDeleteDir(Path.GetDirectoryName(stored));

            using (var db = Db())
                db.Execute("DELETE FROM RecycleBin WHERE RecycleId = @id", new { id = item.RecycleId });

            return (string.IsNullOrEmpty(item.OriginalFolder) ? "" : item.OriginalFolder + "/") + name;
        }

        public static void DeleteForever(int id)
        {
            var item = Get(id);
            if (item == null) return;

            string stored = StoredFullPath(item);
            if (stored != null && item.IsFolder && Directory.Exists(stored))
                ForceDeleteDirectory(stored);
            else if (stored != null && File.Exists(stored))
                File.Delete(stored);
            if (stored != null)
                TryDeleteDir(Path.GetDirectoryName(stored));

            using (var db = Db())
                db.Execute("DELETE FROM RecycleBin WHERE RecycleId = @id", new { id });
        }

        public static int Empty()
        {
            var items = GetAll();
            foreach (var item in items)
                DeleteForever(item.RecycleId);
            return items.Count;
        }

        /// <summary>Deletes a folder and everything in it, including read-only files.</summary>
        public static void ForceDeleteDirectory(string dir)
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(dir, true);
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception)
            {
                // leftover empty folder is harmless
            }
        }
    }
}
