using System.Security.Cryptography;
using System.Text;

namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Sign-up needs an invite (D42). A code is twelve characters from an alphabet with no
	/// look-alikes (no 0/O, 1/I/L), shown in groups of four, used once, and valid for a day.
	///
	/// Only its SHA-256 is stored: a copy of the database must not be a stack of usable
	/// invitations. A plain hash is enough — the code is random, not a password a person chose,
	/// so there is nothing for a slow hash to protect.
	/// </summary>
	public static class InviteCodes
	{
		public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

		private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
		private const int Length = 12;
		private const int GroupSize = 4;

		/// <summary>A new code, as the person will type it: <c>K7QM-X2PD-9RTA</c>.</summary>
		public static string Generate()
		{
			string code = RandomNumberGenerator.GetString(Alphabet, Length);
			return string.Join('-', Enumerable.Range(0, Length / GroupSize).Select(group => code.Substring(group * GroupSize, GroupSize)));
		}

		/// <summary>
		/// What is stored and looked up. Case, dashes and spaces are forgiven, so a code read out
		/// over the phone or pasted with a stray space still works. Null for anything that cannot
		/// be a code, which then needs no lookup at all.
		/// </summary>
		public static string? Hash(string? code)
		{
			string normalized = new((code ?? string.Empty)
				.Where(character => character is not ('-' or ' '))
				.Select(char.ToUpperInvariant)
				.ToArray());

			if (normalized.Length != Length || normalized.Any(character => !Alphabet.Contains(character)))
			{
				return null;
			}

			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
		}
	}
}
