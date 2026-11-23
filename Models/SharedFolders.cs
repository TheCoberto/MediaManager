using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace MediaManager.Models
{
    /// <summary>
    /// Folders the owner has shared with guests (the "Share with guests" button on a folder's Gallery page).
    /// Sharing a folder shares everything under it, read-only. Stored per site, normalized ("a/b").
    /// Kept in memory because Filters/MediaAuthModule asks on every /media request a guest makes.
    /// </summary>
    public static class SharedFolders
    {
        private static volatile List<string> cache;

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);

        /// <summary>Every shared folder, in name order.</summary>
        public static List<string> GetAll()
        {
            var list = cache;
            if (list != null)
                return list;

            using (var db = Db())
                list = db.Query<string>("SELECT Folder FROM SharedFolders").ToList();
            list.Sort(NaturalStringComparer.Instance);
            cache = list;
            return list;
        }

        /// <summary>True if this folder was shared itself (not just through a parent).</summary>
        public static bool IsSharedDirectly(string folder)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            return GetAll().Contains(folder, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>True if guests may see this folder: it, or a folder above it, is shared.</summary>
        public static bool IsShared(string folder)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            if (folder.Length == 0 || folder.Split('/').Any(s => s == ".." || s == "."))
                return false;
            return GetAll().Any(s => string.Equals(folder, s, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase));
        }

        public static void Set(string folder, bool shared)
        {
            folder = MediaPaths.NormalizeFolder(folder);
            if (folder.Length == 0)
                return; // never share the whole library
            using (var db = Db())
            {
                if (shared)
                    db.Execute(@"
                        IF NOT EXISTS (SELECT 1 FROM SharedFolders WHERE Folder = @folder)
                            INSERT INTO SharedFolders (Folder) VALUES (@folder)", new { folder });
                else
                    db.Execute("DELETE FROM SharedFolders WHERE Folder = @folder", new { folder });
            }
            cache = null;
        }
    }
}
