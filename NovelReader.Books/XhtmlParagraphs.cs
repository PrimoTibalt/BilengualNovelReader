using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace NovelReader.Books
{
	/// <summary>A picture as a content document refers to it: its path inside the archive.</summary>
	internal sealed record RawImage(string Path, string? Alt);

	/// <summary>
	/// A paragraph straight out of a content document, before its pictures are looked up: runs of
	/// text (<see cref="string"/>) and pictures (<see cref="RawImage"/>), in order.
	/// </summary>
	internal sealed class RawParagraph(IReadOnlyList<object> parts, bool isHeading)
	{
		public IReadOnlyList<object> Parts { get; } = parts;
		public bool IsHeading { get; } = isHeading;
		public string Text => string.Concat(Parts.OfType<string>());
		public bool HasImages => Parts.Any(part => part is RawImage);
	}

	/// <summary>
	/// Reads a content document (XHTML) into paragraphs (D50).
	///
	/// Every block element — a paragraph, a heading, a list item, a div with its own text — ends
	/// one paragraph and starts the next, and a line break does too, so verse keeps its lines.
	/// Inline elements (emphasis, the spans e-book tools wrap every sentence in, links) only
	/// contribute their text: the reader's column is plain text with saved words marked up (D3),
	/// so the book's own styling is not carried over. Pictures — <c>&lt;img&gt;</c>, or an SVG
	/// <c>&lt;image&gt;</c> as covers are often drawn — stay where they are in the text, and a
	/// picture standing on its own becomes a paragraph of its own. A rule (<c>&lt;hr&gt;</c>)
	/// becomes the run of stars the page already draws as a scene break (D34).
	/// </summary>
	internal static partial class XhtmlParagraphs
	{
		public const string SceneBreak = "* * *";

		private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
		{
			"head", "title", "script", "style", "noscript", "template", "iframe", "object", "embed",
			"video", "audio", "math", "button", "input", "select", "textarea",
			// Ruby annotations: the reading above the word, and the brackets for readers without ruby.
			"rt", "rp",
		};

		private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
		{
			"html", "body", "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "dl",
			"dt", "dd", "blockquote", "section", "article", "header", "footer", "aside", "nav",
			"figure", "figcaption", "pre", "table", "thead", "tbody", "tfoot", "tr", "td", "th",
			"caption", "address", "center", "main", "hgroup", "details", "summary",
		};

		private static readonly HashSet<string> Headings = new(StringComparer.Ordinal) { "h1", "h2", "h3", "h4", "h5", "h6" };

		/// <summary>Deeper than this, nesting is no longer followed (it is not a book any more).</summary>
		private const int MaximumDepth = 256;

		[GeneratedRegex(@"\s+")]
		private static partial Regex Whitespace { get; }

		/// <param name="resolve">Turns an <c>src</c> into a path in the archive, or null for one that is not in it.</param>
		public static List<RawParagraph> Read(HtmlDocument document, Func<string?, string?> resolve)
		{
			HtmlNode root = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
			Collector collector = new(resolve);
			collector.Walk(root, 0);
			collector.Flush();
			return collector.Paragraphs;
		}

		private sealed class Collector(Func<string?, string?> resolve)
		{
			public List<RawParagraph> Paragraphs { get; } = [];

			private readonly List<object> parts = [];
			private readonly StringBuilder text = new();
			private int headingDepth;

			public void Walk(HtmlNode node, int depth)
			{
				switch (node.NodeType)
				{
					case HtmlNodeType.Text:
						text.Append(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text));
						return;
					case HtmlNodeType.Element:
						break;
					default:
						if (node.NodeType == HtmlNodeType.Document)
						{
							WalkChildren(node, depth);
						}

						return;
				}

				string name = node.Name.ToLowerInvariant();
				if (Skipped.Contains(name) || depth > MaximumDepth)
				{
					return;
				}

				switch (name)
				{
					case "br":
						Flush();
						return;
					case "hr":
						Flush();
						Paragraphs.Add(new RawParagraph([SceneBreak], isHeading: false));
						return;
					case "img":
						AddImage(node.GetAttributeValue("src", null), node.GetAttributeValue("alt", null));
						return;
					case "image":
						AddSvgImage(node);
						return;
					case "svg":
						// Only its pictures: an SVG's text is drawing, not prose.
						foreach (HtmlNode image in node.Descendants("image"))
						{
							AddSvgImage(image);
						}

						return;
				}

				bool block = Blocks.Contains(name);
				bool heading = Headings.Contains(name);
				if (block)
				{
					Flush();
				}

				if (heading)
				{
					headingDepth++;
				}

				WalkChildren(node, depth);

				if (block)
				{
					Flush();
				}

				if (heading)
				{
					headingDepth--;
				}
			}

			private void WalkChildren(HtmlNode node, int depth)
			{
				foreach (HtmlNode child in node.ChildNodes)
				{
					Walk(child, depth + 1);
				}
			}

			private void AddSvgImage(HtmlNode image)
			{
				string? href = image.GetAttributeValue("xlink:href", null) ?? image.GetAttributeValue("href", null);
				AddImage(href, null);
			}

			private void AddImage(string? source, string? alt)
			{
				string? path = resolve(source);
				if (path is null)
				{
					return;
				}

				PushText();
				parts.Add(new RawImage(path, string.IsNullOrWhiteSpace(alt) ? null : Whitespace.Replace(HtmlEntity.DeEntitize(alt), " ").Trim()));
			}

			private void PushText()
			{
				if (text.Length > 0)
				{
					parts.Add(text.ToString());
					text.Clear();
				}
			}

			/// <summary>
			/// Ends the paragraph being built: whitespace collapsed as a browser would, trimmed at
			/// both ends, and a paragraph that is only pictures split into one per picture.
			/// </summary>
			public void Flush()
			{
				PushText();
				if (parts.Count == 0)
				{
					return;
				}

				List<object> cleaned = [];
				foreach (object part in parts)
				{
					if (part is string run)
					{
						string collapsed = Whitespace.Replace(run, " ");
						if (cleaned.Count > 0 && cleaned[^1] is string previous)
						{
							cleaned[^1] = previous + collapsed;
						}
						else
						{
							cleaned.Add(collapsed);
						}
					}
					else
					{
						cleaned.Add(part);
					}
				}

				parts.Clear();

				if (cleaned[0] is string first)
				{
					cleaned[0] = first.TrimStart();
				}

				if (cleaned[^1] is string last)
				{
					cleaned[^1] = last.TrimEnd();
				}

				cleaned.RemoveAll(part => part is string { Length: 0 });
				if (cleaned.Count == 0)
				{
					return;
				}

				bool onlyPictures = cleaned.All(part => part is RawImage || (part is string run && string.IsNullOrWhiteSpace(run)));
				if (onlyPictures)
				{
					foreach (RawImage image in cleaned.OfType<RawImage>())
					{
						Paragraphs.Add(new RawParagraph([image], isHeading: false));
					}

					return;
				}

				Paragraphs.Add(new RawParagraph(cleaned, headingDepth > 0));
			}
		}
	}
}
