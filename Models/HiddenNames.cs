using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MediaManager.Models
{
    /// <summary>
    /// Site.config Feature:HideNames: links to folders and files carry a token instead of the folder and file
    /// names, so the names don't show in the address bar, browser history, bookmarks, shared links or server logs
    /// (the pages themselves still show them). Readable links keep working either way.
    ///
    /// A token is the path from the media root ("Videos/Cats/a.mp4", or a folder "Videos/Cats"), encrypted with
    /// this site's key. The same path always gives the same token, so bookmarks stay good and browsers can cache
    /// files and thumbnails; anyone without the key can neither read a token nor make one. (The IV is an HMAC of
    /// the path, checked again on the way back, as in SIV: a changed token never decodes to anything.) Renaming or
    /// moving a file changes its token, just as it changes a readable link.
    ///
    /// The key is made the first time it's needed and kept in the media folder (the site can't write to its own
    /// app folder), hidden, and never served (Filters/MediaAuthModule). Deleting it just makes a new one, which
    /// breaks bookmarked hidden links.
    /// </summary>
    public static class HiddenNames
    {
        public const string KeyFileName = ".hidden-names.key";

        private const int KeyBytes = 64; // 32 for the HMAC, 32 for AES-256
        private const int IvBytes = 16;

        public static bool Enabled => SiteSettings.HideNamesEnabled;

        private static readonly object keyLock = new object();
        private static byte[] key;

        /// <summary>Token for a path from the media root ("a/b/file.ext" or "a/b").</summary>
        public static string Encode(string relativePath)
        {
            byte[] plain = Encoding.UTF8.GetBytes(MediaPaths.NormalizeFolder(relativePath));
            byte[] k = Key;

            byte[] iv = new byte[IvBytes];
            using (var hmac = new HMACSHA256(Slice(k, 0, 32)))
                Buffer.BlockCopy(hmac.ComputeHash(plain), 0, iv, 0, IvBytes);

            byte[] cipher;
            using (var aes = NewAes(k, iv))
            using (var enc = aes.CreateEncryptor())
                cipher = enc.TransformFinalBlock(plain, 0, plain.Length);

            byte[] token = new byte[IvBytes + cipher.Length];
            Buffer.BlockCopy(iv, 0, token, 0, IvBytes);
            Buffer.BlockCopy(cipher, 0, token, IvBytes, cipher.Length);
            return ToBase64Url(token);
        }

        /// <summary>The path a token stands for ("a/b/file.ext" or "a/b"), or null if it isn't one of this site's tokens.</summary>
        public static string Decode(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 4096)
                return null;

            byte[] bytes = FromBase64Url(token);
            if (bytes == null || bytes.Length < IvBytes * 2 || (bytes.Length - IvBytes) % 16 != 0)
                return null;

            byte[] k = Key;
            byte[] iv = Slice(bytes, 0, IvBytes);
            byte[] plain;
            try
            {
                using (var aes = NewAes(k, iv))
                using (var dec = aes.CreateDecryptor())
                    plain = dec.TransformFinalBlock(bytes, IvBytes, bytes.Length - IvBytes);
            }
            catch (CryptographicException)
            {
                return null;
            }

            byte[] expected;
            using (var hmac = new HMACSHA256(Slice(k, 0, 32)))
                expected = hmac.ComputeHash(plain);

            int diff = 0;
            for (int i = 0; i < IvBytes; i++)
                diff |= expected[i] ^ iv[i];
            if (diff != 0)
                return null;

            try
            {
                return new UTF8Encoding(false, true).GetString(plain);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Folder and file from a file token, or false if it isn't a valid token. A file at the very top of the
        /// media folder has folder "".
        /// </summary>
        public static bool TryDecodeFile(string token, out string folder, out string file)
        {
            folder = file = null;
            string path = Decode(token);
            if (string.IsNullOrEmpty(path))
                return false;

            int slash = path.LastIndexOf('/');
            folder = slash < 0 ? "" : path.Substring(0, slash);
            file = path.Substring(slash + 1);
            return file.Length > 0;
        }

        private static Aes NewAes(byte[] k, byte[] iv)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Slice(k, 32, 32);
            aes.IV = iv;
            return aes;
        }

        /// <summary>This site's key, read from (or, the first time, written to) the media folder.</summary>
        private static byte[] Key
        {
            get
            {
                var k = key;
                if (k != null)
                    return k;

                lock (keyLock)
                {
                    if (key == null)
                        key = LoadOrCreateKey(Path.Combine(MediaPaths.Root, KeyFileName));
                    return key;
                }
            }
        }

        private static byte[] LoadOrCreateKey(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        byte[] existing = File.ReadAllBytes(path);
                        if (existing.Length == KeyBytes)
                            return existing;
                        // Damaged: a key can't be recovered, so start again (hidden links made with it stop working)
                        Trace.TraceWarning("The hidden-names key " + path + " was damaged, so a new one was made. Old hidden links won't open.");
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                    }

                    byte[] fresh = new byte[KeyBytes];
                    using (var rng = RandomNumberGenerator.Create())
                        rng.GetBytes(fresh);

                    // CreateNew: if another request (or the other worker) made one meanwhile, use theirs instead
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        stream.Write(fresh, 0, fresh.Length);
                    File.SetAttributes(path, FileAttributes.Hidden);
                    return fresh;
                }
                catch (IOException) when (attempt < 2)
                {
                    System.Threading.Thread.Sleep(50); // being written by someone else: read theirs
                }
            }
            throw new IOException("Couldn't read or create the hidden-names key " + path);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] FromBase64Url(string s)
        {
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_')
                    return null;

            string b64 = s.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 1: return null;
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }
            try
            {
                return Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
