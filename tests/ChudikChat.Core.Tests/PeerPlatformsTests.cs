using System.Text;
using System.Text.Json;
using ChudikChat.Core.Discovery;
using ChudikChat.Core.Model;

namespace ChudikChat.Core.Tests;

public class PeerPlatformsTests
{
    [Theory]
    [InlineData("windows", PeerPlatform.Windows)]
    [InlineData("macos", PeerPlatform.MacOs)]
    [InlineData("android", PeerPlatform.Android)]
    [InlineData("Windows", PeerPlatform.Windows)]
    [InlineData("MACOS", PeerPlatform.MacOs)]
    public void Known_platforms_are_parsed(string raw, PeerPlatform expected)
    {
        Assert.Equal(expected, PeerPlatforms.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("linux")]
    [InlineData("ios")]
    [InlineData("windows ")]
    [InlineData("окна")]
    public void Anything_else_is_unknown(string? raw)
    {
        Assert.Equal(PeerPlatform.Unknown, PeerPlatforms.Parse(raw));
    }

    /// <summary>
    /// Мусор произвольной длины не должен ни падать, ни во что-то превращаться:
    /// значение приходит по сети, и длину его никто не ограничивает.
    /// </summary>
    [Fact]
    public void A_very_long_value_is_unknown()
    {
        Assert.Equal(PeerPlatform.Unknown, PeerPlatforms.Parse(new string('ы', 100_000)));
    }

    [Fact]
    public void Unknown_is_the_absence_of_a_value()
    {
        Assert.Null(PeerPlatforms.Wire(PeerPlatform.Unknown));
    }

    [Theory]
    [InlineData(PeerPlatform.Windows)]
    [InlineData(PeerPlatform.MacOs)]
    [InlineData(PeerPlatform.Android)]
    public void A_platform_survives_the_trip_to_the_wire_and_back(PeerPlatform platform)
    {
        Assert.Equal(platform, PeerPlatforms.Parse(PeerPlatforms.Wire(platform)));
    }

    /// <summary>
    /// Своя платформа. Сверять с константой нельзя: тесты гоняются и в CI на Windows,
    /// и на машине разработчика, — поэтому спрашиваем у того же источника, но иначе.
    /// </summary>
    [Fact]
    public void The_local_platform_agrees_with_the_runtime()
    {
        var expected = PeerPlatform.Unknown;

        if (OperatingSystem.IsAndroid())
            expected = PeerPlatform.Android;
        else if (OperatingSystem.IsWindows())
            expected = PeerPlatform.Windows;
        else if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            expected = PeerPlatform.MacOs;

        Assert.Equal(expected, PeerPlatforms.Local);
    }

    /// <summary>
    /// Датаграмма с платформой, о которой эта сборка не знает, обязана разбираться.
    /// </summary>
    /// <remarks>
    /// Это и есть та причина, по которой на проводе строка, а не перечисление.
    /// Перечисление <c>JsonStringEnumConverter</c> развернул бы в исключение, а разбор
    /// датаграммы исключения гасит молча — и пир на новой платформе пропал бы из списка
    /// целиком, не иконка, а сам пир. Проверять это рассуждением нельзя.
    /// </remarks>
    [Fact]
    public void A_datagram_with_an_unknown_platform_still_parses()
    {
        var sent = new DiscoveryDatagram
        {
            Kind = DiscoveryKind.Announce,
            PeerId = PeerId.New(),
            DisplayName = "Даша",
            ListenPort = 51234,
            Platform = "freebsd",
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            sent, typeof(DiscoveryDatagram), DiscoveryJsonContext.Default);

        var received = (DiscoveryDatagram?)JsonSerializer.Deserialize(
            payload, typeof(DiscoveryDatagram), DiscoveryJsonContext.Default);

        Assert.NotNull(received);
        Assert.Equal(sent.PeerId, received.PeerId);
        Assert.Equal(51234, received.ListenPort);
        Assert.Equal(PeerPlatform.Unknown, PeerPlatforms.Parse(received.Platform));
    }

    /// <summary>Датаграмма от старой сборки: поля платформы в ней нет вовсе.</summary>
    [Fact]
    public void A_datagram_without_a_platform_still_parses()
    {
        var json = $$"""
            {"version":1,"kind":"Announce","peerId":"{{Guid.NewGuid():N}}",
             "displayName":"Даша","listenPort":51234}
            """;

        var received = (DiscoveryDatagram?)JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes(json), typeof(DiscoveryDatagram), DiscoveryJsonContext.Default);

        Assert.NotNull(received);
        Assert.Null(received.Platform);
        Assert.Equal(PeerPlatform.Unknown, PeerPlatforms.Parse(received.Platform));
    }

    /// <summary>Своя платформа доезжает до датаграммы, а не теряется по дороге.</summary>
    [Fact]
    public void The_local_platform_reaches_the_datagram()
    {
        var beacon = new LocalBeacon(PeerId.New(), "Даша", 51234, null, PeerPlatform.Android);

        var datagram = new DiscoveryDatagram
        {
            Kind = DiscoveryKind.Announce,
            PeerId = beacon.Peer,
            DisplayName = beacon.DisplayName,
            ListenPort = beacon.ListenPort,
            Platform = PeerPlatforms.Wire(beacon.Platform),
        };

        Assert.Equal("android", datagram.Platform);
    }
}
