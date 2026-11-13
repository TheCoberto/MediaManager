using System;
using System.IO;

namespace MediaManager.Models
{
    public class FileViewModel
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; } // File size in bytes
        public string FileSizeFormatted => FormatFileSize(FileSize); // Human-readable size
        public DateTime LastModified { get; set; }
        public DateTime CreatedDate { get; set; }
        public string FileType { get; set; }
        public string FileExtension { get; set; }
        public string Owner { get; set; }
        public bool IsReadOnly { get; set; }
        public DateTime LastAccessed { get; set; }
        public int FilesInDirectory { get; set; }
        public string MD5Hash { get; set; } // File Hash for uniqueness
        public int TotalFiles { get; set; } // Total files from all supported directories

        public static string FormatFileSize(long bytes)
        {
            if (bytes >= 1073741824) return $"{bytes / 1073741824.0:F2} GB";
            if (bytes >= 1048576) return $"{bytes / 1048576.0:F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} Bytes";
        }

        public static string ComputeMD5Hash(string filePath)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }
    }
}