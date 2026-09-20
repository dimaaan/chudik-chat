using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ChudikChat.Core.Model;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Discovery;

/// <summary>
/// Обнаружение пиров в локальной сети: два сокета UDP, только IPv4.
/// </summary>
/// <remarks>
/// Почему именно так:
/// приём — на одном сокете, привязанном к 0.0.0.0, с отдельным вступлением в группу
/// по каждому интерфейсу; отправка — со второго сокета на эфемерном порту, в его порт
/// приходят unicast-ответы на HELLO.
/// Периодическое объявление идёт в multicast-группу, потому что при общем порте
/// с ReuseAddress multicast получают все сокеты, а unicast и broadcast — ровно один.
/// Без этого два экземпляра на одной машине не увидели бы друг друга, а это
/// основной стенд для отладки.
/// </remarks>
public sealed class UdpDiscoveryService : IDisposable
{
    private static readonly TimeSpan AnnouncePeriod = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AdapterPoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AdapterDebounce = TimeSpan.FromMilliseconds(750);

    private readonly IPAddress _group = IPAddress.Parse(ProtocolConstants.MulticastGroup);
    private readonly Func<LocalBeacon> _beacon;
    private readonly Action<DiscoveryObservation> _observed;
    private readonly Action<string> _diagnostic;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _adapterSignal = new(0, 1);
    private readonly HashSet<int> _joined = [];

    /// <summary>
    /// Интерфейсы, про отказ которых уже сказано. Нужен отдельно от <see cref="_joined"/>:
    /// туда неудачник не попадает, чтобы попытку можно было повторить на следующем
    /// опросе, — а без этого списка каждая повторная попытка снова писала бы в
    /// диагностику. На маке с поднятым VPN это значит одну и ту же строку про utun
    /// каждые несколько секунд, и она затирает единственное, что там полезно.
    /// </summary>
    private readonly HashSet<int> _joinFailuresReported = [];

    private Socket? _rx;
    private Socket? _tx;
    private volatile IReadOnlyList<NetworkAdapter> _adapters = [];
    private NetworkAddressChangedEventHandler? _addressChanged;

    public UdpDiscoveryService(
        Func<LocalBeacon> beacon,
        Action<DiscoveryObservation> observed,
        Action<string> diagnostic)
    {
        _beacon = beacon;
        _observed = observed;
        _diagnostic = diagnostic;
    }

    /// <summary>Интерфейсы, на которых сейчас ведётся обнаружение.</summary>
    public IReadOnlyList<NetworkAdapter> ActiveAdapters => _adapters;

    public async Task RunAsync(CancellationToken ct)
    {
        _rx = CreateReceiveSocket();
        _tx = CreateSendSocket();

        RefreshAdapters();

        _addressChanged = (_, _) =>
        {
            try
            {
                _adapterSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Пересканирование уже запланировано — событие приходит пачками.
            }
        };

        NetworkChange.NetworkAddressChanged += _addressChanged;

        try
        {
            await Task.WhenAll(
                ReceiveLoopAsync(_rx, ct),
                ReceiveLoopAsync(_tx, ct),
                AnnounceLoopAsync(ct),
                AdapterLoopAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= _addressChanged;
            await SayGoodbyeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Разослать HELLO немедленно — например, когда пользователь нажал «обновить».</summary>
    public Task HelloAsync(CancellationToken ct) => BroadcastAsync(DiscoveryKind.Hello, ct);

    /// <summary>Сколько адресов в подсети готовы обойти поштучно.</summary>
    private const int MaxSweepHosts = 1022;

    /// <summary>
    /// Обход подсети поштучно — запасной путь, когда в сети зарезана многоадресная рассылка.
    /// </summary>
    /// <remarks>
    /// Обходим UDP, а не TCP: слушающий порт у каждого пира эфемерный и заранее неизвестен,
    /// зато порт обнаружения фиксирован. Пир, получивший такой HELLO, ответит одноадресно
    /// на наш эфемерный порт — ровно как на обычное приветствие.
    /// </remarks>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var socket = _tx;
        if (socket is null)
            return 0;

        var payload = Serialize(DiscoveryKind.Hello);
        var probed = 0;

        foreach (var adapter in _adapters)
        {
            foreach (var address in HostsOf(adapter))
            {
                ct.ThrowIfCancellationRequested();

                if (address.Equals(adapter.Address))
                    continue;

                try
                {
                    await socket
                        .SendToAsync(payload, SocketFlags.None, new IPEndPoint(address, ProtocolConstants.DiscoveryPort), ct)
                        .ConfigureAwait(false);

                    probed++;
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    // Один недостижимый адрес не повод прекращать обход.
                }
            }
        }

        return probed;
    }

    private static IEnumerable<IPAddress> HostsOf(NetworkAdapter adapter)
    {
        var address = ToUInt32(adapter.Address);
        var mask = ToUInt32(adapter.Mask);

        var network = address & mask;
        var broadcast = network | ~mask;

        var count = broadcast - network;
        if (count is 0 or > MaxSweepHosts)
            yield break;

        for (var host = network + 1; host < broadcast; host++)
            yield return ToAddress(host);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress ToAddress(uint value) => new(
    [
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value,
    ]);

    private Socket CreateReceiveSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // ReuseAddress обязательно до Bind: иначе второй экземпляр на машине не стартует.
        //
        // SO_REUSEPORT здесь не ставится, и это проверено, а не забыто. Опасение было
        // такое: на BSD-ядрах без него второй экземпляр не уживается с первым. На деле
        // сырое значение 0x0200 через управляемый SetSocketOption не проходит вовсе —
        // .NET отвечает OperationNotSupported, то есть ветка никогда и не работала,
        // только писала пугающую строку в диагностику при каждом запуске на маке.
        //
        // А нужды в ней нет: два процесса на macOS 26, привязанные к одному порту
        // с одним лишь ReuseAddress, получили все датаграммы до единой — оба.
        // Понадобится всё-таки SO_REUSEPORT — это P/Invoke setsockopt, а не каст
        // к SocketOptionName.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // IP_PKTINFO: даёт индекс интерфейса, на который пришла датаграмма.
        // Без него нельзя отличить адрес, достижимый с нашей стороны, от чужого.
        TrySetOption(socket, SocketOptionLevel.IP, SocketOptionName.PacketInformation, "IP_PKTINFO");

        SuppressConnectionReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, ProtocolConstants.DiscoveryPort));

        return socket;
    }

    private Socket CreateSendSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };

        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        // Без петли два экземпляра на одной машине не услышали бы друг друга.
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

        SuppressConnectionReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        return socket;
    }

    /// <summary>
    /// На Windows ICMP «порт недостижим» на UDP-сокете превращается в исключение при
    /// следующем чтении и рвёт цикл приёма. Отключаем это поведение.
    /// </summary>
    private static void SuppressConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int sioUdpConnreset = -1744830452;
        try
        {
            socket.IOControl((IOControlCode)sioUdpConnreset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Не критично: цикл приёма всё равно переживает SocketException.
        }
    }

    private void RefreshAdapters()
    {
        var adapters = InterfaceScanner.ScanPreferred();
        _adapters = adapters;

        if (_rx is null)
            return;

        foreach (var adapter in adapters)
        {
            if (!_joined.Add(adapter.InterfaceIndex))
                continue;

            try
            {
                _rx.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(_group, adapter.InterfaceIndex));

                // Получилось — значит про следующий отказ здесь снова стоит сказать.
                _joinFailuresReported.Remove(adapter.InterfaceIndex);
            }
            catch (SocketException e)
            {
                // Типично для VPN- и виртуальных адаптеров. Один отказ не должен
                // мешать обнаружению на остальных интерфейсах.
                _joined.Remove(adapter.InterfaceIndex);

                // Повторять попытку — да, повторять жалобу — нет.
                if (_joinFailuresReported.Add(adapter.InterfaceIndex))
                    _diagnostic($"{adapter.Name}: не удалось вступить в группу ({e.SocketErrorCode})");
            }
        }
    }

    private async Task AdapterLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var signalled = await _adapterSignal.WaitAsync(AdapterPoll, ct).ConfigureAwait(false);

            if (signalled)
            {
                // Событие приходит пачками — даём буре улечься.
                await Task.Delay(AdapterDebounce, ct).ConfigureAwait(false);
                _adapterSignal.Wait(0);
            }

            RefreshAdapters();
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        await BroadcastAsync(DiscoveryKind.Hello, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            // Джиттер, чтобы десяток устройств не объявлялся в один и тот же миг.
            var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
            await Task.Delay(AnnouncePeriod * jitter, ct).ConfigureAwait(false);

            await BroadcastAsync(DiscoveryKind.Announce, ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(DiscoveryKind kind, CancellationToken ct)
    {
        var socket = _tx;
        if (socket is null)
            return;

        var payload = Serialize(kind);
        var adapters = _adapters;

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var adapter in adapters)
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        adapter.Address.GetAddressBytes());

                    await socket
                        .SendToAsync(payload, SocketFlags.None, new IPEndPoint(_group, ProtocolConstants.DiscoveryPort), ct)
                        .ConfigureAwait(false);

                    await socket
                        .SendToAsync(payload, SocketFlags.None, new IPEndPoint(adapter.DirectedBroadcast, ProtocolConstants.DiscoveryPort), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    _diagnostic($"{adapter.Name}: объявление не ушло ({e.Message})");
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SayGoodbyeAsync()
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await BroadcastAsync(DiscoveryKind.Bye, brief.Token).ConfigureAwait(false);
        }
        catch
        {
            // Прощание — вежливость, а не механизм. Уход всё равно заметят по таймауту.
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await socket
                    .ReceiveMessageFromAsync(buffer, SocketFlags.None, any, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (result.RemoteEndPoint is not IPEndPoint source)
                continue;

            Handle(
                buffer.AsSpan(0, result.ReceivedBytes),
                source,
                result.PacketInformation.Interface,
                ct);
        }
    }

    private void Handle(ReadOnlySpan<byte> payload, IPEndPoint source, int interfaceIndex, CancellationToken ct)
    {
        DiscoveryDatagram? datagram;
        try
        {
            datagram = JsonSerializer.Deserialize(payload, typeof(DiscoveryDatagram), DiscoveryJsonContext.Default)
                as DiscoveryDatagram;
        }
        catch (JsonException)
        {
            // В сети может лежать что угодно на том же порту. Молча пропускаем.
            return;
        }

        if (datagram is null || datagram.Version != ProtocolConstants.Version)
            return;

        if (datagram.ListenPort is <= 0 or > 65535)
            return;

        var me = _beacon();
        if (datagram.PeerId == me.Peer)
            return;

        _observed(new DiscoveryObservation(
            datagram.PeerId,
            DeviceNames.Sanitize(datagram.DisplayName),
            source.Address,
            datagram.ListenPort,
            interfaceIndex,
            datagram.Kind == DiscoveryKind.Bye,
            Avatars.SanitizeTag(datagram.AvatarTag)));

        if (datagram.Kind == DiscoveryKind.Hello)
            _ = ReplyAsync(source, ct);
    }

    /// <summary>
    /// Ответ на HELLO идёт unicast на адрес И порт источника, а не на фиксированный порт:
    /// иначе между двумя экземплярами на одной машине ответ достаётся случайному.
    /// </summary>
    private async Task ReplyAsync(IPEndPoint source, CancellationToken ct)
    {
        var socket = _tx;
        if (socket is null)
            return;

        try
        {
            var payload = Serialize(DiscoveryKind.Announce);

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, source, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Ответ не дошёл — собеседник всё равно услышит следующее объявление.
        }
    }

    private byte[] Serialize(DiscoveryKind kind)
    {
        var me = _beacon();
        var datagram = new DiscoveryDatagram
        {
            Kind = kind,
            PeerId = me.Peer,
            DisplayName = me.DisplayName,
            ListenPort = me.ListenPort,
            AvatarTag = me.AvatarTag,
        };

        return JsonSerializer.SerializeToUtf8Bytes(datagram, typeof(DiscoveryDatagram), DiscoveryJsonContext.Default);
    }

    private void TrySetOption(Socket socket, SocketOptionLevel level, SocketOptionName name, string label)
    {
        try
        {
            socket.SetSocketOption(level, name, true);
        }
        catch (SocketException e)
        {
            _diagnostic($"{label} не поддерживается ({e.SocketErrorCode})");
        }
    }

    public void Dispose()
    {
        _rx?.Dispose();
        _tx?.Dispose();
        _sendGate.Dispose();
        _adapterSignal.Dispose();
    }
}
