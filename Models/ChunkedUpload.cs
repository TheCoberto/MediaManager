using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Web;

namespace MediaManager.Models
{
    /// <summary>
    /// Large uploads arrive in pieces (Scripts/upload-queue.js), so file size isn't limited by the per-request
    /// limits (maxRequestLength, timeouts). Pieces are appended to "&lt;media root&gt;\.uploads\&lt;id&gt;.part"
    /// (hidden, not listed, blocked from the web); once the last piece arrives the caller moves the file into
    /// place. That's on the same drive as the media, so it's a rename, not a copy.
    /// </summary>
    public static class ChunkedUpload
    {
        public const string FolderName = ".uploads";

        /// <summary>Size of each piece the browser sends (well under the 500 MB per-request limit).</summary>
        public const int ChunkBytes = 10 * 1024 * 1024;

        private static readonly Regex IdPattern = new Regex("^[a-f0-9]{32}$", RegexOptions.Compiled);
        private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

        public class Result
        {
            public bool Ok { get; set; }
            /// <summary>The last piece arrived: the whole file is at PartPath.</summary>
            public bool Complete { get; set; }
            public string PartPath { get; set; }
            public string Error { get; set; }
            /// <summary>Set when the piece didn't line up with what's been received so far (e.g. a retry).</summary>
            public long? ReceivedSoFar { get; set; }
        }

        public static string FormatLimit(long bytes) => FileViewModel.FormatFileSize(bytes);

        /// <summary>Appends one piece. <paramref name="offset"/> must equal the bytes received so far.</summary>
        public static Result Append(string uploadId, long offset, long totalSize, long maxBytes, HttpPostedFileBase piece)
        {
            if (uploadId == null || !IdPattern.IsMatch(uploadId))
                return Fail("Invalid upload.");
            if (totalSize <= 0)
                return Fail("The file is empty.");
            if (totalSize > maxBytes)
                return Fail("This file is too big (" + FormatLimit(totalSize) + "). The limit is " + FormatLimit(maxBytes) + ".");
            if (piece == null || offset < 0)
                return Fail("No data was received.");

            string dir = EnsureFolder();
            string part = Path.Combine(dir, uploadId + ".part");

            if (offset == 0)
            {
                CleanUpAbandoned(dir);
            }
            else
            {
                if (!File.Exists(part))
                    return Fail("This upload was interrupted for too long. Please upload the file again.");
                long received = new FileInfo(part).Length;
                if (received != offset)
                    return new Result { Error = "Upload out of step.", ReceivedSoFar = received };
            }

            if (offset + piece.ContentLength > totalSize)
            {
                Discard(uploadId);
                return Fail("Received more data than the file's size.");
            }

            using (var fs = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
                piece.InputStream.CopyTo(fs);

            return new Result { Ok = true, Complete = offset + piece.ContentLength == totalSize, PartPath = part };
        }

        /// <summary>Throws away a partly received upload.</summary>
        public static void Discard(string uploadId)
        {
            if (uploadId == null || !IdPattern.IsMatch(uploadId))
                return;
            try
            {
                string part = Path.Combine(MediaPaths.Root, FolderName, uploadId + ".part");
                if (File.Exists(part))
                    File.Delete(part);
            }
            catch (IOException)
            {
                // cleaned up later anyway
            }
        }

        private static string EnsureFolder()
        {
            string dir = Path.Combine(MediaPaths.Root, FolderName);
            var info = Directory.CreateDirectory(dir);
            if ((info.Attributes & FileAttributes.Hidden) == 0)
                info.Attributes |= FileAttributes.Hidden;
            return dir;
        }

        // Uploads abandoned part-way (tab closed, connection lost) are deleted after a day.
        private static void CleanUpAbandoned(string dir)
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.part"))
            {
                try
                {
                    if (DateTime.UtcNow - f.LastWriteTimeUtc > AbandonedAfter)
                        f.Delete();
                }
                catch (IOException)
                {
                    // in use: try again next time
                }
            }
        }

        private static Result Fail(string error) => new Result { Error = error };
    }
}
