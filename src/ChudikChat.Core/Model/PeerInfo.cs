using System.Net;

namespace ChudikChat.Core.Model;

/// <summary>
/// Один известный адрес пира. У многодомной машины их несколько, и часть
/// недостижима с нашей стороны — поэтому храним список и ранжируем его.
/// </summary>
public sealed class PeerEndpoint
{
    public required IPAddress Address { get; init; }

    public required int Port { get; init; }

    /// <summary>Индекс нашего интерфейса, на который пришла датаграмма (из IP_PKTINFO).</summary>
    public int LocalInterfaceIndex { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset? LastConnectOkUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public IPEndPoint ToIPEndPoint() => new(Address, Port);

    public override string ToString() => $"{Address}:{Port}";
}

/// <summary>
/// Пир в таблице движка. Изменяется только в цикле событий <c>ChatEngine</c>.
/// </summary>
public sealed class PeerInfo
{
    public required PeerId Id { get; init; }

    public string DisplayName { get; set; } = DeviceNames.Fallback;

    public List<PeerEndpoint> Endpoints { get; } = [];

    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>
    /// Добавляет или обновляет адрес. Совпадение — по паре адрес+порт.
    /// </summary>
    public PeerEndpoint TouchEndpoint(IPAddress address, int port, int interfaceIndex, DateTimeOffset now)
    {
        foreach (var existing in Endpoints)
        {
            if (existing.Port == port && existing.Address.Equals(address))
            {
                existing.LastSeenUtc = now;
                existing.LocalInterfaceIndex = interfaceIndex;
                return existing;
            }
        }

        var added = new PeerEndpoint
        {
            Address = address,
            Port = port,
            LocalInterfaceIndex = interfaceIndex,
            LastSeenUtc = now,
        };

        Endpoints.Add(added);
        return added;
    }

    public PeerSnapshot ToSnapshot()
    {
        var best = Endpoints.Count == 0 ? null : Endpoints[0].ToIPEndPoint();
        return new PeerSnapshot(Id, DisplayName, best, Endpoints.Count, LastSeenUtc);
    }
}

/// <summary>Неизменяемый слепок для UI: пересекать границу потока должен он, а не <see cref="PeerInfo"/>.</summary>
public sealed record PeerSnapshot(
    PeerId Id,
    string DisplayName,
    IPEndPoint? PrimaryEndpoint,
    int EndpointCount,
    DateTimeOffset LastSeenUtc);
