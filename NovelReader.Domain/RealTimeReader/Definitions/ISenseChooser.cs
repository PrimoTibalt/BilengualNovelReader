namespace NovelReader.Domain.RealTimeReader.Definitions
{
	/// <summary>
	/// Picks the dictionary sense a word has in the paragraph it was selected in (D47), so the
	/// definition box opens on it rather than on whatever the dictionary lists first.
	/// </summary>
	public interface ISenseChooser
	{
		/// <returns>
		/// The index into <paramref name="senses"/> of the sense meant, or null when none fits,
		/// the chooser is not sure, or it could not be asked.
		/// </returns>
		Task<int?> ChooseAsync(
			string term,
			string paragraph,
			IReadOnlyList<DefinitionSense> senses,
			CancellationToken cancellationToken = default);
	}
}
