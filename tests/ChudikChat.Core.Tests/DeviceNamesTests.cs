using ChudikChat.Core.Model;

namespace ChudikChat.Core.Tests;

public class DeviceNamesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("​")]
    public void Empty_names_fall_back(string? raw)
    {
        Assert.Equal(DeviceNames.Fallback, DeviceNames.Sanitize(raw));
    }

    [Fact]
    public void Newlines_cannot_break_the_peer_list()
    {
        Assert.Equal("ВасяПетя", DeviceNames.Sanitize("Вася\nПетя"));
    }

    [Fact]
    public void Bidi_overrides_are_stripped()
    {
        // Без этого имя "‮gnp.exe" рисуется как "exe.png" и вводит в заблуждение.
        Assert.Equal("gnp.exe", DeviceNames.Sanitize("‮gnp.exe"));
    }

    [Fact]
    public void Long_names_are_capped()
    {
        var sanitized = DeviceNames.Sanitize(new string('я', 500));

        Assert.Equal(DeviceNames.MaxLength, sanitized.Length);
    }

    [Fact]
    public void Ordinary_names_pass_through()
    {
        Assert.Equal("MacBook Air Даши", DeviceNames.Sanitize("  MacBook Air Даши  "));
    }
}
