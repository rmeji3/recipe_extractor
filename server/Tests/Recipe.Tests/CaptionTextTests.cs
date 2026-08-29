using Recipe.Api.Common;

namespace Recipe.Tests;

public class CaptionTextTests
{
    /// <summary>
    /// A gem emoji (U+1F48E) as it appears in the raw Instagram export: its utf-8 bytes
    /// read back as latin-1 characters. Written as escapes so the mojibake survives any
    /// re-encoding of this source file.
    /// </summary>
    private const string MangledGem = "\u00f0\u009f\u0092\u008e";

    /// <summary>A right single quote (U+2019) mis-decoded the same way.</summary>
    private const string MangledApostrophe = "\u00e2\u0080\u0099";

    [Fact]
    public void Normalise_drops_captions_repeated_across_carousel_slides()
    {
        // The sample Instagram export carried 18 caption leaves for 14 posts; every extra
        // was byte-identical to its sibling.
        var result = CaptionText.Normalise(["Mix the flour.", "Mix the flour."]);

        Assert.Equal("Mix the flour.", result);
    }

    [Fact]
    public void Normalise_keeps_distinct_captions_in_order()
    {
        var result = CaptionText.Normalise(["Ingredients: 2 eggs", "Step 1: whisk"]);

        Assert.Equal("Ingredients: 2 eggs\n\nStep 1: whisk", result);
    }

    [Fact]
    public void Normalise_repairs_before_deduplicating()
    {
        // Otherwise a repaired caption and its mangled twin both survive.
        var result = CaptionText.Normalise([$"Pairings {MangledGem}", "Pairings \U0001F48E"]);

        Assert.Equal("Pairings \U0001F48E", result);
    }

    [Fact]
    public void Normalise_returns_null_when_nothing_usable_remains()
    {
        Assert.Null(CaptionText.Normalise(null));
        Assert.Null(CaptionText.Normalise([]));
        Assert.Null(CaptionText.Normalise(["", "   "]));
    }

    [Fact]
    public void RepairMojibake_restores_emoji_mangled_by_latin1_decoding()
    {
        Assert.Equal("Best pairings \U0001F48E", CaptionText.RepairMojibake($"Best pairings {MangledGem}"));
    }

    [Fact]
    public void RepairMojibake_restores_smart_quotes()
    {
        // Smart quotes sit inside recipe words, so this matters beyond emoji.
        Assert.Equal("it’s ready", CaptionText.RepairMojibake($"it{MangledApostrophe}s ready"));
    }

    [Theory]
    [InlineData("Already clean text.")]
    [InlineData("Already clean \U0001F48E with emoji")]
    [InlineData("Café au lait")]
    [InlineData("")]
    public void RepairMojibake_leaves_correct_text_alone(string text)
    {
        Assert.Equal(text, CaptionText.RepairMojibake(text));
    }
}
