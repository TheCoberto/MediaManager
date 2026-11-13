using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace MediaManager.Models
{
    public class FavoriteItem
    {
        public int FavoriteId { get; set; }
        public string Folder { get; set; }
        public string FileName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// The owner's favorite files, stored per site. Folders are stored normalized ("a/b", '' = root).
    /// Rename/Move/Delete through the site keep this in step (see MediaController).
    /// </summary>
    public static class Favorites
    {
        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);
        private static string Norm(string folder) => MediaPaths.NormalizeFolder(folder);

        public static List<FavoriteItem> GetAll()
        {
            using (var db = Db())
                return db.Query<FavoriteItem>("SELECT * FROM Favorites ORDER BY CreatedAt DESC, FavoriteId DESC").ToList();
        }

        public static FavoriteItem Get(int id)
        {
            using (var db = Db())
                return db.QueryFirstOrDefault<FavoriteItem>("SELECT * FROM Favorites WHERE FavoriteId = @id", new { id });
        }

        /// <summary>File names in one folder that are favorites (case-insensitive).</summary>
        public static HashSet<string> GetFileNamesIn(string folder)
        {
            using (var db = Db())
            {
                var names = db.Query<string>("SELECT FileName FROM Favorites WHERE Folder = @Folder", new { Folder = Norm(folder) });
                return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static bool IsFavorite(string folder, string fileName)
        {
            return GetFileNamesIn(folder).Contains(fileName);
        }

        public static void Set(string folder, string fileName, bool favorite)
        {
            using (var db = Db())
            {
                if (favorite)
                    db.Execute(@"
                        IF NOT EXISTS (SELECT 1 FROM Favorites WHERE Folder = @Folder AND FileName = @FileName)
                            INSERT INTO Favorites (Folder, FileName) VALUES (@Folder, @FileName)",
                        new { Folder = Norm(folder), FileName = fileName });
                else
                    db.Execute("DELETE FROM Favorites WHERE Folder = @Folder AND FileName = @FileName",
                        new { Folder = Norm(folder), FileName = fileName });
            }
        }

        public static void Rename(string folder, string oldName, string newName)
        {
            using (var db = Db())
                db.Execute("UPDATE Favorites SET FileName = @NewName WHERE Folder = @Folder AND FileName = @OldName",
                    new { Folder = Norm(folder), OldName = oldName, NewName = newName });
        }

        /// <param name="newName">The name it was moved under, if different (one update, so it can't touch another file's favorite).</param>
        public static void Move(string folder, string fileName, string newFolder, string newName = null)
        {
            using (var db = Db())
                db.Execute("UPDATE Favorites SET Folder = @NewFolder, FileName = @NewName WHERE Folder = @Folder AND FileName = @FileName",
                    new { Folder = Norm(folder), FileName = fileName, NewFolder = Norm(newFolder), NewName = newName ?? fileName });
        }

        public static void RemoveById(int id)
        {
            using (var db = Db())
                db.Execute("DELETE FROM Favorites WHERE FavoriteId = @id", new { id });
        }
    }
}
