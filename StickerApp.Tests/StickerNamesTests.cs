using StickerApp;
using Xunit;

namespace StickerApp.Tests;

public class StickerNamesTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 15, 0, 0);

    [Fact]
    public void Regular_files_show_their_file_name() =>
        Assert.Equal("photo.jpg", StickerNames.Display(@"C:\pics\photo.jpg", Now));

    [Fact]
    public void Todays_clipboard_capture_shows_the_time()
    {
        string name = StickerNames.Display(
            @"C:\x\clipboard\clip-20261116-144502-686-f5ac38d26d124af0912bdf55dd8493bd.png", Now);
        Assert.StartsWith("Pasted image (", name);
        Assert.Contains(new DateTime(2026, 11, 16, 14, 45, 2).ToString("t"), name);
    }

    [Fact]
    public void Older_clipboard_capture_shows_the_date()
    {
        string name = StickerNames.Display(@"C:\x\clip-20261102-090000-000-abc.png", Now);
        Assert.Equal($"Pasted image ({new DateTime(2026, 11, 2).ToString("d MMM")})", name);
    }

    [Fact]
    public void A_file_merely_starting_with_clip_is_left_alone() =>
        Assert.Equal("clip-art.png", StickerNames.Display(@"C:\pics\clip-art.png", Now));
}
