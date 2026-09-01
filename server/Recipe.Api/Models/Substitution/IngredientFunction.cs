namespace Recipe.Api.Models.Substitution;

/// <summary>
/// What an ingredient is *doing* in a dish, which is what actually governs whether
/// something can replace it.
/// </summary>
/// <remarks>
/// Substituting by identity is how you ruin dinner. Buttermilk is not "milk that tastes
/// sour" — it is acid plus dairy, and swapping plain milk for it removes the acid that
/// was reacting with the baking soda, so the thing does not rise. An egg is a binder, a
/// leavener, an emulsifier, or a wash depending on where it appears, and the right
/// replacement differs for each.
///
/// Values are pinned and are a wire contract — see server/CLAUDE.md.
/// </remarks>
public enum IngredientFunction
{
    Unknown = 0,

    /// <summary>Carries flavour, conducts heat, shortens gluten. Swaps change texture and smoke point.</summary>
    Fat = 1,

    /// <summary>Holds a mixture together. The failure mode is a dish that falls apart.</summary>
    Binder = 2,

    /// <summary>Makes it rise. Removing the acid half of an acid/base pair stops that.</summary>
    Leavener = 3,

    /// <summary>Brightens, tenderises, and reacts with bases.</summary>
    Acid = 4,

    /// <summary>The substantial part of the dish.</summary>
    Protein = 5,

    /// <summary>Milk solids. Distinct from fat: contributes browning and body.</summary>
    Dairy = 6,

    /// <summary>Sweetens, browns, retains moisture. Liquid and dry forms are not interchangeable.</summary>
    Sweetener = 7,

    /// <summary>Structure and thickening.</summary>
    Starch = 8,

    /// <summary>Flavour base — onion, garlic, ginger.</summary>
    Aromatic = 9,

    /// <summary>Seasoning and spice.</summary>
    Seasoning = 10,

    /// <summary>Liquid volume — stock, water, wine.</summary>
    Liquid = 11
}
