using System;
using System.IO;

namespace MediaManager.Models
{
    /// <summary>
    /// The kinds of file the site shows (from MediaPaths.KindOf), and how each one is displayed.
    /// The extension lists live in Constants; the browser gets them too (window.MEDIA_KINDS in _MediaTools).
    /// </summary>
    public static class MediaKind
    {
        public const string Image = "image";
        public const string Video = "video";
        public const string Audio = "audio";
        public const string Document = "document";

        /// <summary>Documents the browser can show itself: PDFs in its PDF viewer.</summary>
        public static bool IsPdf(string fileName) => HasExtension(fileName, ".pdf");

        /// <summary>Plain-text documents, shown as text on the page.</summary>
        public static bool IsText(string fileName) => HasExtension(fileName, ".txt", ".md");

        /// <summary>Short label for a file's type, e.g. "PDF", "MP3".</summary>
        public static string Label(string fileName) => Path.GetExtension(fileName ?? "").TrimStart('.').ToUpperInvariant();

        /// <summary>Icon for kinds that have no picture of their own.</summary>
        public static string Icon(string fileName)
        {
            switch (MediaPaths.KindOf(fileName))
            {
                case Audio: return "🎵";
                case Video: return "🎬";
                case Document: return IsPdf(fileName) ? "📕" : IsText(fileName) ? "📄" : "📝";
                default: return "🖼️";
            }
        }

        private static bool HasExtension(string fileName, params string[] extensions)
        {
            string ext = Path.GetExtension(fileName ?? "");
            return Array.Exists(extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }
    }
}
