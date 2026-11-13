using System.Configuration;
using System.IO;

namespace MediaManager.Models
{
    /// <summary>
    /// Settings that may differ per IIS site. Each site's app folder has its own Site.config
    /// (not in git, not published) which overrides the defaults in Web.config.
    /// </summary>
    public static class SiteSettings
    {
        public static string SiteName => Get("SiteName", "Media Manager");

        /// <summary>
        /// Hash of the owner password (see PasswordHasher; make one with deploy\New-PasswordHash.ps1), or null if
        /// Site.config doesn't set one. There is deliberately no built-in default: with none set, nobody can log in.
        /// </summary>
        public static string LoginPasswordHash => Get("LoginPasswordHash", null);

        /// <summary>Hash of the guest password (same format), or null if this site has no guest login.</summary>
        public static string GuestPasswordHash => Get("GuestPasswordHash", null);

        /// <summary>Folder (relative to the media root) where Images uploads are saved, e.g. "Guest Uploads".</summary>
        public static string ImageUploadFolder => Get("ImageUploadFolder", "Guest Uploads");

        /// <summary>Browser URL of an uploaded image.</summary>
        public static string ImageUrl(string fileName) => MediaPaths.UrlFor(ImageUploadFolder + "/" + fileName);

        /// <summary>Largest file the owner can upload (Add Media, Guest Uploads page). Site.config: MaxUploadMB, default 20 GB.</summary>
        public static long MaxUploadBytes => GetMegabytes("MaxUploadMB", 20 * 1024);

        /// <summary>Largest image a guest can upload. Site.config: GuestMaxUploadMB, default 50 MB.</summary>
        public static long GuestMaxUploadBytes => GetMegabytes("GuestMaxUploadMB", 50);

        /// <summary>Folder containing ffmpeg.exe and yt-dlp.exe.</summary>
        public static string ToolsFolder => Get("ToolsFolder", @"C:\Tools\");

        // Optional pages, switched per site with <add key="Feature:Name" value="true|false" /> in Site.config.
        // Off unless turned on:
        public static bool StorageRandomEnabled => IsFeatureEnabled("StorageRandom", false);

        // On unless turned off:
        /// <summary>The Guest Uploads page (ImagesController). Key kept as "Images" so existing Site.config files still work.</summary>
        public static bool ImagesEnabled => IsFeatureEnabled("Images", true);

        public static bool DownloadersEnabled => IsFeatureEnabled("Downloaders", true);

        public static bool IsFeatureEnabled(string feature, bool enabledByDefault)
        {
            return bool.TryParse(ConfigurationManager.AppSettings["Feature:" + feature], out var value) ? value : enabledByDefault;
        }

        public static string FfmpegPath => Path.Combine(ToolsFolder, "ffmpeg.exe");

        public static string YtDlpPath => Path.Combine(ToolsFolder, "yt-dlp.exe");

        private static long GetMegabytes(string key, long fallbackMb)
        {
            long mb = long.TryParse(ConfigurationManager.AppSettings[key], out var v) && v > 0 ? v : fallbackMb;
            return mb * 1024 * 1024;
        }

        private static string Get(string key, string fallback)
        {
            var value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
