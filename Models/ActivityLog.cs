using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Threading;
using System.Web;

namespace MediaManager.Models
{
    /// <summary>
    /// What the site has been doing (dbo.ActivityLog, Activity page): uploads, moves, renames, deletes, fixes,
    /// playable copies, duplicate scans, Recycle Bin changes, logins, and the server's own warnings and errors
    /// (ActivityLogTraceListener catches every Trace.TraceWarning / TraceError).
    ///
    /// Writing never throws: a log that can't be written (database down, table missing) mustn't stop the action it
    /// describes. Entries older than KeepDays are removed now and then.
    /// </summary>
    public static class ActivityLog
    {
        public const int KeepDays = 60;

        // Kinds, for the page's labels and filters
        public const string Upload = "upload";
        public const string Delete = "delete";
        public const string Rename = "rename";
        public const string Move = "move";
        public const string Copy = "copy";
        public const string Tags = "tags";
        public const string Fix = "fix";
        public const string Playable = "playable";
        public const string Duplicates = "duplicates";
        public const string RecycleBin = "recyclebin";
        public const string Login = "login";
        public const string Server = "server";

        public class Entry
        {
            public int ActivityId { get; set; }
            public DateTime At { get; set; }
            public string Kind { get; set; }
            public string Folder { get; set; }    // the file's folder ('' = top), or null if it isn't about one file
            public string FileName { get; set; }
            public string Message { get; set; }
            public bool IsError { get; set; }
            public string Ip { get; set; }        // the device that did it; null for the server's own entries
            public string Role { get; set; }      // Owner / Guest, or null (not logged in, or the server)
        }

        /// <summary>
        /// HttpContext.Items key: the role to log for this request when the session doesn't say it (set at login,
        /// where the old session has just been thrown away).
        /// </summary>
        public const string RoleItem = "ActivityLog.Role";

        private static long lastPruneTicks;

        // Whether the database has the Ip and Role columns yet. The sites' SQL logins can't add them
        // (deploy\Setup-Database.ps1 does), so until then the log works as before without them. While they're
        // missing, it looks again every few minutes.
        private static volatile bool hasWhoColumns;
        private static long whoCheckedTicks;
        private static readonly long WhoRecheckTicks = TimeSpan.FromMinutes(5).Ticks;

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);

        private static bool HasWhoColumns(IDbConnection db)
        {
            if (hasWhoColumns)
                return true;
            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref whoCheckedTicks) < WhoRecheckTicks)
                return false;
            Interlocked.Exchange(ref whoCheckedTicks, now);
            hasWhoColumns = db.ExecuteScalar<int>(@"
                SELECT CASE WHEN COL_LENGTH('dbo.ActivityLog', 'Ip') IS NOT NULL
                             AND COL_LENGTH('dbo.ActivityLog', 'Role') IS NOT NULL THEN 1 ELSE 0 END") == 1;
            return hasWhoColumns;
        }

        /// <summary>The IP address and role of the request this runs in, or nulls outside one (background work, startup).</summary>
        private static void CurrentWho(out string ip, out string role)
        {
            ip = null;
            role = null;
            try
            {
                var http = HttpContext.Current;
                if (http == null)
                    return;
                ip = http.Request.UserHostAddress;
                role = http.Items[RoleItem] as string
                    ?? http.Session?["Role"] as string
                    ?? MediaAccess.Read(new HttpContextWrapper(http))?.Role; // media requests have no session
            }
            catch (HttpException)
            {
                // "Request is not available in this context" (Application_Start): no request, so nobody to name
            }
        }

        public static void Write(string kind, string message, string folder = null, string fileName = null, bool isError = false)
        {
            try
            {
                CurrentWho(out string ip, out string role);
                using (var db = Db())
                {
                    db.Execute(HasWhoColumns(db)
                        ? @"INSERT INTO ActivityLog (Kind, Folder, FileName, Message, IsError, Ip, Role)
                            VALUES (@Kind, @Folder, @FileName, @Message, @IsError, @Ip, @Role)"
                        : @"INSERT INTO ActivityLog (Kind, Folder, FileName, Message, IsError)
                            VALUES (@Kind, @Folder, @FileName, @Message, @IsError)",
                        new
                        {
                            Kind = Cut(kind, 30),
                            Folder = folder == null ? null : Cut(MediaPaths.NormalizeFolder(folder), 1000),
                            FileName = Cut(fileName, 400),
                            Message = Cut(message ?? "", 2000),
                            IsError = isError,
                            Ip = Cut(ip, 45),
                            Role = Cut(role, 10)
                        });
                    PruneNowAndThen(db);
                }
            }
            catch (Exception ex) when (ex is SqlException || ex is InvalidOperationException || ex is TimeoutException)
            {
                // Not through Trace: the listener would try to log this failure too
                Debug.WriteLine("ActivityLog write failed: " + ex.Message);
            }
        }

        public static void Error(string kind, string message, string folder = null, string fileName = null) =>
            Write(kind, message, folder, fileName, isError: true);

        /// <summary>The newest entries (before an id, to page back), optionally errors only.</summary>
        public static List<Entry> Recent(int take, int? beforeId = null, bool errorsOnly = false)
        {
            using (var db = Db())
                return db.Query<Entry>(@"
                    SELECT TOP (@Take) ActivityId, At, Kind, Folder, FileName, Message, IsError" + (HasWhoColumns(db) ? ", Ip, Role" : "") + @"
                    FROM ActivityLog
                    WHERE (@BeforeId IS NULL OR ActivityId < @BeforeId) AND (@ErrorsOnly = 0 OR IsError = 1)
                    ORDER BY ActivityId DESC",
                    new { Take = take, BeforeId = beforeId, ErrorsOnly = errorsOnly }).AsList();
        }

        /// <summary>Removes entries older than KeepDays, at most once a day.</summary>
        private static void PruneNowAndThen(IDbConnection db)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastPruneTicks);
            if (now - last < TimeSpan.TicksPerDay || Interlocked.CompareExchange(ref lastPruneTicks, now, last) != last)
                return;
            db.Execute("DELETE FROM ActivityLog WHERE At < DATEADD(DAY, -@Days, SYSDATETIME())", new { Days = KeepDays });
        }

        /// <summary>"1.2 GB", "340 KB": for messages.</summary>
        public static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return i == 0 ? bytes + " bytes" : size.ToString(size < 10 ? "0.#" : "0") + " " + units[i];
        }

        private static string Cut(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    /// <summary>
    /// Sends the site's Trace.TraceWarning / TraceError messages (failed thumbnails, uploads kept as they came,
    /// failed playable copies...) to the activity log as server errors. Registered in Global.asax.
    /// </summary>
    public class ActivityLogTraceListener : TraceListener
    {
        [ThreadStatic] private static bool writing; // a failing write mustn't loop back in

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            if (eventType <= TraceEventType.Warning)
                Log(args == null || args.Length == 0 ? format : string.Format(format, args));
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            if (eventType <= TraceEventType.Warning)
                Log(message);
        }

        // Plain Trace.Write / WriteLine (debug chatter) isn't logged
        public override void Write(string message) { }
        public override void WriteLine(string message) { }

        private static void Log(string message)
        {
            if (writing)
                return;
            writing = true;
            try
            {
                ActivityLog.Error(ActivityLog.Server, message);
            }
            finally
            {
                writing = false;
            }
        }
    }
}
