using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace MediaManager.Models
{
    /// <summary>
    /// The owner's own tags (dbo.Tags) and which files carry them (dbo.MediaTags), per site. Files are kept by folder
    /// ("a/b", '' = root) and name, like Favorites, and Rename / Move / Delete through the site keep them in step
    /// (see MediaController). Tag names are unique ignoring case.
    /// </summary>
    public static class Tags
    {
        public const int MaxNameLength = 100;

        public class Tag
        {
            public int TagId { get; set; }
            public string Name { get; set; }
            public int FileCount { get; set; }
        }

        public class TaggedFile
        {
            public string Folder { get; set; }
            public string FileName { get; set; }
            public DateTime TaggedAt { get; set; }   // newest tagging among the tags asked for
            public string TagIds { get; set; }       // all of the file's tags, "1,4,7"
        }

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);
        private static string Norm(string folder) => MediaPaths.NormalizeFolder(folder);

        /// <summary>Trims a typed name and squeezes inner spaces; null if it's empty or too long.</summary>
        public static string CleanName(string name)
        {
            name = string.Join(" ", (name ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return name.Length == 0 || name.Length > MaxNameLength ? null : name;
        }

        /// <summary>Every tag with how many files carry it, by name.</summary>
        public static List<Tag> GetAll()
        {
            using (var db = Db())
                return db.Query<Tag>(@"
                    SELECT t.TagId, t.Name, COUNT(m.MediaTagId) AS FileCount
                    FROM Tags t LEFT JOIN MediaTags m ON m.TagId = t.TagId
                    GROUP BY t.TagId, t.Name
                    ORDER BY t.Name").AsList();
        }

        /// <summary>The tag with this name (ignoring case), created if there isn't one. Null if the name isn't usable.</summary>
        public static Tag GetOrCreate(string name)
        {
            name = CleanName(name);
            if (name == null)
                return null;
            using (var db = Db())
                return db.QueryFirst<Tag>(@"
                    IF NOT EXISTS (SELECT 1 FROM Tags WHERE Name = @Name)
                        INSERT INTO Tags (Name) VALUES (@Name);
                    SELECT TagId, Name, 0 AS FileCount FROM Tags WHERE Name = @Name", new { Name = name });
        }

        /// <summary>Renames a tag. False if another tag already has that name, or it isn't usable.</summary>
        public static bool Rename(int tagId, string newName)
        {
            newName = CleanName(newName);
            if (newName == null)
                return false;
            using (var db = Db())
                return db.Execute(@"
                    UPDATE Tags SET Name = @Name
                    WHERE TagId = @TagId AND NOT EXISTS (SELECT 1 FROM Tags WHERE Name = @Name AND TagId <> @TagId)",
                    new { TagId = tagId, Name = newName }) > 0;
        }

        /// <summary>Deletes a tag; it comes off every file (the files themselves aren't touched).</summary>
        public static void Delete(int tagId)
        {
            using (var db = Db())
                db.Execute("DELETE FROM Tags WHERE TagId = @TagId", new { TagId = tagId }); // MediaTags rows go too (cascade)
        }

        /// <summary>One file's tags, by name.</summary>
        public static List<Tag> For(string folder, string fileName)
        {
            using (var db = Db())
                return db.Query<Tag>(@"
                    SELECT t.TagId, t.Name FROM MediaTags m JOIN Tags t ON t.TagId = m.TagId
                    WHERE m.Folder = @Folder AND m.FileName = @FileName
                    ORDER BY t.Name", new { Folder = Norm(folder), FileName = fileName }).AsList();
        }

        /// <summary>Tag names of many files in one folder at once (Gallery), by file name (case-insensitive).</summary>
        public static Dictionary<string, List<string>> NamesIn(string folder, ICollection<string> fileNames)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (fileNames.Count == 0)
                return result;
            using (var db = Db())
            {
                // In batches: SQL Server takes at most ~2100 parameters
                foreach (var batch in fileNames.Select((n, i) => new { n, i }).GroupBy(x => x.i / 1000, x => x.n))
                    foreach (var row in db.Query(@"
                        SELECT m.FileName, t.Name FROM MediaTags m JOIN Tags t ON t.TagId = m.TagId
                        WHERE m.Folder = @Folder AND m.FileName IN @Names
                        ORDER BY t.Name", new { Folder = Norm(folder), Names = batch.ToList() }))
                    {
                        string file = row.FileName;
                        if (!result.TryGetValue(file, out var list))
                            result[file] = list = new List<string>();
                        list.Add((string)row.Name);
                    }
            }
            return result;
        }

        /// <summary>Puts a tag on a file (nothing happens if it's already there).</summary>
        public static void Add(int tagId, string folder, string fileName)
        {
            using (var db = Db())
                db.Execute(@"
                    IF NOT EXISTS (SELECT 1 FROM MediaTags WHERE TagId = @TagId AND Folder = @Folder AND FileName = @FileName)
                        INSERT INTO MediaTags (TagId, Folder, FileName) VALUES (@TagId, @Folder, @FileName)",
                    new { TagId = tagId, Folder = Norm(folder), FileName = fileName });
        }

        public static void Remove(int tagId, string folder, string fileName)
        {
            using (var db = Db())
                db.Execute("DELETE FROM MediaTags WHERE TagId = @TagId AND Folder = @Folder AND FileName = @FileName",
                    new { TagId = tagId, Folder = Norm(folder), FileName = fileName });
        }

        /// <summary>
        /// Files carrying the given tags: any of them, or all of them (matchAll). TaggedAt is the newest of those
        /// taggings, TagIds every tag the file has.
        /// </summary>
        public static List<TaggedFile> FilesWith(ICollection<int> tagIds, bool matchAll)
        {
            if (tagIds.Count == 0)
                return new List<TaggedFile>();
            using (var db = Db())
                return db.Query<TaggedFile>(@"
                    SELECT m.Folder, m.FileName, MAX(m.TaggedAt) AS TaggedAt,
                           (SELECT STRING_AGG(CAST(a.TagId AS varchar(12)), ',') FROM MediaTags a
                            WHERE a.Folder = m.Folder AND a.FileName = m.FileName) AS TagIds
                    FROM MediaTags m
                    WHERE m.TagId IN @TagIds
                    GROUP BY m.Folder, m.FileName
                    HAVING @MatchAll = 0 OR COUNT(DISTINCT m.TagId) = @Count",
                    new { TagIds = tagIds.ToList(), MatchAll = matchAll, Count = tagIds.Count }).AsList();
        }

        // ---------- keeping in step with Rename / Move / Delete through the site ----------

        public static void Rename(string folder, string oldName, string newName)
        {
            using (var db = Db())
                db.Execute("UPDATE MediaTags SET FileName = @NewName WHERE Folder = @Folder AND FileName = @OldName",
                    new { Folder = Norm(folder), OldName = oldName, NewName = newName });
        }

        public static void Move(string folder, string fileName, string newFolder, string newName)
        {
            using (var db = Db())
                db.Execute("UPDATE MediaTags SET Folder = @NewFolder, FileName = @NewName WHERE Folder = @Folder AND FileName = @FileName",
                    new { Folder = Norm(folder), FileName = fileName, NewFolder = Norm(newFolder), NewName = newName });
        }

        public static void Forget(string folder, string fileName)
        {
            using (var db = Db())
                db.Execute("DELETE FROM MediaTags WHERE Folder = @Folder AND FileName = @FileName",
                    new { Folder = Norm(folder), FileName = fileName });
        }
    }
}
