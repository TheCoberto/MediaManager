using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;

namespace MediaManager.Models.Constants
{
    public class Constants
    {
        public static Random RNG { get; set; } = new Random();

        // File types the site shows, by kind (see MediaPaths.KindOf). Any new extension also needs a
        // <mimeMap> in Web.config if IIS doesn't already know it, or IIS refuses to serve the file (404).
        // Never add .html / .htm / .svg / .js: those would run as web pages on this site.
        public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic" };

        public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".webm" };

        public static readonly string[] AudioExtensions = { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac" };

        public static readonly string[] DocumentExtensions = { ".txt", ".md", ".pdf", ".odt", ".ods", ".odp", ".doc", ".docx", ".rtf" };

        /// <summary>Every file type the site lists, uploads and serves.</summary>
        public static readonly string[] SupportedExtensions =
            ImageExtensions.Concat(VideoExtensions).Concat(AudioExtensions).Concat(DocumentExtensions).ToArray();

        public static string _connectionString = ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;

        // Folder names hidden from folder lists and random picks, at any depth, on every site.
        public static readonly List<string> ExcludedDirectories = new List<string>()
        {
            // System folders
            "$RECYCLE.BIN",
            "System Volume Information",
            "Program Files",

            // Non-media folders that can sit next to media when a whole drive is the media root
            "Documents",
            "GS",
            "Gamevault",

            // The site's Recycle Bin (Models/RecycleBin.cs), uploads in progress (Models/ChunkedUpload.cs)
            // and thumbnails (Models/Thumbnails.cs)
            ".recycle",
            ".uploads",
            ".thumbs",

            // Tool / app-generated folders
            ".thumb.stogram",   // 4K Stogram's thumbnail cache (the "4K Stogram" folder itself is shown)
            ".vs",

            // Leftovers from when the app was deployed inside the media folders
            "bin",
            "Views",
            "Scripts",
            "Content",
            "fonts",
            "asp_net",
            "aspnet_client",
        };
    }
}
