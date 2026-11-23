using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace MediaManager.Models
{
    public class GuestbookEntry
    {
        public int EntryId { get; set; }
        public string Name { get; set; }
        public string Message { get; set; }
        /// <summary>Owner or Guest: who wrote it.</summary>
        public string Role { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>The Guestbook page: short messages from guests (and replies from the owner). Stored per site.</summary>
    public static class Guestbook
    {
        public const int MaxName = 50;
        public const int MaxMessage = 1000;

        private static IDbConnection Db() => new SqlConnection(Constants.Constants._connectionString);

        /// <summary>The newest entries first.</summary>
        public static List<GuestbookEntry> GetRecent(int count = 200)
        {
            using (var db = Db())
                return db.Query<GuestbookEntry>(
                    "SELECT TOP (@count) * FROM GuestbookEntries ORDER BY CreatedAt DESC, EntryId DESC", new { count }).ToList();
        }

        public static void Add(string name, string message, string role)
        {
            using (var db = Db())
                db.Execute("INSERT INTO GuestbookEntries (Name, Message, Role) VALUES (@name, @message, @role)",
                    new { name, message, role });
        }

        public static void Delete(int id)
        {
            using (var db = Db())
                db.Execute("DELETE FROM GuestbookEntries WHERE EntryId = @id", new { id });
        }
    }
}
