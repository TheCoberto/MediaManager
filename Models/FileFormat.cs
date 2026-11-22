using System;
using System.IO;
using System.Text;

namespace MediaManager.Models
{
    /// <summary>
    /// What a file really is, from its first bytes, whatever its name says. Apps and caches often save pictures
    /// under a ".jpg" name in another format (TIFF, WebP, HEIC...). Windows can read some of those for thumbnails
    /// while browsers can't show them, so a file can look fine in the Gallery and blank on its View page.
    /// </summary>
    public static class FileFormat
    {
        public class Info
        {
            public string Name;        // "JPEG", "TIFF", ...; null if not recognized
            public string Extension;   // what it would normally be called: ".jpg", ".tif", ...
            public bool BrowsersShow;  // a picture every browser can show as it is
        }

        /// <summary>The file's real format, or null if it can't be read or isn't one listed here.</summary>
        public static Info Detect(string path)
        {
            var b = new byte[16];
            int n;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    n = fs.Read(b, 0, b.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
            return Detect(b, n);
        }

        internal static Info Detect(byte[] b, int n)
        {
            string ascii = Encoding.ASCII.GetString(b, 0, Math.Max(0, n));
            bool Starts(params int[] bytes)
            {
                if (n < bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++)
                    if (b[i] != bytes[i]) return false;
                return true;
            }

            if (Starts(0xFF, 0xD8, 0xFF)) return Make("JPEG", ".jpg", true);
            if (Starts(0x89, 0x50, 0x4E, 0x47)) return Make("PNG", ".png", true);
            if (ascii.StartsWith("GIF8")) return Make("GIF", ".gif", true);
            if (ascii.StartsWith("BM")) return Make("BMP", ".bmp", true);
            if (ascii.StartsWith("RIFF") && n >= 12 && ascii.Substring(8, 4) == "WEBP") return Make("WebP", ".webp", true);
            if (Starts(0x49, 0x49, 0x2A, 0x00) || Starts(0x4D, 0x4D, 0x00, 0x2A)) return Make("TIFF", ".tif", false);
            if (n >= 12 && ascii.Substring(4, 4) == "ftyp")
            {
                string brand = ascii.Substring(8, 4);
                if (brand == "heic" || brand == "heix" || brand == "mif1" || brand == "msf1" || brand == "hevc") return Make("HEIC", ".heic", false);
                if (brand == "avif" || brand == "avis") return Make("AVIF", ".avif", true);
                if (brand.StartsWith("qt")) return Make("QuickTime video", ".mov", false);
                return Make("MP4 video", ".mp4", false);
            }
            if (Starts(0x1A, 0x45, 0xDF, 0xA3)) return Make("WebM / MKV video", ".webm", false);
            if (ascii.StartsWith("%PDF")) return Make("PDF", ".pdf", false);
            return null;
        }

        /// <summary>True if the name's extension doesn't fit what the file really is (".jpeg" fits JPEG, ".tiff" fits TIFF).</summary>
        public static bool NameMismatch(Info info, string fileName)
        {
            if (info == null)
                return false;
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext == info.Extension)
                return false;
            return !(info.Name == "JPEG" && (ext == ".jpeg" || ext == ".jfif"))
                && !(info.Name == "TIFF" && ext == ".tiff")
                && !(info.Name == "HEIC" && ext == ".heif");
        }

        private static Info Make(string name, string ext, bool browsersShow) =>
            new Info { Name = name, Extension = ext, BrowsersShow = browsersShow };
    }
}
