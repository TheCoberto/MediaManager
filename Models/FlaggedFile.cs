using System;

namespace MediaManager.Models
{
    /// <summary>A file currently flagged in LogTable (Corrupted and/or Marked for Deletion), for the Flagged page.</summary>
    public class FlaggedFile
    {
        public string FileName { get; set; }

        /// <summary>
        /// Its folder (relative to the media root, "" = top), or null for an old flag saved by name only whose
        /// file isn't anywhere now.
        /// </summary>
        public string Folder { get; set; }

        /// <summary>The file is in <see cref="Folder"/> right now (false if it was moved or deleted outside the site).</summary>
        public bool Exists { get; set; }

        public bool Corrupted { get; set; }
        public bool ToBeDeleted { get; set; }
        public DateTime FlaggedAt { get; set; }

        public bool IsVideo => Array.Exists(Constants.Constants.VideoExtensions,
            ext => FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
