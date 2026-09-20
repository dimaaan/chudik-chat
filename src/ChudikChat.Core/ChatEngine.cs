using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ChudikChat.Core.Discovery;
using ChudikChat.Core.Model;
using ChudikChat.Core.Transfer;
using ChudikChat.Core.Transport;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core;

/// <summary>
/// Сердце приложения. Всё изменяемое состояние живёт здесь и меняется только
/// в одном цикле-потребителе: сетевые циклы кладут события в канал и ничего не трогают
/// напрямую. Поэтому в движке нет ни одного замка вокруг состояния и ни одной гонки.
/// </summary>
public sealed class ChatEngine : IAsyncDisposable, IExchangeSink
{
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Таблица пиров. Читается и пишется ИСКЛЮЧИТЕЛЬНО в цикле событий.</summary>
    private readonly Dictionary<PeerId, PeerInfo> _peers = [];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();
    private readonly TcpListenerService _listener = new();

    /// <summary>
    /// Картинки по отпечатку. Читается и пишется ИСКЛЮЧИТЕЛЬНО в цикле событий.
    /// Только в памяти: на диск аватары не попадают, как и всё остальное.
    /// </summary>
    private readonly Dictionary<string, AvatarImage> _avatars = [];

    /// <summary>
    /// Отпечатки, за которыми уже ходили: неважно, успешно или нет.
    /// </summary>
    /// <remarks>
    /// За отпечатком ходят ровно один раз. Пометка ставится ДО похода и при неудаче
    /// не снимается — поэтому здесь же и дедупликация (два пира с одной картинкой —
    /// один поход), и запрет повтора. Ни счётчиков попыток, ни отступов, ни таймеров:
    /// объявления идут каждые четыре секунды, и любая схема повторов превратилась бы
    /// в шквал соединений на всю сеть.
    ///
    /// Чистится вместе с <see cref="_avatars"/> в <see cref="ReapAvatars"/>, иначе
    /// неудачный отпечаток остался бы помеченным навсегда.
    /// </remarks>
    private readonly HashSet<string> _avatarAsked = [];

    /// <summary>Сколько картинок держим. Потолок на случай людной сети.</summary>
    private const int MaxCachedAvatars = 64;

    /// <summary>
    /// Слепок маршрутов, публикуемый циклом событий для отправителей.
    /// Ссылка меняется целиком, содержимое неизменяемо — читать можно из любого потока.
    /// </summary>
    private volatile IReadOnlyDictionary<PeerId, IReadOnlyList<IPEndPoint>> _routes =
        new Dictionary<PeerId, IReadOnlyList<IPEndPoint>>();

    private readonly UdpDiscoveryService _discovery;

    private volatile string _displayName = DeviceNames.Local();

    /// <summary>
    /// Своя картинка. volatile, как и имя: голова присваивает её из продолжения на пуле
    /// потоков уже после запуска, а читают поток обнаружения и потоки входящих соединений.
    /// </summary>
    private volatile AvatarImage? _avatar;

    private Task? _loop;
    private Task? _accept;
    private Task? _discoveryTask;
    private Task? _expiryTask;

    public ChatEngine()
    {
        _discovery = new UdpDiscoveryService(
            () => new LocalBeacon(LocalId, _displayName, _listener.Port, _avatar?.Tag, PeerPlatforms.Local),
            OnObserved,
            message => Publish(new DiagnosticEvent(message)));
    }

    /// <summary>Сколько молчания считаем уходом.</summary>
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(1);

    public PeerId LocalId { get; } = PeerId.New();

    public int ListenPort => _listener.Port;

    public string LocalDisplayName
    {
        get => _displayName;
        set => _displayName = DeviceNames.Sanitize(value);
    }

    /// <summary>Все события поднимаются из потока цикла, а не из UI-потока.</summary>
    public event Action<ChatMessage>? MessageReceived;

    public event Action<PeerSnapshot>? PeerAppeared;

    public event Action<PeerSnapshot>? PeerUpdated;

    public event Action<PeerId>? PeerGone;

    public event Action<string>? Diagnostic;

    /// <summary>Изменение состояния передачи. Не чаще десяти раз в секунду на передачу.</summary>
    public event Action<TransferProgress>? TransferChanged;

    /// <summary>
    /// Кто решает, принимать ли входящую передачу. Пока не установлен, всё отклоняется:
    /// молча складывать чужие файлы на диск приложение не должно.
    /// </summary>
    public Func<IncomingTransferOffer, CancellationToken, Task<TransferDecision>>? TransferDecider { get; set; }

    /// <summary>
    /// Картинка учётной записи хозяина. Ядро её не добывает — подставляет голова приложения.
    /// </summary>
    /// <remarks>
    /// Свойство, а не <c>Func&lt;&gt;</c>, как <see cref="DeviceNames.LocalProvider"/>: добыть
    /// картинку у операционной системы — медленная работа (на macOS вообще отдельный
    /// процесс), а провайдер дёргался бы из цикла объявлений каждые четыре секунды
    /// и на каждом входящем соединении.
    /// </remarks>
    public AvatarImage? LocalAvatar
    {
        get => _avatar;
        set => _avatar = value;
    }

    /// <summary>
    /// Куда складывать принятое. Внутри создаётся отдельная папка на каждую передачу.
    /// Android-голова подменяет это значение своим.
    /// </summary>
    public string DownloadRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "ChudikChat");

    public Task StartAsync()
    {
        if (_loop is not null)
            throw new InvalidOperationException("движок уже запущен");

        _loop = Task.Run(() => RunEventLoopAsync(_lifetime.Token), CancellationToken.None);
        _accept = Task.Run(() => _listener.RunAsync(HandleInboundAsync, _lifetime.Token), CancellationToken.None);
        _discoveryTask = Task.Run(() => _discovery.RunAsync(_lifetime.Token), CancellationToken.None);
        _expiryTask = Task.Run(() => RunExpiryAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Интерфейсы, на которых сейчас ведётся обнаружение.</summary>
    public IReadOnlyList<NetworkAdapter> ActiveAdapters => _discovery.ActiveAdapters;

    /// <summary>
    /// Кнопка «обновить»: сперва обычное приветствие, затем поштучный обход подсети —
    /// он выручает в сетях, где многоадресная рассылка зарезана.
    /// </summary>
    public async Task RefreshPeersAsync(CancellationToken ct = default)
    {
        await _discovery.HelloAsync(ct).ConfigureAwait(false);

        var probed = await _discovery.SweepAsync(ct).ConfigureAwait(false);
        if (probed > 0)
            Publish(new DiagnosticEvent($"опросил {probed} адрес(ов) в подсети"));
    }

    /// <summary>Известные пиры. Слепок, безопасный для чтения из любого потока.</summary>
    public IReadOnlyCollection<PeerId> KnownPeers => _routes.Keys.ToArray();

    /// <summary>
    /// Дожидается, пока цикл событий разберёт всё, что уже положено в очередь.
    /// Нужно там, где вызывающий обязан увидеть последствия собственного действия:
    /// например, после отправки по явному адресу — чтобы выученный маршрут уже был опубликован.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_loop is null)
            return;

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new BarrierEvent(signal)))
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await signal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Движок остановлен — ждать больше нечего.
        }
    }

    /// <summary>
    /// Сколько картинок сейчас в кэше. Только для тестов: словарь принадлежит циклу
    /// событий, и читать его из чужого потока нельзя даже после <see cref="FlushAsync"/> —
    /// таймер уборки продолжает тикать.
    /// </summary>
    internal async Task<int> CachedAvatarCountAsync(CancellationToken ct = default)
    {
        if (_loop is null)
            return 0;

        var signal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new InspectEvent(signal)))
            return 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        return await signal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task<ChatMessage> SendTextAsync(PeerId peer, string text, CancellationToken ct = default)
    {
        if (!_routes.TryGetValue(peer, out var endpoints) || endpoints.Count == 0)
            return Outgoing(peer, text, MessageState.Failed);

        var message = Outgoing(peer, text, MessageState.Sending);
        var frame = ToFrame(message);

        var gate = _sendLocks.GetOrAdd(peer.Canonical, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Перебираем адреса по порядку: у многодомной машины часть из них
            // с нашей стороны недостижима.
            foreach (var endpoint in endpoints)
            {
                try
                {
                    var remote = await OutboundExchange
                        .SendTextAsync(endpoint, LocalIdentity(), frame, ct)
                        .ConfigureAwait(false);

                    Publish(new PeerSeenEvent(
                        remote.PeerId,
                        remote.DisplayName,
                        endpoint.Address,
                        remote.ListenPort,
                        0,
                        true,
                        Avatars.SanitizeTag(remote.AvatarTag),
                        remote.Platform));
                    await FlushAsync(ct).ConfigureAwait(false);
                    return message with { State = MessageState.Delivered };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Publish(new DiagnosticEvent($"{endpoint} не принял сообщение: {e.Message}"));
                }
            }
        }
        finally
        {
            gate.Release();
        }

        return message with { State = MessageState.Failed };
    }

    /// <summary>
    /// Отправка по явному адресу — путь «добавить пира по IP», когда multicast в сети зарезан.
    /// </summary>
    public async Task<ChatMessage> SendTextToAsync(IPEndPoint target, string text, CancellationToken ct = default)
    {
        var placeholder = new PeerId(Guid.Empty);
        var message = Outgoing(placeholder, text, MessageState.Sending);
        var frame = ToFrame(message);

        var gate = _sendLocks.GetOrAdd(target.ToString(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var remote = await OutboundExchange
                .SendTextAsync(target, LocalIdentity(), frame, ct)
                .ConfigureAwait(false);

            Publish(new PeerSeenEvent(
                remote.PeerId,
                remote.DisplayName,
                target.Address,
                remote.ListenPort,
                0,
                true,
                Avatars.SanitizeTag(remote.AvatarTag),
                remote.Platform));
            await FlushAsync(ct).ConfigureAwait(false);
            return message with { Peer = remote.PeerId, State = MessageState.Delivered };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Publish(new DiagnosticEvent($"{target}: {e.Message}"));
            return message with { State = MessageState.Failed };
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly ConcurrentDictionary<Guid, TransferTracker> _transfers = new();

    public Task<TransferProgress> SendFileAsync(PeerId peer, string path, CancellationToken ct = default)
        => SendAsync(peer, FileSystemTransferSource.ForFile(path), ct);

    public Task<TransferProgress> SendFolderAsync(PeerId peer, string path, CancellationToken ct = default)
        => SendAsync(peer, FileSystemTransferSource.ForDirectory(path), ct);

    /// <summary>
    /// Передача идёт по отдельному соединению, поэтому папка на четыре гигабайта
    /// не мешает переписке, а несколько передач идут разом.
    /// </summary>
    public async Task<TransferProgress> SendAsync(PeerId peer, ITransferSource source, CancellationToken ct = default)
    {
        var tracker = new TransferTracker
        {
            Id = Guid.NewGuid(),
            Peer = peer,
            Direction = TransferDirection.Outgoing,
            RootName = source.RootName,
        };

        _transfers[tracker.Id] = tracker;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        tracker.Cancellation = cts;

        try
        {
            var summary = await source.ScanAsync(cts.Token).ConfigureAwait(false);
            tracker.TotalBytes = summary.TotalBytes;
            tracker.TotalEntries = summary.EntryCount;
            PublishProgress(tracker, TransferState.Offered, force: true);

            if (!_routes.TryGetValue(peer, out var endpoints) || endpoints.Count == 0)
                return Finish(tracker, TransferState.Failed, "адрес пира неизвестен");

            string? lastError = null;

            foreach (var endpoint in endpoints)
            {
                try
                {
                    await using var connection = await OutboundExchange
                        .ConnectAsync(endpoint, LocalIdentity(), cts.Token)
                        .ConfigureAwait(false);

                    if (connection.Remote.PeerId != peer)
                    {
                        lastError = $"по адресу {endpoint} оказался другой пир";
                        continue;
                    }

                    // Отмена = закрытие сокета. Токена в ReadAsync/WriteAsync обычно хватает,
                    // но закрытие работает одинаково на всех трёх платформах.
                    using var onCancel = cts.Token.Register(() => SafeDispose(connection.Stream));

                    PublishProgress(tracker, TransferState.Running, force: true);

                    await TransferSender.RunAsync(
                        connection.Stream,
                        tracker.Id,
                        source,
                        summary,
                        bytes =>
                        {
                            tracker.AddBytes(bytes);
                            PublishProgress(tracker, TransferState.Running);
                        },
                        entries =>
                        {
                            tracker.SetCompleted(entries);
                            PublishProgress(tracker, TransferState.Running);
                        },
                        cts.Token).ConfigureAwait(false);

                    return Finish(tracker, TransferState.Completed);
                }
                catch (TransferDeclinedException declined)
                {
                    return Finish(tracker, TransferState.Declined, declined.Reason);
                }
                catch (Exception e)
                {
                    // Отмена закрывает сокет, и наружу приходит вовсе не
                    // OperationCanceledException — решает намерение, а не тип исключения.
                    if (tracker.CancelRequested || cts.IsCancellationRequested)
                        return Finish(tracker, TransferState.Cancelled, "отменено");

                    lastError = e.Message;
                    Publish(new DiagnosticEvent($"{endpoint}: передача не удалась — {e.Message}"));
                }
            }

            return Finish(tracker, TransferState.Failed, lastError ?? "не удалось соединиться");
        }
        catch (Exception e)
        {
            return Finish(tracker, tracker.CancelRequested ? TransferState.Cancelled : TransferState.Failed, e.Message);
        }
        finally
        {
            _transfers.TryRemove(tracker.Id, out _);
        }
    }

    /// <summary>Отменяет передачу в любую сторону. Возвращает <c>false</c>, если её уже нет.</summary>
    public bool CancelTransfer(Guid transferId)
    {
        if (!_transfers.TryGetValue(transferId, out var tracker))
            return false;

        tracker.CancelRequested = true;
        tracker.Cancellation?.Cancel();
        return true;
    }

    async Task IExchangeSink.HandleTransferAsync(
        PeerId peer,
        string displayName,
        TransferOfferFrame offer,
        Stream stream,
        CancellationToken ct)
    {
        if (offer.EntryCount <= 0 || offer.TotalBytes < 0 || offer.FileCount < 0)
        {
            await TryWriteErrorAsync(stream, "бессмысленная сводка передачи", ct).ConfigureAwait(false);
            return;
        }

        var tracker = new TransferTracker
        {
            Id = offer.TransferId,
            Peer = peer,
            Direction = TransferDirection.Incoming,
            RootName = DeviceNames.Sanitize(offer.RootName),
            TotalBytes = offer.TotalBytes,
            TotalEntries = offer.EntryCount,
        };

        if (!_transfers.TryAdd(tracker.Id, tracker))
        {
            await TryWriteErrorAsync(stream, "передача с таким номером уже идёт", ct).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        tracker.Cancellation = cts;

        string? destination = null;
        try
        {
            PublishProgress(tracker, TransferState.Offered, force: true);

            var decision = await DecideAsync(tracker, peer, displayName, offer, cts.Token).ConfigureAwait(false);

            if (!decision.Accepted)
            {
                await FrameCodec.WriteAsync(stream, new TransferDecisionFrame
                {
                    TransferId = offer.TransferId,
                    Accepted = false,
                    Reason = decision.Reason,
                }, cts.Token).ConfigureAwait(false);

                Finish(tracker, TransferState.Declined, decision.Reason);
                return;
            }

            destination = CreateTransferFolder(decision.DestinationRoot ?? DownloadRoot, displayName);
            tracker.DestinationPath = destination;

            await FrameCodec.WriteAsync(stream, new TransferDecisionFrame
            {
                TransferId = offer.TransferId,
                Accepted = true,
            }, cts.Token).ConfigureAwait(false);

            PublishProgress(tracker, TransferState.Running, force: true);

            using var onCancel = cts.Token.Register(() => SafeDispose(stream));

            await TransferReceiver.RunAsync(
                stream,
                offer,
                destination,
                bytes =>
                {
                    tracker.AddBytes(bytes);
                    PublishProgress(tracker, TransferState.Running);
                },
                entries =>
                {
                    tracker.SetCompleted(entries);
                    PublishProgress(tracker, TransferState.Running);
                },
                cts.Token).ConfigureAwait(false);

            await FrameCodec.WriteAsync(stream, new AckFrame { MessageId = offer.TransferId }, cts.Token)
                .ConfigureAwait(false);

            Finish(tracker, TransferState.Completed);
        }
        catch (Exception e)
        {
            // Папка этой передачи создана пустой и принадлежит только ей,
            // поэтому её можно снести целиком, не разбирая, что успело записаться.
            TryDeleteFolder(destination);

            var state = tracker.CancelRequested || cts.IsCancellationRequested
                ? TransferState.Cancelled
                : TransferState.Failed;

            Finish(tracker, state, e.Message);

            if (state == TransferState.Failed)
                await TryWriteErrorAsync(stream, e.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _transfers.TryRemove(tracker.Id, out _);
        }
    }

    private async Task<TransferDecision> DecideAsync(
        TransferTracker tracker,
        PeerId peer,
        string displayName,
        TransferOfferFrame offer,
        CancellationToken ct)
    {
        var decider = TransferDecider;
        if (decider is null)
            return TransferDecision.Decline("приём не настроен");

        var incoming = new IncomingTransferOffer
        {
            TransferId = offer.TransferId,
            Peer = peer,
            PeerName = displayName,
            RootName = tracker.RootName,
            IsDirectory = offer.IsDirectory,
            EntryCount = offer.EntryCount,
            FileCount = offer.FileCount,
            TotalBytes = offer.TotalBytes,
        };

        TransferDecision decision;
        try
        {
            decision = await decider(incoming, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return TransferDecision.Decline($"получатель не смог решить: {e.Message}");
        }

        if (!decision.Accepted)
            return decision;

        var shortage = CheckFreeSpace(decision.DestinationRoot ?? DownloadRoot, offer.TotalBytes);
        return shortage is null ? decision : TransferDecision.Decline(shortage);
    }

    private static string CreateTransferFolder(string root, string peerName)
    {
        var safeName = PathSanitizer.ToSafeFolderName(peerName);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var unique = Guid.NewGuid().ToString("N")[..6];

        var path = Path.Combine(root, $"{safeName}-{stamp}-{unique}");
        Directory.CreateDirectory(PathSanitizer.ForFileSystem(path));
        return path;
    }

    private static string? CheckFreeSpace(string root, long needed)
    {
        if (needed <= 0)
            return null;

        try
        {
            Directory.CreateDirectory(PathSanitizer.ForFileSystem(root));

            var pathRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(pathRoot))
                return null;

            var free = new DriveInfo(pathRoot).AvailableFreeSpace;
            if (free < needed)
                return $"не хватает места: нужно {Bytes(needed)}, свободно {Bytes(free)}";
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            // Не смогли узнать объём — это не повод отказывать.
        }

        return null;
    }

    private static string Bytes(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{value / (double)(1L << 10):0.#} КБ",
        _ => $"{value} Б",
    };

    private static void TryDeleteFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(PathSanitizer.ForFileSystem(path), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Уборка по возможности.
        }
    }

    private static void SafeDispose(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // Закрываем именно для того, чтобы порвать ожидающее чтение.
        }
    }

    private static async Task TryWriteErrorAsync(Stream stream, string reason, CancellationToken ct)
    {
        try
        {
            await FrameCodec.WriteAsync(stream, new ErrorFrame { Reason = reason }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Соединение уже разваливается.
        }
    }

    private void PublishProgress(TransferTracker tracker, TransferState state, bool force = false)
    {
        if (!force && !tracker.ShouldPublish())
            return;

        Publish(new TransferProgressEvent(tracker.Snapshot(state)));
    }

    private TransferProgress Finish(TransferTracker tracker, TransferState state, string? error = null)
    {
        var progress = tracker.Snapshot(state, error);
        Publish(new TransferProgressEvent(progress));
        return progress;
    }

    void IExchangeSink.OnIdentified(
        PeerId peer,
        string displayName,
        IPEndPoint remote,
        int listenPort,
        string? avatarTag,
        string? platform)
        => Publish(new PeerSeenEvent(peer, displayName, remote.Address, listenPort, 0, false, avatarTag, platform));

    void IExchangeSink.OnText(PeerId peer, Guid messageId, DateTimeOffset sentAt, string text)
        => Publish(new TextReceivedEvent(peer, messageId, sentAt, text));

    private Task HandleInboundAsync(Socket socket, CancellationToken ct)
    {
        // Одно чтение поля на оба применения. Прочитав его дважды, при смене картинки
        // между чтениями мы объявили бы отпечаток A, а отдавать отказывались бы уже B —
        // и спрашивающий больше не пришёл бы, попытка у него одна.
        var avatar = _avatar;
        return InboundExchange.HandleAsync(socket, LocalIdentity(avatar), avatar, this, ct);
    }

    private void OnObserved(DiscoveryObservation observation)
    {
        if (observation.IsFarewell)
        {
            Publish(new PeerFarewellEvent(observation.Peer, observation.Address));
            return;
        }

        Publish(new PeerSeenEvent(
            observation.Peer,
            observation.DisplayName,
            observation.Address,
            observation.ListenPort,
            observation.InterfaceIndex,
            ConnectSucceeded: false,
            observation.AvatarTag,
            observation.Platform));
    }

    private async Task RunExpiryAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepPeriod);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Publish(new SweepEvent());
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка.
        }
    }

    private IdentifyFrame LocalIdentity() => LocalIdentity(_avatar);

    private IdentifyFrame LocalIdentity(AvatarImage? avatar) => new()
    {
        PeerId = LocalId,
        DisplayName = _displayName,
        ListenPort = _listener.Port,
        AvatarTag = avatar?.Tag,
        Platform = PeerPlatforms.Wire(PeerPlatforms.Local),
    };

    private void Publish(EngineEvent e) => _events.Writer.TryWrite(e);

    private async Task RunEventLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case PeerSeenEvent seen:
                        ApplyPeerSeen(seen);
                        break;

                    case TextReceivedEvent text:
                        Raise(MessageReceived, new ChatMessage
                        {
                            Id = text.MessageId,
                            Peer = text.Peer,
                            Direction = MessageDirection.Incoming,
                            At = text.SentAt,
                            Text = text.Text,
                            State = MessageState.Received,
                        });
                        break;

                    case DiagnosticEvent diagnostic:
                        Raise(Diagnostic, diagnostic.Message);
                        break;

                    case PeerFarewellEvent farewell:
                        ApplyFarewell(farewell);
                        break;

                    case SweepEvent:
                        ApplySweep();
                        break;

                    case TransferProgressEvent progress:
                        Raise(TransferChanged, progress.Progress);
                        break;

                    case AvatarFetchedEvent fetched:
                        ApplyAvatarFetched(fetched);
                        break;

                    case AvatarFetchFailedEvent failed:
                        ApplyAvatarFetchFailed(failed);
                        break;

                    case BarrierEvent barrier:
                        barrier.Signal.TrySetResult();
                        break;

                    case InspectEvent inspect:
                        inspect.Signal.TrySetResult(_avatars.Count);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка.
        }
    }

    private void ApplyPeerSeen(PeerSeenEvent seen)
    {
        if (seen.Peer == LocalId)
            return;

        var now = DateTimeOffset.UtcNow;
        var isNew = !_peers.TryGetValue(seen.Peer, out var peer);

        if (peer is null)
        {
            peer = new PeerInfo { Id = seen.Peer };
            _peers[seen.Peer] = peer;
        }

        // Санация здесь, на входе в таблицу, а не у каждого источника события.
        // Источников три — датаграмма обнаружения, входящее представление и
        // представление в ответ на нашу отправку, — и третий про неё забыл: имя
        // собеседника, к которому мы подключились сами, попадало в список как есть.
        // Повторная санация уже обезвреженного имени ничего не меняет, а забыть
        // её в следующем источнике теперь негде.
        peer.DisplayName = DeviceNames.Sanitize(seen.DisplayName);
        peer.LastSeenUtc = now;
        peer.SetAvatarTag(seen.AvatarTag);
        peer.Platform = PeerPlatforms.Parse(seen.Platform);

        var endpoint = peer.TouchEndpoint(seen.Address, seen.ListenPort, seen.InterfaceIndex, now);
        if (seen.ConnectSucceeded)
        {
            endpoint.LastConnectOkUtc = now;
            endpoint.ConsecutiveFailures = 0;
        }

        RepublishRoutes();
        Raise(isNew ? PeerAppeared : PeerUpdated, peer.ToSnapshot(CachedAvatar(peer)));

        MaybeFetchAvatar(peer);
    }

    private AvatarImage? CachedAvatar(PeerInfo peer) =>
        peer.AvatarTag is { } tag && _avatars.TryGetValue(tag, out var image) ? image : null;

    /// <summary>
    /// Отправляет за картинкой, если за этим отпечатком ещё не ходили.
    /// </summary>
    /// <remarks>
    /// Вызывается на каждое объявление, то есть примерно раз в четыре секунды на пира.
    /// Поэтому все проверки — за постоянное время, а решение принимается одним
    /// добавлением в множество: сходили — и всё, повторов нет.
    /// </remarks>
    private void MaybeFetchAvatar(PeerInfo peer)
    {
        if (peer.AvatarTag is not { } tag)
            return;

        // Картинка уже есть — возможно, принесённая походом к совсем другому пиру.
        if (_avatars.ContainsKey(tag))
            return;

        if (!_routes.TryGetValue(peer.Id, out var routes) || routes.Count == 0)
            return;

        if (!_avatarAsked.Add(tag))
            return;

        var target = peer.Id;
        _ = Task.Run(() => FetchAvatarAsync(target, tag, routes), CancellationToken.None);
    }

    /// <summary>
    /// Поход за картинкой. Идёт вне цикла событий и ничего в движке не трогает:
    /// единственный обратный путь — событие в канале.
    /// </summary>
    private async Task FetchAvatarAsync(PeerId peer, string tag, IReadOnlyList<IPEndPoint> routes)
    {
        var identity = LocalIdentity();
        string? lastError = null;

        foreach (var endpoint in routes)
        {
            try
            {
                var image = await OutboundExchange
                    .FetchAvatarAsync(endpoint, identity, peer, tag, _lifetime.Token)
                    .ConfigureAwait(false);

                Publish(new AvatarFetchedEvent(peer, tag, image));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                lastError = e.Message;
            }
        }

        Publish(new AvatarFetchFailedEvent(peer, tag, lastError ?? "адрес пира неизвестен"));
    }

    private void ApplyAvatarFetched(AvatarFetchedEvent fetched)
    {
        if (_avatars.Count >= MaxCachedAvatars)
            return;

        _avatars[fetched.Tag] = fetched.Image;

        // Одна и та же картинка бывает у нескольких пиров — обновляем всех разом,
        // иначе остальным пришлось бы ждать собственного похода, которого не будет.
        foreach (var peer in _peers.Values)
        {
            if (peer.AvatarTag == fetched.Tag)
                Raise(PeerUpdated, peer.ToSnapshot(fetched.Image));
        }
    }

    /// <summary>
    /// Неудача не делает ничего, кроме строки в диагностике: чинить нечего,
    /// пометка «ходили» уже стоит и снимется только с уходом пира.
    /// </summary>
    private void ApplyAvatarFetchFailed(AvatarFetchFailedEvent failed)
    {
        if (!_peers.TryGetValue(failed.Peer, out var peer) || peer.AvatarTag != failed.Tag)
            return;

        Raise(Diagnostic, $"картинка от «{peer.DisplayName}» не получена: {failed.Reason}");
    }

    /// <summary>
    /// Прощание — подсказка, а не команда: его honour только если датаграмма пришла
    /// с адреса, который у этого пира действительно известен. Иначе любой в сети
    /// мог бы гасить чужие строки в списке.
    /// </summary>
    private void ApplyFarewell(PeerFarewellEvent farewell)
    {
        if (!_peers.TryGetValue(farewell.Peer, out var peer))
            return;

        var fromKnownAddress = peer.Endpoints.Any(x => x.Address.Equals(farewell.Address));
        if (!fromKnownAddress)
            return;

        _peers.Remove(farewell.Peer);
        RepublishRoutes();
        Raise(PeerGone, farewell.Peer);
    }

    /// <summary>
    /// Истечение по таймауту — основной механизм ухода: процесс могли убить,
    /// и прощание тогда никто не отправит.
    /// </summary>
    private void ApplySweep()
    {
        ReapAvatars();

        if (_peers.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var deadline = now - PeerTimeout;
        List<PeerId>? gone = null;
        var routesChanged = false;

        foreach (var (id, peer) in _peers)
        {
            var removed = peer.Endpoints.RemoveAll(x => x.LastSeenUtc < deadline);
            if (removed > 0)
                routesChanged = true;

            if (peer.Endpoints.Count == 0 || peer.LastSeenUtc < deadline)
            {
                gone ??= [];
                gone.Add(id);
            }
        }

        if (gone is not null)
        {
            foreach (var id in gone)
                _peers.Remove(id);

            routesChanged = true;
        }

        if (routesChanged)
            RepublishRoutes();

        if (gone is null)
            return;

        foreach (var id in gone)
            Raise(PeerGone, id);
    }

    /// <summary>
    /// Выбрасывает картинки и пометки, на которые больше никто не ссылается.
    /// </summary>
    /// <remarks>
    /// Чистить оба множества обязательно, и именно вместе. Останься пометка без картинки —
    /// отпечаток, за которым сходили неудачно, был бы помечен навсегда, и картинка не
    /// появилась бы даже после перезапуска пира. А так уход пира — единственное, что
    /// снимает пометку, и возвращение даёт ровно одну свежую попытку.
    ///
    /// Раннего выхода по равенству счётчиков здесь нет намеренно: счётчики совпадают
    /// ровно тогда, когда один лишний отпечаток соседствует с одним незакэшированным,
    /// и утечка стала бы беззвучной.
    /// </remarks>
    private void ReapAvatars()
    {
        if (_avatars.Count == 0 && _avatarAsked.Count == 0)
            return;

        var live = new HashSet<string>(_peers.Count + 1);

        foreach (var peer in _peers.Values)
        {
            if (peer.AvatarTag is { } tag)
                live.Add(tag);
        }

        if (_avatar?.Tag is { } mine)
            live.Add(mine);

        List<string>? doomed = null;

        foreach (var tag in _avatars.Keys)
        {
            if (!live.Contains(tag))
                (doomed ??= []).Add(tag);
        }

        if (doomed is not null)
        {
            foreach (var tag in doomed)
                _avatars.Remove(tag);
        }

        _avatarAsked.RemoveWhere(x => !live.Contains(x));
    }

    /// <summary>
    /// Пересобирает маршруты. Порядок — ранг: сначала адрес, по которому уже удалось
    /// соединиться, затем самый свежий.
    /// </summary>
    private void RepublishRoutes()
    {
        var routes = new Dictionary<PeerId, IReadOnlyList<IPEndPoint>>(_peers.Count);

        foreach (var (id, peer) in _peers)
        {
            routes[id] = peer.Endpoints
                .OrderByDescending(x => x.LastConnectOkUtc ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.ConsecutiveFailures)
                .ThenByDescending(x => x.LastSeenUtc)
                .Select(x => x.ToIPEndPoint())
                .ToArray();
        }

        _routes = routes;
    }

    private ChatMessage Outgoing(PeerId peer, string text, MessageState state) => new()
    {
        Id = Guid.NewGuid(),
        Peer = peer,
        Direction = MessageDirection.Outgoing,
        At = DateTimeOffset.Now,
        Text = text,
        State = state,
    };

    private static TextFrame ToFrame(ChatMessage message) => new()
    {
        MessageId = message.Id,
        SentAt = message.At,
        Text = message.Text,
    };

    /// <summary>
    /// Состояние одной передачи. Счётчики меняются из потока, который качает байты,
    /// поэтому только через <see cref="Interlocked"/>; в цикл событий уходит уже слепок.
    /// </summary>
    private sealed class TransferTracker
    {
        private const long PublishIntervalMs = 100;

        private long _transferred;
        private int _completed;
        private long _lastPublishTicks = -PublishIntervalMs;

        public required Guid Id { get; init; }

        public required PeerId Peer { get; init; }

        public required TransferDirection Direction { get; init; }

        public required string RootName { get; init; }

        public long TotalBytes { get; set; }

        public int TotalEntries { get; set; }

        public string? DestinationPath { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }

        public volatile bool CancelRequested;

        public void AddBytes(long count) => Interlocked.Add(ref _transferred, count);

        public void SetCompleted(int entries) => Volatile.Write(ref _completed, entries);

        /// <summary>Пропускает событие не чаще десяти раз в секунду: на гигабитной сети
        /// событие на каждый блок — это сотни обращений к UI в секунду.</summary>
        public bool ShouldPublish()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastPublishTicks);

            if (now - last < PublishIntervalMs)
                return false;

            return Interlocked.CompareExchange(ref _lastPublishTicks, now, last) == last;
        }

        public TransferProgress Snapshot(TransferState state, string? error = null) => new()
        {
            TransferId = Id,
            Peer = Peer,
            Direction = Direction,
            RootName = RootName,
            State = state,
            TotalBytes = TotalBytes,
            TransferredBytes = Interlocked.Read(ref _transferred),
            TotalEntries = TotalEntries,
            CompletedEntries = Volatile.Read(ref _completed),
            DestinationPath = DestinationPath,
            Error = error,
        };
    }

    private void Raise<T>(Action<T>? handler, T argument)
    {
        try
        {
            handler?.Invoke(argument);
        }
        catch
        {
            // Подписчик не имеет права остановить цикл событий.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        _listener.Dispose();

        foreach (var task in new[] { _loop, _accept, _discoveryTask, _expiryTask })
        {
            if (task is null)
                continue;

            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Остановка не должна падать.
            }
        }

        _discovery.Dispose();

        foreach (var gate in _sendLocks.Values)
            gate.Dispose();

        _lifetime.Dispose();
    }
}

internal abstract record EngineEvent;

internal sealed record PeerSeenEvent(
    PeerId Peer,
    string DisplayName,
    IPAddress Address,
    int ListenPort,
    int InterfaceIndex,
    bool ConnectSucceeded,
    string? AvatarTag = null,
    string? Platform = null) : EngineEvent;

internal sealed record TextReceivedEvent(
    PeerId Peer,
    Guid MessageId,
    DateTimeOffset SentAt,
    string Text) : EngineEvent;

internal sealed record PeerFarewellEvent(PeerId Peer, IPAddress Address) : EngineEvent;

/// <summary>Тик уборки: кто замолчал дольше положенного, тот ушёл.</summary>
internal sealed record SweepEvent : EngineEvent;

internal sealed record TransferProgressEvent(TransferProgress Progress) : EngineEvent;

internal sealed record DiagnosticEvent(string Message) : EngineEvent;

/// <summary>Метка в очереди: когда цикл до неё дошёл, всё, что было положено раньше, уже применено.</summary>
internal sealed record BarrierEvent(TaskCompletionSource Signal) : EngineEvent;

/// <summary>Картинка доехала и проверена.</summary>
internal sealed record AvatarFetchedEvent(PeerId Peer, string Tag, AvatarImage Image) : EngineEvent;

/// <summary>За картинкой сходили и не принесли. Второй раз не пойдём.</summary>
internal sealed record AvatarFetchFailedEvent(PeerId Peer, string Tag, string Reason) : EngineEvent;

/// <summary>
/// Вопрос к циклу о его собственном состоянии. Нужен тестам: словари движка
/// принадлежат циклу, и читать их из чужого потока нельзя даже после барьера —
/// таймер уборки продолжает тикать.
/// </summary>
internal sealed record InspectEvent(TaskCompletionSource<int> Signal) : EngineEvent;
