using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaManager.Models
{
    /// <summary>
    /// Password hashes for Site.config (LoginPasswordHash / GuestPasswordHash).
    ///
    /// Current format (made by deploy\New-PasswordHash.ps1):
    ///     pbkdf2-sha256$&lt;iterations&gt;$&lt;salt, base64&gt;$&lt;hash, base64&gt;
    /// PBKDF2 is deliberately slow and salted, so a copied hash can't be cracked quickly or looked up in a table.
    ///
    /// Older sites may still have a plain SHA-256 hex hash (64 hex characters). Those are still accepted
    /// so nobody gets locked out, but they're much weaker: replace them with a new hash when you can.
    /// </summary>
    public static class PasswordHasher
    {
        public const string Scheme = "pbkdf2-sha256";

        /// <summary>
        /// About 0.4 s per check on a typical PC with .NET Framework. Stored in each hash, so changing this
        /// only affects new hashes. Keep deploy\New-PasswordHash.ps1's default in step.
        /// </summary>
        public const int DefaultIterations = 210000;

        private const int SaltBytes = 16;
        private const int HashBytes = 32;

        private static readonly Regex LegacyHex = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

        /// <summary>A new PBKDF2 hash of the password, in the Site.config format above.</summary>
        public static string Hash(string password, int iterations = DefaultIterations)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password can't be empty.", nameof(password));

            var salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            {
                return string.Join("$", Scheme, iterations.ToString(CultureInfo.InvariantCulture),
                    Convert.ToBase64String(salt), Convert.ToBase64String(kdf.GetBytes(HashBytes)));
            }
        }

        /// <summary>True if the password matches the stored hash (either format). Never throws for a bad hash: just false.</summary>
        public static bool Verify(string password, string storedHash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash))
                return false;

            storedHash = storedHash.Trim();

            if (IsLegacy(storedHash))
            {
                using (var sha = SHA256.Create())
                {
                    var actual = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
                    return FixedTimeEquals(actual, FromHex(storedHash));
                }
            }

            var parts = storedHash.Split('$');
            if (parts.Length != 4 || parts[0] != Scheme)
                return false;

            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) || iterations < 1)
                return false;

            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            if (salt.Length < 8 || expected.Length == 0) // Rfc2898DeriveBytes needs at least 8 bytes of salt
                return false;

            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return FixedTimeEquals(kdf.GetBytes(expected.Length), expected);
        }

        /// <summary>True for an old-style plain SHA-256 hex hash.</summary>
        public static bool IsLegacy(string storedHash)
        {
            return storedHash != null && LegacyHex.IsMatch(storedHash.Trim());
        }

        /// <summary>Compares without stopping at the first difference, so timing doesn't reveal how much matched.</summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }
}
