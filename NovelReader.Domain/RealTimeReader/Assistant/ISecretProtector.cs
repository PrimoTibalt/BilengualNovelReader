namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// Encrypts a secret for storage. Declared here so the domain can insist that an API key is
	/// never stored in the clear without itself depending on how that is done.
	/// </summary>
	public interface ISecretProtector
	{
		string Protect(string secret);

		/// <summary>
		/// Null when the secret can no longer be read — the keys it was protected with are gone.
		/// The caller treats that as "not configured", and the reader enters the key again.
		/// </summary>
		string? Unprotect(string protectedSecret);
	}
}
