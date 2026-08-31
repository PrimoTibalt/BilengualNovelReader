using System.Security.Cryptography;
using System.Text;

namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// PBKDF2-HMAC-SHA256 password hashing. Lives in Domain because it is arithmetic, not
	/// infrastructure, and needs nothing beyond the BCL — the zero-package rule is about
	/// NuGet, and <c>System.Security.Cryptography</c> is part of the framework.
	///
	/// The stored string carries everything needed to verify it — algorithm, iteration count
	/// and salt — so raising the cost later does not strand the accounts hashed before.
	/// </summary>
	public static class PasswordHasher
	{
		/// <summary>OWASP's floor for PBKDF2-HMAC-SHA256 at the time of writing.</summary>
		private const int Iterations = 600_000;
		private const int SaltSizeInBytes = 16;
		private const int HashSizeInBytes = 32;
		private const string Prefix = "pbkdf2-sha256";

		public static string Hash(string password)
		{
			ArgumentNullException.ThrowIfNull(password);

			byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeInBytes);
			byte[] hash = Derive(password, salt, Iterations);

			return string.Join('$', Prefix, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
		}

		/// <summary>
		/// Verifies a candidate against a stored hash. Any malformed stored value is a
		/// failure rather than an exception: a corrupt row must not take down sign-in.
		/// </summary>
		public static bool Verify(string password, string storedHash)
		{
			if (password is null || string.IsNullOrEmpty(storedHash))
			{
				return false;
			}

			string[] parts = storedHash.Split('$');
			if (parts.Length != 4 || parts[0] != Prefix)
			{
				return false;
			}

			if (!int.TryParse(parts[1], out int iterations) || iterations <= 0)
			{
				return false;
			}

			byte[] salt;
			byte[] expected;
			try
			{
				salt = Convert.FromBase64String(parts[2]);
				expected = Convert.FromBase64String(parts[3]);
			}
			catch (FormatException)
			{
				return false;
			}

			byte[] actual = Derive(password, salt, iterations, expected.Length);

			// Fixed-time comparison: a byte-by-byte early exit leaks the hash one byte at a time.
			return CryptographicOperations.FixedTimeEquals(actual, expected);
		}

		private static byte[] Derive(string password, byte[] salt, int iterations, int size = HashSizeInBytes)
		{
			return Rfc2898DeriveBytes.Pbkdf2(
				Encoding.UTF8.GetBytes(password),
				salt,
				iterations,
				HashAlgorithmName.SHA256,
				size);
		}
	}
}
