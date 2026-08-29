using System.Text;

namespace Recipe.Api.Common;

/// <summary>
/// Repairs and merges caption text coming out of platform exports. Both behaviours here
/// are responses to quirks confirmed against real export files — see the root README.
/// </summary>
public static class CaptionText
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Deduplicates captions, repairs each, and joins what remains with a blank line.
    /// Returns null when nothing usable survives.
    /// </summary>
    /// <remarks>
    /// Instagram repeats a caption verbatim across carousel slides — a sample export had
    /// 18 caption leaves against 14 posts, and every extra was byte-identical to its
    /// sibling. Joining blind would feed the extraction model the same text twice. Real
    /// carousels can still split ingredients and steps across slides, so distinct
    /// captions are all kept, in order.
    /// </remarks>
    public static string? Normalise(IEnumerable<string>? captions)
    {
        if (captions is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var caption in captions)
        {
            if (string.IsNullOrWhiteSpace(caption))
            {
                continue;
            }

            var repaired = RepairMojibake(caption).Trim();

            if (repaired.Length > 0 && seen.Add(repaired))
            {
                kept.Add(repaired);
            }
        }

        return kept.Count == 0 ? null : string.Join("\n\n", kept);
    }

    /// <summary>
    /// Undoes the latin-1/utf-8 mis-decoding Instagram applies to caption text, where a
    /// gem emoji arrives as a run of mangled latin-1 characters.
    /// </summary>
    /// <remarks>
    /// Not optional: 12 of 18 captions in the sample export were affected. It mangles
    /// smart quotes as well as emoji, and smart quotes sit inside recipe words. Returns
    /// the input unchanged when it is already clean — the round trip only succeeds on
    /// text that genuinely is mis-decoded utf-8.
    /// </remarks>
    public static string RepairMojibake(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Latin-1 substitutes '?' for anything above U+00FF, which would corrupt text
        // that is already correct. Only attempt the repair when every char round-trips.
        foreach (var c in text)
        {
            if (c > 0xFF)
            {
                return text;
            }
        }

        try
        {
            return StrictUtf8.GetString(Encoding.Latin1.GetBytes(text));
        }
        catch (DecoderFallbackException)
        {
            // Not mis-decoded utf-8 after all; the original was correct.
            return text;
        }
    }
}
