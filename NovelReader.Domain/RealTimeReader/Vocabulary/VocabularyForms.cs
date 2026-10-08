namespace NovelReader.Domain.RealTimeReader.Vocabulary
{
	/// <summary>
	/// A reader's saved words, opened out into every form they take, each mapped back to the
	/// word as saved (D39). `run` saved → `runs`, `ran`, `running` … all → `run`, so the
	/// underline on `running` opens the definition of what the reader actually kept.
	/// </summary>
	public sealed class VocabularyForms
	{
		private readonly Dictionary<string, string> _savedTermByForm = new(StringComparer.Ordinal);

		public VocabularyForms(IEnumerable<VocabularyEntry> entries)
		{
			List<VocabularyEntry> list = [.. entries.Where(entry => entry.NormalizedTerm.Length > 0)];

			// A word saved as itself beats any other saved word that happens to generate it: with
			// both `run` and `running` saved, `running` is its own entry.
			foreach (VocabularyEntry entry in list)
			{
				_savedTermByForm[entry.NormalizedTerm] = entry.NormalizedTerm;
			}

			foreach (VocabularyEntry entry in list)
			{
				foreach (string form in FormsOf(entry))
				{
					_savedTermByForm.TryAdd(form, entry.NormalizedTerm);
				}
			}
		}

		/// <summary>Form → saved term, for the markup builder.</summary>
		public IReadOnlyDictionary<string, string> SavedTermByForm => _savedTermByForm;

		/// <summary>The saved term a normalised word belongs to, or null when it is not one of them.</summary>
		public string? SavedTermFor(string normalizedTerm) =>
			_savedTermByForm.GetValueOrDefault(normalizedTerm);

		/// <summary>
		/// Every form one entry is matched in, itself included: the forms of the base the
		/// dictionary gave it, or — for a word no dictionary knew, or one saved before base forms
		/// were kept — the forms of every base the suffix rules can find.
		/// </summary>
		public static IReadOnlyCollection<string> FormsOf(VocabularyEntry entry)
		{
			IReadOnlyCollection<string> bases = entry.BaseForm is not null
				? [entry.BaseForm]
				: WordForms.CandidateBasesOf(entry.NormalizedTerm);

			HashSet<string> forms = [entry.NormalizedTerm];
			foreach (string baseForm in bases)
			{
				forms.UnionWith(WordForms.FormsOf(baseForm));
			}

			return forms;
		}
	}
}
