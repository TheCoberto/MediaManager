using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web;

namespace MediaManager.Models
{
    /// <summary>
    /// Bulk downloads: streams a ZIP straight to the browser while it's being built, so it starts at once,
    /// needs no temporary copy, and works for any size (files over 4 GB use ZIP64; tested with .NET Framework).
    /// Files are stored, not compressed: photos / videos / audio are already compressed, so it's much faster.
    /// </summary>
    public static class ZipDownload
    {
        public class Item
        {
            public string FullPath { get; set; }
            /// <summary>Path inside the ZIP, with "/" between folders.</summary>
            public string ZipPath { get; set; }
        }

        /// <summary>Writes the ZIP to the response. Stops quietly if the browser goes away.</summary>
        public static void Send(HttpContextBase http, string zipName, IEnumerable<Item> items)
        {
            var response = http.Response;
            http.Server.ScriptTimeout = (int)TimeSpan.FromHours(12).TotalSeconds; // big downloads take a while
            response.BufferOutput = false;
            response.ContentType = "application/zip";
            response.AddHeader("Content-Disposition", ContentDisposition(zipName));

            var skipped = new List<string>();
            try
            {
                using (var counting = new PositionTrackingStream(response.OutputStream))
                using (var zip = new ZipArchive(counting, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var item in items)
                    {
                        if (!response.IsClientConnected)
                            return;

                        FileStream source;
                        try
                        {
                            source = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            skipped.Add(item.ZipPath + "  (" + ex.Message + ")");
                            continue;
                        }

                        using (source)
                        {
                            var entry = zip.CreateEntry(item.ZipPath, CompressionLevel.NoCompression);
                            entry.LastWriteTime = ZipSafeDate(File.GetLastWriteTime(item.FullPath));
                            using (var target = entry.Open())
                                source.CopyTo(target, 1 << 20);
                        }
                    }

                    if (skipped.Count > 0)
                    {
                        var note = zip.CreateEntry("_files that could not be included.txt", CompressionLevel.Optimal);
                        using (var w = new StreamWriter(note.Open(), Encoding.UTF8))
                            w.Write(string.Join("\r\n", skipped));
                    }
                }
            }
            catch (Exception) when (!response.IsClientConnected)
            {
                // The download was cancelled or the connection dropped: nothing more to do.
            }
        }

        /// <summary>attachment; filename="..." with a UTF-8 version for names like "Café.zip".</summary>
        private static string ContentDisposition(string fileName)
        {
            string ascii = new string(fileName.Select(c => c < 32 || c > 126 || c == '"' || c == '\\' ? '_' : c).ToArray());
            return "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + Uri.EscapeDataString(fileName);
        }

        // ZIP dates only go from 1980 to 2107
        private static DateTimeOffset ZipSafeDate(DateTime d)
        {
            if (d.Year < 1980) return new DateTimeOffset(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local));
            if (d.Year > 2107) return new DateTimeOffset(new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local));
            return new DateTimeOffset(d);
        }

        /// <summary>
        /// The web response can't seek or report its position, which .NET Framework's ZipArchive needs while
        /// writing; this counts the bytes written instead (what newer .NET does internally).
        /// </summary>
        private sealed class PositionTrackingStream : Stream
        {
            private readonly Stream inner;
            private long position;

            public PositionTrackingStream(Stream inner) { this.inner = inner; }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => position;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                inner.Write(buffer, offset, count);
                position += count;
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
