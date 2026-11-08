using System.Buffers.Binary;

namespace NovelReader.Books
{
	/// <summary>A picture format the reader serves, and the picture's size in pixels.</summary>
	internal sealed record SniffedImage(string Extension, int Width, int Height);

	/// <summary>
	/// Tells a picture's format and size from its first bytes (D50). The manifest's media type is
	/// not trusted — it is the uploader's word — and the size is what lets the page hold a
	/// picture's space before it loads.
	///
	/// JPEG, PNG, GIF and WebP only. SVG is left out on purpose: it is a document that can carry
	/// script, and it would be served from this origin.
	/// </summary>
	internal static class ImageSniffer
	{
		/// <summary>Anything wider or taller than this is not a picture in a book.</summary>
		private const int MaximumSide = 30_000;

		public static SniffedImage? Sniff(ReadOnlySpan<byte> bytes)
		{
			SniffedImage? sniffed = Png(bytes) ?? Jpeg(bytes) ?? Gif(bytes) ?? WebP(bytes);
			return sniffed is { Width: > 0 and <= MaximumSide, Height: > 0 and <= MaximumSide } ? sniffed : null;
		}

		private static SniffedImage? Png(ReadOnlySpan<byte> b)
		{
			ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
			if (b.Length < 24 || !b[..8].SequenceEqual(signature) || !b.Slice(12, 4).SequenceEqual("IHDR"u8))
			{
				return null;
			}

			return new SniffedImage("png", BinaryPrimitives.ReadInt32BigEndian(b[16..]), BinaryPrimitives.ReadInt32BigEndian(b[20..]));
		}

		private static SniffedImage? Gif(ReadOnlySpan<byte> b)
		{
			if (b.Length < 10 || !(b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)))
			{
				return null;
			}

			return new SniffedImage("gif", BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));
		}

		/// <summary>The size is in the first start-of-frame segment; everything before it is skipped by length.</summary>
		private static SniffedImage? Jpeg(ReadOnlySpan<byte> b)
		{
			if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
			{
				return null;
			}

			int index = 2;
			while (index + 9 < b.Length)
			{
				if (b[index] != 0xFF)
				{
					return null;
				}

				byte marker = b[index + 1];
				if (marker == 0xFF)
				{
					// Fill byte before a marker.
					index++;
					continue;
				}

				if (marker is 0x01 or >= 0xD0 and <= 0xD8)
				{
					// Markers without a length.
					index += 2;
					continue;
				}

				if (marker is 0xD9 or 0xDA)
				{
					// The end, or the image data, came before any frame header.
					return null;
				}

				int length = BinaryPrimitives.ReadUInt16BigEndian(b[(index + 2)..]);
				bool startOfFrame = marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
				if (startOfFrame)
				{
					int height = BinaryPrimitives.ReadUInt16BigEndian(b[(index + 5)..]);
					int width = BinaryPrimitives.ReadUInt16BigEndian(b[(index + 7)..]);
					return new SniffedImage("jpg", width, height);
				}

				if (length < 2)
				{
					return null;
				}

				index += 2 + length;
			}

			return null;
		}

		private static SniffedImage? WebP(ReadOnlySpan<byte> b)
		{
			if (b.Length < 30 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WEBP"u8))
			{
				return null;
			}

			ReadOnlySpan<byte> chunk = b.Slice(12, 4);
			if (chunk.SequenceEqual("VP8 "u8))
			{
				// Lossy: the frame header's 14-bit sizes, after the three-byte start code.
				return new SniffedImage(
					"webp",
					BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF,
					BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
			}

			if (chunk.SequenceEqual("VP8L"u8))
			{
				// Lossless: after the signature byte, 14 bits of width - 1 and 14 of height - 1.
				uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
				return new SniffedImage("webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
			}

			if (chunk.SequenceEqual("VP8X"u8))
			{
				// Extended: the canvas size, 24 bits each, minus one.
				int width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
				int height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
				return new SniffedImage("webp", width, height);
			}

			return null;
		}
	}
}
