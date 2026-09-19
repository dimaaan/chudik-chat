using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ChudikChat.Core.Discovery;

/// <summary>
/// Пригодный для обнаружения интерфейс с одним из его адресов IPv4.
/// </summary>
public sealed record NetworkAdapter
{
    public required int InterfaceIndex { get; init; }

    public required IPAddress Address { get; init; }

    public required IPAddress Mask { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// Похож ли адаптер на виртуальный: VPN, гипервизор, контейнерный мост.
    /// Такие по умолчанию не используем — именно они плодят недостижимые адреса
    /// и дубли в списке пиров.
    /// </summary>
    public required bool IsLikelyVirtual { get; init; }

    /// <summary>
    /// Широковещательный адрес подсети. Именно он, а не 255.255.255.255: последний
    /// на многодомной машине уходит в один интерфейс по таблице маршрутизации,
    /// и половина сети остаётся неохваченной.
    /// </summary>
    public IPAddress DirectedBroadcast => ComputeBroadcast(Address, Mask);

    public override string ToString() => $"{Name} {Address}/{MaskBits(Mask)}";

    private static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var result = new byte[4];

        for (var i = 0; i < 4; i++)
            result[i] = (byte)(a[i] | ~m[i]);

        return new IPAddress(result);
    }

    private static int MaskBits(IPAddress mask)
    {
        var bits = 0;
        foreach (var b in mask.GetAddressBytes())
            bits += System.Numerics.BitOperations.PopCount(b);

        return bits;
    }
}

public static class InterfaceScanner
{
    private static readonly string[] VirtualMarkers =
    [
        "vethernet", "vmware", "virtualbox", "hyper-v", "docker", "tap-windows",
        "tailscale", "wireguard", "zerotier", "npcap", "pseudo-interface", "wsl",
    ];

    /// <summary>Все интерфейсы, на которых обнаружение в принципе возможно.</summary>
    public static IReadOnlyList<NetworkAdapter> ScanAll()
    {
        var adapters = new List<NetworkAdapter>();

        foreach (var nic in SafeEnumerate())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (!nic.SupportsMulticast || nic.IsReceiveOnly)
                continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Ppp)
            {
                continue;
            }

            int index;
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
                index = properties.GetIPv4Properties()?.Index ?? -1;
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                continue;
            }

            if (index < 0)
                continue;

            var virtualLooking = LooksVirtual(nic);

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                // 169.254/16 — адрес, который машина выдала себе сама, не получив DHCP.
                if (unicast.Address.GetAddressBytes() is [169, 254, ..])
                    continue;

                var mask = unicast.IPv4Mask;
                if (mask is null || Equals(mask, IPAddress.Any))
                    continue;

                adapters.Add(new NetworkAdapter
                {
                    InterfaceIndex = index,
                    Address = unicast.Address,
                    Mask = mask,
                    Name = nic.Name,
                    Description = nic.Description,
                    IsLikelyVirtual = virtualLooking,
                });
            }
        }

        return adapters;
    }

    /// <summary>Интерфейсы, на которых ведём обнаружение по умолчанию.</summary>
    public static IReadOnlyList<NetworkAdapter> ScanPreferred()
    {
        var all = ScanAll();
        var real = all.Where(x => !x.IsLikelyVirtual).ToArray();

        // Если «настоящих» не нашлось, лучше работать через виртуальные, чем не работать.
        return real.Length > 0 ? real : all;
    }

    private static bool LooksVirtual(NetworkInterface nic)
    {
        var haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();

        foreach (var marker in VirtualMarkers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static NetworkInterface[] SafeEnumerate()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
