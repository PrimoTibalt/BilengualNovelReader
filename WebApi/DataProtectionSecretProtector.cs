using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using NovelReader.Domain.RealTimeReader.Assistant;

namespace NovelReader
{
	/// <summary>
	/// Protects readers' API keys with ASP.NET Core Data Protection (D35). The keys that do
	/// the protecting are kept beside the accounts database — see
	/// <c>Data.Sqlite.ServiceCollectionExtension.AccountsDirectory</c> — so the two cannot be
	/// separated by a deploy.
	/// </summary>
	public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
	{
		/// <summary>Scopes the protection, so a value protected for anything else cannot be read here.</summary>
		private const string Purpose = "NovelReader.AssistantApiKey.v1";

		private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

		public string Protect(string secret) => _protector.Protect(secret);

		public string? Unprotect(string protectedSecret)
		{
			try
			{
				return _protector.Unprotect(protectedSecret);
			}
			catch (CryptographicException)
			{
				// The key ring it was protected with is gone. The reader enters the key again.
				return null;
			}
		}
	}
}
