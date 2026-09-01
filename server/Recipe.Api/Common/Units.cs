namespace Recipe.Api.Common;

/// <summary>What a unit measures. Only quantities of the same kind can be added together.</summary>
public enum UnitKind
{
    /// <summary>"2 eggs", "1 onion" — a count, not a measurement.</summary>
    Count,
    Volume,
    Mass
}

/// <summary>
/// Parsing, converting, and combining recipe quantities.
/// </summary>
/// <remarks>
/// Recipes are written by people, so the same amount appears as "tbsp", "Tbsp",
/// "tablespoons", and "T". Anything that merges ingredients across recipes has to
/// reconcile those, and has to refuse to reconcile the ones that genuinely cannot be —
/// 100g of butter and 2 tablespoons of butter are both butter, but adding them needs a
/// density table nobody has, so they stay as separate lines.
/// </remarks>
public static class Units
{
    /// <param name="Canonical">The name this unit normalises to.</param>
    /// <param name="Kind">What it measures.</param>
    /// <param name="InBase">
    /// How many base units one of these is — millilitres for volume, grams for mass.
    /// Everything is converted through the base so any two compatible units can be added.
    /// </param>
    private record UnitInfo(string Canonical, UnitKind Kind, double InBase);

    /// <summary>
    /// US customary volumes, since that is what short-form recipe video overwhelmingly uses.
    /// A UK tablespoon is 15ml either way; the cup is the one that differs, and this takes
    /// the US 240ml.
    /// </summary>
    private static readonly Dictionary<string, UnitInfo> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Volume
        ["ml"] = new("ml", UnitKind.Volume, 1),
        ["millilitre"] = new("ml", UnitKind.Volume, 1),
        ["milliliter"] = new("ml", UnitKind.Volume, 1),
        ["l"] = new("l", UnitKind.Volume, 1000),
        ["litre"] = new("l", UnitKind.Volume, 1000),
        ["liter"] = new("l", UnitKind.Volume, 1000),
        ["tsp"] = new("tsp", UnitKind.Volume, 4.93),
        ["t"] = new("tsp", UnitKind.Volume, 4.93),
        ["teaspoon"] = new("tsp", UnitKind.Volume, 4.93),
        ["teaspoons"] = new("tsp", UnitKind.Volume, 4.93),
        ["tbsp"] = new("tbsp", UnitKind.Volume, 14.79),
        ["tbs"] = new("tbsp", UnitKind.Volume, 14.79),
        ["tablespoon"] = new("tbsp", UnitKind.Volume, 14.79),
        ["tablespoons"] = new("tbsp", UnitKind.Volume, 14.79),
        ["cup"] = new("cup", UnitKind.Volume, 240),
        ["cups"] = new("cup", UnitKind.Volume, 240),
        ["fl oz"] = new("fl oz", UnitKind.Volume, 29.57),
        ["floz"] = new("fl oz", UnitKind.Volume, 29.57),
        ["pint"] = new("pint", UnitKind.Volume, 473),
        ["quart"] = new("quart", UnitKind.Volume, 946),

        // Mass
        ["g"] = new("g", UnitKind.Mass, 1),
        ["gram"] = new("g", UnitKind.Mass, 1),
        ["grams"] = new("g", UnitKind.Mass, 1),
        ["kg"] = new("kg", UnitKind.Mass, 1000),
        ["kilogram"] = new("kg", UnitKind.Mass, 1000),
        ["kilograms"] = new("kg", UnitKind.Mass, 1000),
        ["oz"] = new("oz", UnitKind.Mass, 28.35),
        ["ounce"] = new("oz", UnitKind.Mass, 28.35),
        ["ounces"] = new("oz", UnitKind.Mass, 28.35),
        ["lb"] = new("lb", UnitKind.Mass, 453.6),
        ["lbs"] = new("lb", UnitKind.Mass, 453.6),
        ["pound"] = new("lb", UnitKind.Mass, 453.6),
        ["pounds"] = new("lb", UnitKind.Mass, 453.6),
    };

    /// <summary>
    /// Units that describe a handful rather than a measurement.
    /// </summary>
    /// <remarks>
    /// Deliberately not converted or scaled. "A pinch of salt" doubled is still a pinch,
    /// and rendering it as "2 pinch" reads like a bug.
    /// </remarks>
    private static readonly HashSet<string> Vague = new(StringComparer.OrdinalIgnoreCase)
    {
        "pinch", "handful", "dash", "splash", "drizzle", "sprinkle", "to taste", "some"
    };

    public static bool IsVague(string? unit) => unit is not null && Vague.Contains(unit.Trim());

    /// <summary>Resolves a written unit to its canonical spelling, or null if unrecognised.</summary>
    public static string? Canonical(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return null;
        }

        return Known.TryGetValue(unit.Trim(), out var info) ? info.Canonical : unit.Trim();
    }

    public static UnitKind KindOf(string? unit) =>
        unit is not null && Known.TryGetValue(unit.Trim(), out var info) ? info.Kind : UnitKind.Count;

    /// <summary>
    /// Adds two amounts of the same ingredient.
    /// </summary>
    /// <returns>
    /// The combined amount, or null when they cannot honestly be combined — different kinds
    /// of measure, an unrecognised unit, or a missing quantity. A null here means "keep
    /// these as separate lines", which is the right answer far more often than a guess.
    /// </returns>
    public static (double Quantity, string? Unit)? TryAdd(
        double? leftQuantity, string? leftUnit, double? rightQuantity, string? rightUnit)
    {
        if (leftQuantity is not { } left || rightQuantity is not { } right)
        {
            return null;
        }

        var leftKey = leftUnit?.Trim();
        var rightKey = rightUnit?.Trim();

        // Both unitless — "2 eggs" plus "1 egg".
        if (string.IsNullOrEmpty(leftKey) && string.IsNullOrEmpty(rightKey))
        {
            return (left + right, null);
        }

        if (IsVague(leftKey) || IsVague(rightKey))
        {
            return null;
        }

        if (leftKey is null || rightKey is null
            || !Known.TryGetValue(leftKey, out var a) || !Known.TryGetValue(rightKey, out var b)
            || a.Kind != b.Kind)
        {
            // 100g of butter and 2 tbsp of butter are both butter, but adding them needs a
            // density this does not have. Two lines is honest; one wrong number is not.
            return null;
        }

        var total = left * a.InBase + right * b.InBase;

        // Report in the larger of the two units, so 1 kg + 500 g reads as 1.5 kg rather
        // than 1500 g.
        var target = a.InBase >= b.InBase ? a : b;

        return (Round(total / target.InBase), target.Canonical);
    }

    /// <summary>
    /// Multiplies an amount, keeping the result readable.
    /// </summary>
    /// <remarks>
    /// Vague units pass through untouched — a doubled pinch is still a pinch. Counts are
    /// rounded to a half, because "1.5 eggs" is usable and "1.33 eggs" is not.
    /// </remarks>
    public static (double? Quantity, string? Unit) Scale(double? quantity, string? unit, double factor)
    {
        if (quantity is not { } value || IsVague(unit))
        {
            return (quantity, unit);
        }

        var scaled = value * factor;

        if (string.IsNullOrWhiteSpace(unit))
        {
            return (Math.Round(scaled * 2, MidpointRounding.AwayFromZero) / 2, unit);
        }

        return (Round(scaled), unit);
    }

    /// <summary>
    /// Rounds to something a person would write: whole numbers when close, otherwise one or
    /// two decimals. "0.33 tsp" is fine; "0.3333333 tsp" is noise.
    /// </summary>
    private static double Round(double value) => value switch
    {
        >= 100 => Math.Round(value),
        >= 10 => Math.Round(value, 1),
        _ => Math.Round(value, 2)
    };
}
