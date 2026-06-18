using StickerApp;
using Xunit;

namespace StickerApp.Tests;

public class SessionTests
{
    [Fact]
    public void Parses_a_flat_array_round_trip()
    {
        const string json = """
        [
          { "source": "C:\\a.png", "x": 10, "y": 20, "scale": 1.5, "on_top": false },
          { "source": "C:\\b.jpg", "x": 30, "y": 40 }
        ]
        """;
        var states = Session.Parse(json);

        Assert.Equal(2, states.Count);
        Assert.Equal("C:\\a.png", states[0].Source);
        Assert.Equal(10, states[0].X);
        Assert.Equal(20, states[0].Y);
        Assert.Equal(1.5, states[0].Scale);
        Assert.False(states[0].OnTop);
        // Defaults apply to keys the second entry omits.
        Assert.Equal(1.0, states[1].Scale);
        Assert.True(states[1].OnTop);
    }

    [Fact]
    public void Accepts_a_stickers_object_envelope_for_forward_compat()
    {
        const string json = """{ "stickers": [ { "source": "C:\\a.png" } ] }""";
        var states = Session.Parse(json);
        Assert.Single(states);
        Assert.Equal("C:\\a.png", states[0].Source);
    }

    [Fact]
    public void Skips_one_malformed_entry_but_keeps_its_siblings()
    {
        // The middle entry has x as a string, which won't deserialize to long.
        const string json = """
        [
          { "source": "C:\\good1.png", "x": 1 },
          { "source": "C:\\bad.png", "x": "not-a-number" },
          { "source": "C:\\good2.png", "x": 2 }
        ]
        """;
        var states = Session.Parse(json);
        Assert.Equal(2, states.Count);
        Assert.Equal("C:\\good1.png", states[0].Source);
        Assert.Equal("C:\\good2.png", states[1].Source);
    }

    [Fact]
    public void Rejects_entries_with_a_blank_source()
    {
        const string json = """[ { "source": "" }, { "source": "   " }, { "source": "C:\\ok.png" } ]""";
        var states = Session.Parse(json);
        Assert.Single(states);
        Assert.Equal("C:\\ok.png", states[0].Source);
    }

    [Fact]
    public void Preserves_the_csharp_only_keys_python_ignores()
    {
        const string json = """[ { "source": "C:\\a.png", "pinned": true, "model": "birefnet-general" } ]""";
        var states = Session.Parse(json);
        Assert.Single(states);
        Assert.True(states[0].Pinned);
        Assert.Equal("birefnet-general", states[0].Model);
    }

    [Theory]
    [InlineData("[]")]              // empty array
    [InlineData("42")]             // non-array, non-object root
    [InlineData("{}")]             // object without "stickers"
    [InlineData("\"hello\"")]      // string root
    public void Non_sticker_roots_yield_an_empty_list(string json)
    {
        Assert.Empty(Session.Parse(json));
    }
}
