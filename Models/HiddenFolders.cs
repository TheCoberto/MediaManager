using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace MediaManager.Models
{
    /// <summary>Top-level folders hidden from the home page folder list. Stored per site (in that site's database).</summary>
    public static class HiddenFolders
    {
        public static HashSet<string> GetAll()
        {
            using (IDbConnection db = new SqlConnection(Constants.Constants._connectionString))
            {
                var names = db.Query<string>("SELECT FolderName FROM HiddenFolders");
                return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void Set(string folderName, bool hidden)
        {
            using (IDbConnection db = new SqlConnection(Constants.Constants._connectionString))
            {
                const string hideSql = @"
                    IF NOT EXISTS (SELECT 1 FROM HiddenFolders WHERE FolderName = @FolderName)
                        INSERT INTO HiddenFolders (FolderName) VALUES (@FolderName)";
                const string showSql = "DELETE FROM HiddenFolders WHERE FolderName = @FolderName";

                db.Execute(hidden ? hideSql : showSql, new { FolderName = folderName });
            }
        }
    }
}
