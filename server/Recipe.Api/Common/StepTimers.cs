using System.Text.RegularExpressions;

namespace Recipe.Api.Common;

/// <summary>
/// Finds the timers hiding in recipe steps.
/// </summary>
/// <remarks>
/// Cooking mode needs these: "simmer for 20 minutes" should offer a 20-minute timer rather
/// than making someone set one by hand with wet fingers. Parsed here rather than asked of
/// the model, because a regular expression is free, instant, and cannot invent a duration
/// that was never written.
/// </remarks>
public static partial class StepTimers
{
    /// <summary>
    /// A duration with its unit. Ranges are common — "4 to 5 minutes", "8-10 mins" — and
    /// the longer bound is what gets used, since under-cooking is the worse mistake.
    /// </summary>
    [GeneratedRegex(
        @"(\d+(?:\.\d+)?)\s*(?:(?:-|–|—|\s+to\s+|\s+or\s+)\s*(\d+(?:\.\d+)?))?\s*"
        + @"(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Duration { get; }

    /// <summary>
    /// Phrases that mean "not a timer". Without this, "preheat to 200 degrees for 10
    /// minutes" and "cook for 2 hours or until tender" both produce misleading timers, and
    /// an oven temperature like "350 F" can look like a duration to a loose pattern.
    /// </summary>
    [GeneratedRegex(@"\b(degrees?|°|fahrenheit|celsius|\bF\b|\bC\b)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Temperature { get; }

    /// <param name="Seconds">Duration in seconds. The upper bound when the step gave a range.</param>
    /// <param name="Label">The phrase it was found in, so the UI can say what the timer is for.</param>
    public record Timer(int Seconds, string Label);

    /// <summary>
    /// Returns the timers in one step, in the order they appear.
    /// </summary>
    /// <remarks>
    /// A step can legitimately hold more than one — "sear 3 minutes a side, then rest 10
    /// minutes" is two timers, and collapsing them to one loses the resting.
    /// </remarks>
    public static IReadOnlyList<Timer> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var timers = new List<Timer>();

        foreach (Match match in Duration.Matches(text))
        {
            // Skip anything sitting next to a temperature: "350°F for 20 minutes" has a
            // real timer, but "350 F" itself is not one.
            var window = Window(text, match.Index, match.Length);

            if (Temperature.IsMatch(window) && !HasTimeWord(match.Groups[3].Value))
            {
                continue;
            }

            if (!double.TryParse(match.Groups[1].Value, out var low))
            {
                continue;
            }

            // The upper bound of a range: finishing early is recoverable, under-cooking
            // chicken is not.
            var value = match.Groups[2].Success && double.TryParse(match.Groups[2].Value, out var high)
                ? Math.Max(low, high)
                : low;

            var seconds = (int)Math.Round(value * SecondsPerUnit(match.Groups[3].Value));

            // A "0 minute" timer is a parse artefact, and anything over six hours in a
            // short-form recipe is almost certainly a misread.
            if (seconds is > 0 and <= 6 * 60 * 60)
            {
                timers.Add(new Timer(seconds, match.Value.Trim()));
            }
        }

        return timers;
    }

    private static bool HasTimeWord(string unit) =>
        unit.StartsWith("min", StringComparison.OrdinalIgnoreCase)
        || unit.StartsWith("hour", StringComparison.OrdinalIgnoreCase)
        || unit.StartsWith("hr", StringComparison.OrdinalIgnoreCase)
        || unit.StartsWith("sec", StringComparison.OrdinalIgnoreCase);

    private static string Window(string text, int index, int length)
    {
        var start = Math.Max(0, index - 12);
        var end = Math.Min(text.Length, index + length + 12);
        return text[start..end];
    }

    private static double SecondsPerUnit(string unit) => unit.ToLowerInvariant() switch
    {
        "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
        "s" or "sec" or "secs" or "second" or "seconds" => 1,
        // "m" is ambiguous in principle but means minutes in every recipe.
        _ => 60
    };
}
