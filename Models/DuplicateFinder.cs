using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace MediaManager.Models
{
    /// <summary>
    /// Finds files with identical contents (Duplicates page). Only files that share a size with another can be
    /// copies, so those are the only ones read: first a quick sample of each (its start and end), then the whole of
    /// any that still match, compared by SHA-256. Scans run in the background (big videos take a while to read) and
    /// the page polls for progress; one runs at a time, and finished ones are kept for an hour.
    /// </summary>
    public static class DuplicateFinder
    {
        /// <summary>Bytes read from each end of a file for the quick sample.</summary>
        private const int SampleBytes = 64 * 1024;

        private static readonly TimeSpan KeepFinishedFor = TimeSpan.FromHours(1);
        private static readonly ConcurrentDictionary<string, Scan> Scans = new ConcurrentDictionary<string, Scan>();

        public class Scan
        {
            public string Id;
            public string Folder;       // "" = the whole library
            public bool Subfolders;

            /// <summary>"listing", "sampling", "comparing", "done", "stopped" or "failed".</summary>
            public volatile string Phase = "listing";
            public volatile int FilesFound;
            public volatile int Done;
            public volatile int Total;
            public long BytesDone;      // comparing: bytes read so far, of BytesTotal
            public long BytesTotal;
            public volatile int Skipped; // files that couldn't be read
            public string Error;
            public List<List<string>> Groups; // physical paths, each group in suggested keep-first order
            public DateTime Finished;

            internal readonly CancellationTokenSource Cancel = new CancellationTokenSource();
        }

        public static Scan Get(string id) => id != null && Scans.TryGetValue(id, out var scan) ? scan : null;

        /// <summary>Starts a scan of a folder (physical path), stopping any other scan still running.</summary>
        public static Scan Start(string folder, string folderPath, bool subfolders)
        {
            foreach (var old in Scans.Values)
            {
                if (old.Finished == default(DateTime))
                    old.Cancel.Cancel();
                else if (DateTime.UtcNow - old.Finished > KeepFinishedFor)
                    Scans.TryRemove(old.Id, out _);
            }

            var scan = new Scan { Id = Guid.NewGuid().ToString("N"), Folder = folder, Subfolders = subfolders };
            Scans[scan.Id] = scan;
            HostingEnvironment.QueueBackgroundWorkItem(ct => Task.Run(() => Run(scan, folderPath, ct)));
            return scan;
        }

        public static void Stop(string id) => Get(id)?.Cancel.Cancel();

        private static void Run(Scan scan, string folderPath, CancellationToken shutdown)
        {
            using (var both = CancellationTokenSource.CreateLinkedTokenSource(scan.Cancel.Token, shutdown))
            {
                try
                {
                    var files = MediaPaths.GetMediaFiles(folderPath, scan.Subfolders);
                    scan.FilesFound = files.Count;
                    scan.Groups = FindGroups(files, scan, both.Token);
                    scan.Phase = "done";
                }
                catch (OperationCanceledException)
                {
                    scan.Phase = "stopped";
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Duplicate scan of {0} failed: {1}", folderPath, ex);
                    scan.Error = ex.Message;
                    scan.Phase = "failed";
                }
                finally
                {
                    scan.Finished = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// Groups of identical files (physical paths) among <paramref name="files"/>, most space to free first, each
        /// group in suggested keep-first order (see KeepOrder). Reports progress on <paramref name="scan"/>.
        /// </summary>
        internal static List<List<string>> FindGroups(IEnumerable<string> files, Scan scan, CancellationToken ct)
        {
            // Same size: the only files that can be copies of each other. Empty files are all "the same", so skip them.
            var sized = new List<FileInfo>();
            foreach (string path in files)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 0)
                        sized.Add(info);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    scan.Skipped++;
                }
            }
            var sameSize = sized.GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();

            // Quick sample: the start and end of each (the whole file when it's small)
            scan.Phase = "sampling";
            scan.Total = sameSize.Sum(g => g.Count());
            scan.Done = 0;
            var likely = new List<List<FileInfo>>();
            foreach (var group in sameSize)
            {
                var bySample = new Dictionary<string, List<FileInfo>>();
                foreach (var file in group)
                {
                    ct.ThrowIfCancellationRequested();
                    string sample = TryHash(file.FullName, sampleOnly: true, scan, ct);
                    scan.Done++;
                    if (sample == null)
                        continue;
                    if (!bySample.TryGetValue(sample, out var list))
                        bySample[sample] = list = new List<FileInfo>();
                    list.Add(file);
                }
                likely.AddRange(bySample.Values.Where(l => l.Count > 1));
            }

            // Whole contents, for files too big for the sample to have covered all of them
            scan.Phase = "comparing";
            var toRead = likely.Where(l => l[0].Length > 2 * SampleBytes).ToList();
            scan.Total = toRead.Sum(l => l.Count);
            scan.Done = 0;
            Interlocked.Exchange(ref scan.BytesTotal, toRead.Sum(l => l[0].Length * l.Count));
            Interlocked.Exchange(ref scan.BytesDone, 0);

            var result = likely.Where(l => l[0].Length <= 2 * SampleBytes).ToList();
            foreach (var group in toRead)
            {
                var byHash = new Dictionary<string, List<FileInfo>>();
                foreach (var file in group)
                {
                    ct.ThrowIfCancellationRequested();
                    string hash = TryHash(file.FullName, sampleOnly: false, scan, ct);
                    scan.Done++;
                    if (hash == null)
                        continue;
                    if (!byHash.TryGetValue(hash, out var list))
                        byHash[hash] = list = new List<FileInfo>();
                    list.Add(file);
                }
                result.AddRange(byHash.Values.Where(l => l.Count > 1));
            }

            return result
                .OrderByDescending(g => g[0].Length * (g.Count - 1))
                .Select(g => g.OrderBy(f => f, KeepOrder.Instance).Select(f => f.FullName).ToList())
                .ToList();
        }

        /// <summary>
        /// True if two files are the same size and their quick samples match: the check made again just before a copy
        /// is deleted, in case either file changed since the scan.
        /// </summary>
        public static bool StillMatch(string pathA, string pathB)
        {
            try
            {
                var a = new FileInfo(pathA);
                var b = new FileInfo(pathB);
                if (!a.Exists || !b.Exists || a.Length != b.Length)
                    return false;
                var none = new Scan();
                string sa = TryHash(pathA, true, none, CancellationToken.None);
                return sa != null && sa == TryHash(pathB, true, none, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>SHA-256 of the file's sample (start + end) or its whole contents; null if it can't be read.</summary>
        private static string TryHash(string path, bool sampleOnly, Scan scan, CancellationToken ct)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    if (sampleOnly)
                    {
                        long length = fs.Length;
                        HashRange(sha, fs, 0, Math.Min(SampleBytes, length), buffer, null, ct);
                        if (length > SampleBytes)
                            HashRange(sha, fs, Math.Max(SampleBytes, length - SampleBytes), length, buffer, null, ct);
                    }
                    else
                    {
                        HashRange(sha, fs, 0, fs.Length, buffer, scan, ct);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    return Convert.ToBase64String(sha.Hash);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                scan.Skipped++;
                return null;
            }
        }

        private static void HashRange(HashAlgorithm sha, Stream s, long from, long to, byte[] buffer, Scan progress, CancellationToken ct)
        {
            s.Position = from;
            long left = to - from;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n == 0)
                    break;
                sha.TransformBlock(buffer, 0, n, null, 0);
                left -= n;
                if (progress != null)
                    Interlocked.Add(ref progress.BytesDone, n);
            }
        }

        /// <summary>
        /// Which copy to suggest keeping: one without a copy mark in its name ("Trip (2).mp4", "Trip - Copy.mp4"),
        /// then the oldest, then the one with the shortest path.
        /// </summary>
        private class KeepOrder : IComparer<FileInfo>
        {
            public static readonly KeepOrder Instance = new KeepOrder();

            private static readonly Regex CopyMark = new Regex(@"(\s\(\d+\)|\s-\sCopy(\s\(\d+\))?|_copy\d*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            public int Compare(FileInfo a, FileInfo b)
            {
                int c = HasCopyMark(a).CompareTo(HasCopyMark(b));
                if (c == 0)
                    c = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
                if (c == 0)
                    c = a.FullName.Length.CompareTo(b.FullName.Length);
                return c != 0 ? c : string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
            }

            private static bool HasCopyMark(FileInfo f) => CopyMark.IsMatch(Path.GetFileNameWithoutExtension(f.Name));
        }
    }
}
