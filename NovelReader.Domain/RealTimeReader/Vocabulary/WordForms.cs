using System.Text.RegularExpressions;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Domain.RealTimeReader.Vocabulary
{
	/// <summary>
	/// English inflection, both ways, for matching a saved word wherever it turns up (D39).
	///
	/// The direction that matters is *forward*: from a base form, generate the forms it takes —
	/// `sneer` → `sneers, sneered, sneering`. Forward generation is exact where a stemmer guesses.
	/// A stemmer cuts `general` and `generous` to the same `gener` and underlines words the
	/// reader never saved. Going backwards, from `hoping` to `hope`, is only used to find the base
	/// form of a saved word when the dictionary does not name it. Every candidate base is checked
	/// by generating its forms forward and requiring the saved word among them, which is what
	/// tells `hoping` (→ `hope`) from `hopping` (→ `hop`).
	///
	/// Rules plus one small table, no packages: Domain stays dependency-free.
	/// </summary>
	public static partial class WordForms
	{
		/// <summary>
		/// Shortest base the suffix rules may leave behind — `sing` is not `s` + `ing`. The
		/// irregular table is exempt, which is how `go` and `do` still have forms.
		/// </summary>
		private const int MinimumBaseLength = 3;

		/// <summary>
		/// Irregular verbs: base, simple past, past participle. Slashes separate alternatives.
		/// Only the common ones; a missing verb still matches its regular-looking forms and its
		/// saved spelling, and adding one is a line here.
		/// </summary>
		private static readonly string[] IrregularVerbTable =
		[
			"arise arose arisen", "awake awoke awoken", "be was/were been", "bear bore borne/born",
			"beat beat beaten", "become became become", "begin began begun", "bend bent bent",
			"bet bet bet", "bid bid bid", "bind bound bound", "bite bit bitten", "bleed bled bled",
			"blow blew blown", "break broke broken", "breed bred bred", "bring brought brought",
			"build built built", "burn burnt/burned burnt/burned", "burst burst burst", "buy bought bought",
			"cast cast cast", "catch caught caught", "choose chose chosen", "cling clung clung",
			"come came come", "cost cost cost", "creep crept crept", "cut cut cut", "deal dealt dealt",
			"dig dug dug", "do did done", "draw drew drawn", "dream dreamt/dreamed dreamt/dreamed",
			"drink drank drunk", "drive drove driven", "eat ate eaten", "fall fell fallen",
			"feed fed fed", "feel felt felt", "fight fought fought", "find found found", "flee fled fled",
			"fling flung flung", "fly flew flown", "forbid forbade forbidden", "forget forgot forgotten",
			"forgive forgave forgiven", "freeze froze frozen", "get got got/gotten", "give gave given",
			"go went gone", "grind ground ground", "grow grew grown", "hang hung/hanged hung/hanged",
			"have had had", "hear heard heard", "hide hid hidden", "hit hit hit", "hold held held",
			"hurt hurt hurt", "keep kept kept", "kneel knelt/kneeled knelt/kneeled", "know knew known",
			"lay laid laid", "lead led led", "lean leant/leaned leant/leaned", "leap leapt/leaped leapt/leaped",
			"learn learnt/learned learnt/learned", "leave left left", "lend lent lent", "let let let",
			"lie lay lain", "light lit/lighted lit/lighted", "lose lost lost", "make made made",
			"mean meant meant", "meet met met", "mistake mistook mistaken", "pay paid paid",
			"prove proved proven/proved", "put put put", "quit quit quit", "read read read", "rid rid rid",
			"ride rode ridden", "ring rang rung", "rise rose risen", "run ran run", "say said said",
			"see saw seen", "seek sought sought", "sell sold sold", "send sent sent", "set set set",
			"sew sewed sewn", "shake shook shaken", "shed shed shed", "shine shone shone", "shoot shot shot",
			"show showed shown", "shrink shrank shrunk", "shut shut shut", "sing sang sung", "sink sank sunk",
			"sit sat sat", "slay slew slain", "sleep slept slept", "slide slid slid", "sling slung slung",
			"slit slit slit", "smell smelt/smelled smelt/smelled", "speak spoke spoken",
			"speed sped sped", "spend spent spent", "spill spilt/spilled spilt/spilled", "spin spun spun",
			"spit spat spat", "split split split", "spoil spoilt/spoiled spoilt/spoiled", "spread spread spread",
			"spring sprang sprung", "stand stood stood", "steal stole stolen", "stick stuck stuck",
			"sting stung stung", "stink stank stunk", "stride strode stridden", "strike struck struck",
			"string strung strung", "strive strove striven", "swear swore sworn", "sweep swept swept",
			"swell swelled swollen", "swim swam swum", "swing swung swung", "take took taken",
			"teach taught taught", "tear tore torn", "tell told told", "think thought thought",
			"throw threw thrown", "thrust thrust thrust", "tread trod trodden", "understand understood understood",
			"wake woke woken", "wear wore worn", "weave wove woven", "weep wept wept", "win won won",
			"wind wound wound", "withdraw withdrew withdrawn", "wring wrung wrung", "write wrote written"
		];

		/// <summary>Irregular plurals, singular first.</summary>
		private static readonly string[] IrregularNounTable =
		[
			"man men", "woman women", "child children", "foot feet", "tooth teeth", "goose geese",
			"mouse mice", "person people", "ox oxen", "louse lice", "die dice"
		];

		/// <summary>Verbs in the table: their past forms come from it, never from the -ed rule.</summary>
		private static readonly HashSet<string> IrregularVerbs =
			[.. IrregularVerbTable.Select(row => row.Split(' ')[0])];

		/// <summary>Base form → its irregular forms (not including itself).</summary>
		private static readonly Dictionary<string, HashSet<string>> IrregularForms = BuildIrregularForms();

		/// <summary>Irregular form → the base forms it can belong to (`lay` is `lie` and `lay`).</summary>
		private static readonly Dictionary<string, List<string>> IrregularBases = BuildIrregularBases();

		private static Dictionary<string, HashSet<string>> BuildIrregularForms()
		{
			Dictionary<string, HashSet<string>> forms = new(StringComparer.Ordinal);
			foreach (string row in IrregularVerbTable.Concat(IrregularNounTable))
			{
				string[] cells = row.Split(' ');
				HashSet<string> set = forms.TryGetValue(cells[0], out HashSet<string>? existing) ? existing : [];
				foreach (string cell in cells.Skip(1))
				{
					foreach (string form in cell.Split('/'))
					{
						if (form != cells[0]) set.Add(form);
					}
				}

				forms[cells[0]] = set;
			}

			return forms;
		}

		private static Dictionary<string, List<string>> BuildIrregularBases()
		{
			Dictionary<string, List<string>> bases = new(StringComparer.Ordinal);
			foreach ((string baseForm, HashSet<string> forms) in IrregularForms)
			{
				foreach (string form in forms)
				{
					if (!bases.TryGetValue(form, out List<string>? list)) bases[form] = list = [];
					list.Add(baseForm);
				}
			}

			return bases;
		}

		/// <summary>
		/// Every form of a normalised term, the term itself first. For a phrase the first word
		/// is inflected and the rest kept: `give up` → `gave up`, `giving up`, … — phrasal verbs
		/// are what readers save, and their verb comes first.
		/// </summary>
		public static IReadOnlyCollection<string> FormsOf(string baseTerm)
		{
			if (string.IsNullOrEmpty(baseTerm)) return [];

			int space = baseTerm.IndexOf(' ');
			string head = space < 0 ? baseTerm : baseTerm[..space];
			string tail = space < 0 ? string.Empty : baseTerm[space..];

			HashSet<string> forms = [baseTerm];
			foreach (string form in WordFormsOf(head))
			{
				forms.Add(form + tail);
			}

			return forms;
		}

		/// <summary>
		/// The base form a dictionary entry gives a saved term, or null when there is no entry.
		///
		/// The entry's main sense — its first ordinary one — decides how far to trust it:
		/// <list type="bullet">
		/// <item>An inflection note in the main sense is the answer: `sneered` is "simple past and
		/// past participle of sneer", `tables` is "plural of table".</item>
		/// <item>A main sense that is a *noun* is a word in its own right, and a note further down
		/// is about something else: `evening` is a time of day first and "present participle of
		/// even" only later. Taking that would underline every "even" in the novel.</item>
		/// <item>Any other main sense — an adjective, say — carries the verb's meaning, so a note
		/// further down is taken: `sneering` is "expressing contempt" first and "present participle
		/// of sneer" after.</item>
		/// </list>
		/// "Ordinary" because Wiktionary opens some entries with a code or an abbreviation — `ran`
		/// is first "ISO 639-3 language code for Riantana", and only then "simple past of run" —
		/// and those are not the word the reader met. The named base is also only taken when it
		/// really generates the saved word. An entry with no such note means the word *is* its
		/// own base: `seed`, `need` and `saw` are words, not `see` + d, `nee` + d, or the past of
		/// `see`.
		/// </summary>
		public static string? BaseFromDictionary(string term, WordDefinition? definition)
		{
			if (string.IsNullOrEmpty(term) || definition is not { HasSenses: true }) return null;

			int space = term.IndexOf(' ');
			string head = space < 0 ? term : term[..space];
			string tail = space < 0 ? string.Empty : term[space..];

			DefinitionSense main = definition.Senses.FirstOrDefault(sense => !IsNotAWord(sense.PartOfSpeech))
				?? definition.Senses[0];

			string? fromMain = BaseNamedBy(main, head);
			if (fromMain is not null) return fromMain + tail;

			if (string.Equals(main.PartOfSpeech, "Noun", StringComparison.OrdinalIgnoreCase)) return term;

			foreach (DefinitionSense sense in definition.Senses)
			{
				string? named = BaseNamedBy(sense, head);
				if (named is not null) return named + tail;
			}

			return term;
		}

		/// <summary>The base a sense's inflection note names, if it really generates the word.</summary>
		private static string? BaseNamedBy(DefinitionSense sense, string word)
		{
			Match match = InflectionNote.Match(sense.Text);
			if (!match.Success) return null;

			string candidate = TermNormalizer.Normalize(match.Groups[1].Value);
			return candidate.Length > 0 && candidate != word && WordFormsOf(candidate).Contains(word)
				? candidate
				: null;
		}

		/// <summary>Parts of speech that are codes, abbreviations or fragments rather than the word.</summary>
		private static bool IsNotAWord(string? partOfSpeech) =>
			partOfSpeech is not null
			&& (partOfSpeech.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Abbreviation", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Initialism", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Acronym", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Letter", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Prefix", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Suffix", StringComparison.OrdinalIgnoreCase)
				|| partOfSpeech.Equals("Proper noun", StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// Every base the suffix rules can find for a term that no dictionary has defined — the
		/// term itself among them. There is no single answer without a lexicon (`debating` is
		/// `debat` + ing or `debate` - e + ing; `agreed` is `agre` + ed or `agree` + d), so every
		/// candidate whose own forms include the term is kept, and the term is matched in the
		/// forms of all of them. The wrong ones are not words, so their forms never occur in text.
		/// </summary>
		public static IReadOnlyCollection<string> CandidateBasesOf(string term)
		{
			if (string.IsNullOrEmpty(term)) return [];

			int space = term.IndexOf(' ');
			string head = space < 0 ? term : term[..space];
			string tail = space < 0 ? string.Empty : term[space..];

			HashSet<string> bases = [term];

			// A base in its own right stays one — `lay` is a verb, not only the past of `lie` —
			// but its other readings count too.
			if (IrregularBases.TryGetValue(head, out List<string>? irregular))
			{
				foreach (string baseForm in irregular) bases.Add(baseForm + tail);
			}

			foreach (string candidate in CandidateBases(head))
			{
				if (candidate.Length >= MinimumBaseLength
					&& candidate != head
					&& WordFormsOf(candidate).Contains(head))
				{
					bases.Add(candidate + tail);
				}
			}

			return bases;
		}

		/// <summary>
		/// "past participle of sneer", "plural of fang", "present participle and gerund of run"…
		/// The word after "of" in a sense that reads like an inflection note.
		/// </summary>
		[GeneratedRegex(
			@"\b(?:plural|past|participle|gerund|present|third[- ]person|singular|comparative|superlative|inflection|tense)\b[^.;:]*?\bof\s+(?:the\s+(?:verb|noun|adjective)\s+)?[""“']?([a-z][a-z'’-]*)",
			RegexOptions.IgnoreCase)]
		private static partial Regex InflectionNote { get; }

		private static IEnumerable<string> CandidateBases(string word)
		{
			foreach (string suffix in (string[])["ing", "ed", "est", "er"])
			{
				if (!word.EndsWith(suffix, StringComparison.Ordinal)) continue;

				string stem = word[..^suffix.Length];
				if (stem.Length == 0) continue;

				// dy-ing → die, ly-ing → lie, ty-ing → tie: only ever a two-letter stem, or `playing`
				// would be tried as `plaie` first.
				if (suffix == "ing" && stem.Length == 2 && stem[^1] == 'y') yield return stem[..^1] + "ie";

				yield return stem;                                       // sneer-ing
				yield return stem + "e";                                 // hop-ing → hope
				if (stem.Length >= 2 && stem[^1] == stem[^2]) yield return stem[..^1]; // stopp-ing → stop
				if (stem.EndsWith('i')) yield return stem[..^1] + "y";   // tri-ed → try
				if (suffix == "ed" && stem.EndsWith("ck", StringComparison.Ordinal)) yield return stem[..^1]; // panick-ed
			}

			if (word.EndsWith("ies", StringComparison.Ordinal)) yield return word[..^3] + "y";
			if (word.EndsWith("ves", StringComparison.Ordinal))
			{
				yield return word[..^3] + "f";
				yield return word[..^3] + "fe";
			}
			if (word.EndsWith("es", StringComparison.Ordinal)) yield return word[..^2];
			if (word.EndsWith('s')) yield return word[..^1];
		}

		/// <summary>The forms of a single word, itself included.</summary>
		private static HashSet<string> WordFormsOf(string word)
		{
			HashSet<string> forms = [word];
			if (IrregularForms.TryGetValue(word, out HashSet<string>? irregular))
			{
				forms.UnionWith(irregular);
			}

			bool isIrregularVerb = IrregularVerbs.Contains(word);
			if (!word.All(character => char.IsLetter(character) || character is '-' or '\'' or '’')
				|| (word.Length < MinimumBaseLength && irregular is null))
			{
				return forms;
			}

			// -s: the plural and the third person. Both endings for -o (heroes, pianos), and the
			// -f/-fe plural alongside the plain one (leaves, chiefs), because both occur.
			if (word.EndsWith('y') && !IsVowel(word[^2]))
			{
				forms.Add(word[..^1] + "ies");
			}
			else if (EndsInSibilant(word))
			{
				forms.Add(word + "es");
			}
			else
			{
				forms.Add(word + "s");
				if (word.EndsWith('o')) forms.Add(word + "es");
			}

			if (word.EndsWith("fe", StringComparison.Ordinal)) forms.Add(word[..^2] + "ves");
			else if (word.EndsWith('f')) forms.Add(word[..^1] + "ves");

			// An irregular verb's past is in the table, and a regular one made up for it is not
			// harmless: `be` → `bed`, `see` → `seed`, `sing` → `singed`.
			if (!isIrregularVerb)
			{
				foreach (string stem in VerbStems(word))
				{
					forms.Add(stem.EndsWith('e') ? stem + "d" : stem + "ed");
				}
			}

			foreach (string stem in IngStems(word))
			{
				forms.Add(stem + "ing");
			}

			// Comparatives only for -y adjectives (happy → happier, happiest). A plain -er/-est
			// lands on unrelated words far too often — corn/corner, show/shower, flow/flower,
			// cove/cover, for/forest — so `darker` does not match `dark`, on purpose.
			if (irregular is null && word.EndsWith('y') && !IsVowel(word[^2]))
			{
				forms.Add(word[..^1] + "ier");
				forms.Add(word[..^1] + "iest");
			}

			return forms;
		}

		/// <summary>The stems -ed attaches to: try → tri(ed), stop → stopp(ed), hope → hope(d).</summary>
		private static IEnumerable<string> VerbStems(string word)
		{
			if (word.EndsWith('y') && !IsVowel(word[^2]))
			{
				yield return word[..^1] + "i";
				yield break;
			}

			if (word.EndsWith('c'))
			{
				yield return word + "k";                                 // panic → panicked
				yield break;
			}

			foreach (string stem in DoublingStems(word)) yield return stem;
		}

		/// <summary>The stems -ing attaches to: hope → hop(ing), die → dy(ing), stop → stopp(ing).</summary>
		private static IEnumerable<string> IngStems(string word)
		{
			if (word.EndsWith("ie", StringComparison.Ordinal))
			{
				yield return word[..^2] + "y";
				yield break;
			}

			if (word.EndsWith('e') && !word.EndsWith("ee", StringComparison.Ordinal)
				&& !word.EndsWith("ye", StringComparison.Ordinal) && !word.EndsWith("oe", StringComparison.Ordinal))
			{
				yield return word[..^1];
				yield break;
			}

			if (word.EndsWith('c'))
			{
				yield return word + "k";
				yield break;
			}

			foreach (string stem in DoublingStems(word)) yield return stem;
		}

		/// <summary>
		/// A final consonant after a single vowel doubles: stop → stopp-. For a one-syllable word
		/// that is the only form — which is what tells `hopping` (hop) from `hoping` (hope). A
		/// longer word doubles only when its last syllable is stressed (admitted, but visited),
		/// which spelling cannot tell, so both are generated; the wrong one never occurs in text.
		/// </summary>
		private static IEnumerable<string> DoublingStems(string word)
		{
			bool doubles = word.Length >= 3
				&& !IsVowel(word[^1]) && word[^1] is not ('w' or 'x' or 'y')
				&& IsVowel(word[^2])
				&& !IsVowel(word[^3]);

			if (!doubles)
			{
				yield return word;
				yield break;
			}

			yield return word + word[^1];
			if (VowelGroups(word) > 1) yield return word;
		}

		private static bool EndsInSibilant(string word) =>
			word.EndsWith('s') || word.EndsWith('x') || word.EndsWith('z')
			|| word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal);

		private static bool IsVowel(char character) => character is 'a' or 'e' or 'i' or 'o' or 'u';

		private static int VowelGroups(string word)
		{
			int groups = 0;
			bool inVowel = false;
			foreach (char character in word)
			{
				bool vowel = IsVowel(character) || character == 'y';
				if (vowel && !inVowel) groups++;
				inVowel = vowel;
			}

			return groups;
		}
	}
}
