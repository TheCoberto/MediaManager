using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web;

namespace MediaManager.Models
{
    /// <summary>
    /// How often each file has been looked at (dbo.MediaViews, one row per view): the count on the view page,
    /// the Most Viewed page, and the Activity page's weekly summary.
    ///
    /// A view is the file's own page opening (View, Random). Opening the same file again in the same browser
    /// within RepeatWindow doesn't count again, so refreshing or stepping back and forth doesn't inflate it.
    /// Rename / Move / Delete through the site keep the counts with the file (MediaController), like tags.
    /// Counting never throws: a database without the table (SQL\Schema.sql not applied yet) just doesn't count.
    /// </summary>
    public static class MediaViews
    {
        public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(30);

        public sealed class Count
        {
            public string Folder { get; set; }
            public string FileName { get; set; }
            public int Views { get; set; }
            public DateTime LastAt { get; set; }
        }

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);
        private static string Norm(string folder) => MediaPaths.NormalizeFolder(folder);

        /// <summary>Counts a view of this file, unless this browser already looked at it within RepeatWindow.</summary>
        public static void Record(HttpContextBase context, string folder, string fileName)
        {
            folder = Norm(folder);
            string key = folder + "/" + fileName;
            var seen = context.Session?["MediaViews.Seen"] as Dictionary<string, DateTime>;
            if (seen == null)
            {
                seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                if (context.Session != null)
                    context.Session["MediaViews.Seen"] = seen;
            }
            var now = DateTime.UtcNow;
            lock (seen)
            {
                if (seen.TryGetValue(key, out var last) && now - last < RepeatWindow)
                    return;
                seen[key] = now;
            }

            Run(db => db.Execute("INSERT INTO MediaViews (Folder, FileName, Role) VALUES (@Folder, @FileName, @Role)",
                new { Folder = folder, FileName = fileName, Role = context.Session?["Role"] as string }));
        }

        /// <summary>All views of one file (0 if none, or the table isn't there yet).</summary>
        public static int CountFor(string folder, string fileName)
        {
            return Query(db => db.ExecuteScalar<int>("SELECT COUNT(*) FROM MediaViews WHERE FileName = @FileName AND Folder = @Folder",
                new { Folder = Norm(folder), FileName = fileName }), 0);
        }

        public static int TotalSince(DateTime sinceLocal)
        {
            return Query(db => db.ExecuteScalar<int>("SELECT COUNT(*) FROM MediaViews WHERE At >= @Since", new { Since = sinceLocal }), 0);
        }

        /// <summary>The most viewed files that still exist, most first (since a time, or ever).</summary>
        public static List<Count> Top(int take, DateTime? sinceLocal = null)
        {
            var rows = Query(db => db.Query<Count>(@"
                SELECT TOP (@Take) Folder, FileName, COUNT(*) AS Views, MAX(At) AS LastAt
                FROM MediaViews
                WHERE @Since IS NULL OR At >= @Since
                GROUP BY Folder, FileName
                ORDER BY COUNT(*) DESC, MAX(At) DESC",
                new { Take = take * 2 + 10, Since = sinceLocal }).AsList(), new List<Count>());

            // Files deleted or moved outside the site are left out
            return rows.Where(r => MediaPaths.ResolveFile(r.Folder, r.FileName) is string p && File.Exists(p)).Take(take).ToList();
        }

        public static void Rename(string folder, string oldName, string newName)
        {
            Run(db => db.Execute("UPDATE MediaViews SET FileName = @NewName WHERE FileName = @OldName AND Folder = @Folder",
                new { Folder = Norm(folder), OldName = oldName, NewName = newName }));
        }

        public static void Move(string folder, string fileName, string newFolder, string newName)
        {
            Run(db => db.Execute("UPDATE MediaViews SET Folder = @NewFolder, FileName = @NewName WHERE FileName = @FileName AND Folder = @Folder",
                new { Folder = Norm(folder), FileName = fileName, NewFolder = Norm(newFolder), NewName = newName }));
        }

        public static void Forget(string folder, string fileName)
        {
            Run(db => db.Execute("DELETE FROM MediaViews WHERE FileName = @FileName AND Folder = @Folder",
                new { Folder = Norm(folder), FileName = fileName }));
        }

        private static void Run(Action<IDbConnection> action) => Query(db => { action(db); return 0; }, 0);

        private static T Query<T>(Func<IDbConnection, T> query, T fallback)
        {
            try
            {
                using (var db = Db())
                    return query(db);
            }
            catch (SqlException ex)
            {
                // Most likely the table isn't there yet (SQL\Schema.sql not applied); views just aren't counted.
                Debug.WriteLine("MediaViews: " + ex.Message);
                return fallback;
            }
        }
    }
}
