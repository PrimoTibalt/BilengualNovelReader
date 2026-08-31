using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	public class PasswordHasherTests
	{
		[Fact]
		public void A_password_verifies_against_its_own_hash()
		{
			string hash = PasswordHasher.Hash("correct horse battery");

			Assert.True(PasswordHasher.Verify("correct horse battery", hash));
		}

		[Fact]
		public void A_different_password_does_not_verify()
		{
			string hash = PasswordHasher.Hash("correct horse battery");

			Assert.False(PasswordHasher.Verify("correct horse batteries", hash));
			Assert.False(PasswordHasher.Verify("", hash));
		}

		[Fact]
		public void The_same_password_hashes_differently_every_time()
		{
			// A shared salt would let one cracked password reveal every account using it.
			string first = PasswordHasher.Hash("same password");
			string second = PasswordHasher.Hash("same password");

			Assert.NotEqual(first, second);
			Assert.True(PasswordHasher.Verify("same password", first));
			Assert.True(PasswordHasher.Verify("same password", second));
		}

		[Fact]
		public void The_hash_carries_its_own_parameters()
		{
			string[] parts = PasswordHasher.Hash("whatever").Split('$');

			Assert.Equal(4, parts.Length);
			Assert.Equal("pbkdf2-sha256", parts[0]);
			Assert.True(int.Parse(parts[1]) >= 600_000);
		}

		[Theory]
		[InlineData("")]
		[InlineData("not-a-hash")]
		[InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
		[InlineData("pbkdf2-sha256$1000$not-base64!$aGFzaA==")]
		[InlineData("bcrypt$1000$c2FsdA==$aGFzaA==")]
		public void A_malformed_stored_hash_fails_rather_than_throws(string stored)
		{
			Assert.False(PasswordHasher.Verify("anything", stored));
		}
	}
}
