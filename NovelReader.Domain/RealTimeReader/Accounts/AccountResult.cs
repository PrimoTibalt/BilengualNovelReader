namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>Why a sign-up or sign-in did not succeed.</summary>
	public enum AccountFailure
	{
		None = 0,
		UserNameTaken,
		UserNameInvalid,
		PasswordTooShort,
		UnknownUserOrPassword
	}

	/// <summary>
	/// The outcome of an account operation. Sign-in deliberately cannot distinguish "no such
	/// user" from "wrong password" — both come back as
	/// <see cref="AccountFailure.UnknownUserOrPassword"/>, so the endpoint cannot be used to
	/// find out which names are registered.
	/// </summary>
	public sealed class AccountResult
	{
		public required bool Succeeded { get; init; }
		public required AccountFailure Failure { get; init; }
		public string? UserName { get; init; }

		public static AccountResult Success(string userName) => new()
		{
			Succeeded = true,
			Failure = AccountFailure.None,
			UserName = userName
		};

		public static AccountResult Failed(AccountFailure failure) => new()
		{
			Succeeded = false,
			Failure = failure
		};

		/// <summary>Wording safe to show the reader; it never leaks which names exist.</summary>
		public string Message => Failure switch
		{
			AccountFailure.None => "OK",
			AccountFailure.UserNameTaken => "That username is already taken.",
			AccountFailure.UserNameInvalid =>
				"Usernames are 3–32 characters: letters, digits, dot, dash or underscore.",
			AccountFailure.PasswordTooShort => "Passwords must be at least 8 characters.",
			AccountFailure.UnknownUserOrPassword => "Unknown username or password.",
			_ => "Something went wrong."
		};
	}
}
