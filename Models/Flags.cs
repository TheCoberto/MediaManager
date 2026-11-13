using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;

namespace MediaManager.Models
{
    /// <summary>
    /// Corrupted / Marked-for-deletion flags (dbo.LogTable). LogTable is a history: the newest row for a file is
    /// its current state, so flagging, unflagging and fixing each add a row.
    ///
    /// Rows are per file: folder + name. Rows written before the Folder column existed have Folder = NULL and
    /// apply to every file with that name, until a newer row for a specific folder says otherwise.
    /// Folders are stored normalized ("a/b", '' = the top folder).
    /// </summary>
    public static class Flags
    {
        public class State
        {
            public bool Corrupted { get; set; }
            public bool ToBeDeleted { get; set; }
            public bool IsFlagged => Corrupted || ToBeDeleted;
            /// <summary>The folder the deciding row was saved with (null = an old row saved by name only).</summary>
            public string Folder { get; set; }
        }

        private class Row
        {
            public int LogID { get; set; }
            public string Folder { get; set; }
            public string FileName { get; set; }
            public bool Corrupted { get; set; }
            public bool ToBeDeleted { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);
        private static string Norm(string folder) => MediaPaths.NormalizeFolder(folder);

        // Rows that decide one file's state: its own folder's, plus old name-only ones
        private const string RowsForFile = "FileName = @FileName AND (Folder = @Folder OR Folder IS NULL)";

        /// <summary>A file's current flags (nothing flagged if it has no rows).</summary>
        public static State Get(string folder, string fileName)
        {
            using (var db = Db())
                return db.QueryFirstOrDefault<State>(
                    "SELECT TOP 1 Corrupted, ToBeDeleted, Folder FROM LogTable WHERE " + RowsForFile +
                    " ORDER BY CreatedAt DESC, LogID DESC",
                    new { Folder = Norm(folder), FileName = fileName }) ?? new State();
        }

        /// <summary>Which of these files in one folder are currently flagged corrupted (case-insensitive).</summary>
        public static HashSet<string> CorruptedIn(string folder, ICollection<string> fileNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fileNames.Count == 0)
                return result;

            using (var db = Db())
                result.UnionWith(db.Query<string>(@"
                    SELECT FileName FROM (
                        SELECT FileName, Corrupted,
                               ROW_NUMBER() OVER (PARTITION BY FileName ORDER BY CreatedAt DESC, LogID DESC) AS rn
                        FROM LogTable
                        WHERE FileName IN @FileNames AND (Folder = @Folder OR Folder IS NULL)
                    ) x
                    WHERE rn = 1 AND Corrupted = 1",
                    new { Folder = Norm(folder), FileNames = fileNames }));
            return result;
        }

        /// <summary>Records a file's new state.</summary>
        public static void Set(string folder, string fileName, bool corrupted, bool toBeDeleted)
        {
            Insert(Norm(folder), fileName, corrupted, toBeDeleted);
        }

        /// <summary>
        /// Clears the flag on an old name-only row (folder unknown). Files that have their own newer rows keep
        /// their state.
        /// </summary>
        public static void ClearNameOnly(string fileName)
        {
            Insert(null, fileName, false, false);
        }

        /// <summary>The file was deleted through the site: whatever it was flagged for is dealt with.</summary>
        public static void Forget(string folder, string fileName)
        {
            if (Get(folder, fileName).IsFlagged)
                Set(folder, fileName, false, false);
        }

        /// <summary>The file was renamed or moved through the site: its flags go with it.</summary>
        public static void Move(string folder, string fileName, string newFolder, string newName)
        {
            var state = Get(folder, fileName);
            using (var db = Db())
                db.Execute("UPDATE LogTable SET Folder = @NewFolder, FileName = @NewName WHERE Folder = @Folder AND FileName = @FileName",
                    new { Folder = Norm(folder), FileName = fileName, NewFolder = Norm(newFolder), NewName = newName });

            // Flagged by an old name-only row: that row stays with the old name, so give the file its own
            if (state.IsFlagged && state.Folder == null)
                Set(newFolder, newName, state.Corrupted, state.ToBeDeleted);
        }

        /// <summary>
        /// Everything flagged right now, one entry per file, newest first. Old name-only flags are matched to the
        /// files with that name on disk (<paramref name="allFiles"/>: physical paths), or listed once with no folder
        /// if there aren't any.
        /// </summary>
        public static List<FlaggedFile> GetFlagged(IEnumerable<string> allFiles)
        {
            List<Row> rows;
            using (var db = Db())
                rows = db.Query<Row>("SELECT LogID, Folder, FileName, Corrupted, ToBeDeleted, CreatedAt FROM LogTable").ToList();

            var result = new List<FlaggedFile>();
            var byName = rows.GroupBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Folders on disk holding each name that has an old name-only row
            var namesWithOldRows = new HashSet<string>(rows.Where(r => r.Folder == null).Select(r => r.FileName), StringComparer.OrdinalIgnoreCase);
            var diskFolders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (namesWithOldRows.Count > 0)
            {
                foreach (var path in allFiles)
                {
                    string name = Path.GetFileName(path);
                    if (!namesWithOldRows.Contains(name))
                        continue;
                    if (!diskFolders.TryGetValue(name, out var list))
                        diskFolders[name] = list = new List<string>();
                    list.Add(MediaPaths.ToRelative(Path.GetDirectoryName(path)));
                }
            }

            foreach (var group in byName.Values)
            {
                string name = group[0].FileName;
                Row oldRow = Newest(group.Where(r => r.Folder == null));

                // Every folder this name has its own rows in, plus (for an old row) every folder it's in on disk
                var folders = new HashSet<string>(group.Where(r => r.Folder != null).Select(r => r.Folder), StringComparer.OrdinalIgnoreCase);
                if (oldRow != null && diskFolders.TryGetValue(name, out var onDisk))
                    folders.UnionWith(onDisk);

                foreach (var folder in folders)
                {
                    Row current = Newest(group.Where(r => r.Folder == null || string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)));
                    if (current.Corrupted || current.ToBeDeleted)
                        result.Add(ToFlagged(current, folder));
                }

                // An old flag for a name that isn't anywhere now (moved / deleted outside the site)
                if (oldRow != null && (oldRow.Corrupted || oldRow.ToBeDeleted) && folders.Count == 0)
                    result.Add(ToFlagged(oldRow, null));
            }

            return result.OrderByDescending(f => f.FlaggedAt).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Row Newest(IEnumerable<Row> rows) =>
            rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.LogID).FirstOrDefault();

        private static FlaggedFile ToFlagged(Row row, string folder)
        {
            string path = folder == null ? null : MediaPaths.ResolveFile(folder, row.FileName);
            return new FlaggedFile
            {
                FileName = row.FileName,
                Folder = folder,
                Exists = path != null && File.Exists(path),
                Corrupted = row.Corrupted,
                ToBeDeleted = row.ToBeDeleted,
                FlaggedAt = row.CreatedAt
            };
        }

        private static void Insert(string folder, string fileName, bool corrupted, bool toBeDeleted)
        {
            using (var db = Db())
                db.Execute(@"
                    INSERT INTO LogTable (Folder, FileName, ToBeDeleted, Corrupted, CreatedAt)
                    VALUES (@Folder, @FileName, @ToBeDeleted, @Corrupted, GETDATE())",
                    new { Folder = folder, FileName = fileName, ToBeDeleted = toBeDeleted, Corrupted = corrupted });
        }
    }
}
