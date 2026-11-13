using System;
using System.Collections.Generic;

namespace MediaManager.Models
{
    /// <summary>A file currently flagged in LogTable (Corrupted and/or Marked for Deletion), for the Flagged page.</summary>
    public class FlaggedFile
    {
        public string FileName { get; set; }
        public bool Corrupted { get; set; }
        public bool ToBeDeleted { get; set; }
        public DateTime FlaggedAt { get; set; }

        /// <summary>
        /// Folders (relative to the media root, "" = top) where a file with this name exists right now.
        /// Flags are stored by name only, so this can be empty (moved/deleted outside the app) or have several entries.
        /// </summary>
        public List<string> Folders { get; set; } = new List<string>();

        public bool IsVideo => Array.Exists(Constants.Constants.VideoExtensions,
            ext => FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
