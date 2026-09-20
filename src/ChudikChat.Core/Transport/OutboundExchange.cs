using System.Net;
using System.Net.Sockets;
using ChudikChat.Core.Model;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Transport;

/// <summary>
/// Установленное исходящее соединение, на котором обе стороны уже представились.
/// </summary>
public sealed class OutboundConnection : IAsyncDisposable
{
    internal OutboundConnection(Socket socket, NetworkStream stream, IdentifyFrame remote)
    {
        Socket = socket;
        Stream = stream;
        Remote = remote;
    }

    public Socket Socket { get; }

    public NetworkStream Stream { get; }

    /// <summary>Кто на самом деле оказался по этому адресу.</summary>
    public IdentifyFrame Remote { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        Socket.Dispose();
    }
}

/// <summary>
/// Исходящий обмен: короткое соединение на одно взаимодействие.
/// Постоянных сессий нет — поэтому нет ни встречных коннектов, ни keepalive,
/// ни потерянных кадров при переподключении.
/// </summary>
public static class OutboundExchange
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TextTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan AvatarTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Соединяется и обменивается представлениями. Сроки здесь короткие и относятся
    /// только к рукопожатию: дальше соединением распоряжается вызывающий, и передача
    /// файла на четыре гигабайта не должна упираться в таймаут обмена.
    /// </summary>
    public static async Task<OutboundConnection> ConnectAsync(
        IPEndPoint target,
        IdentifyFrame localIdentity,
        CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        NetworkStream? stream = null;
        try
        {
            await ConnectSocketAsync(socket, target, ct).ConfigureAwait(false);

            stream = new NetworkStream(socket, ownsSocket: false);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(HandshakeTimeout);

            await FrameCodec.WriteAsync(stream, localIdentity, handshake.Token).ConfigureAwait(false);

            var greeting = await FrameCodec.ReadAsync(stream, handshake.Token).ConfigureAwait(false);
            var remote = greeting switch
            {
                IdentifyFrame identify => identify,
                ErrorFrame error => throw new ProtocolException($"собеседник отказал: {error.Reason}"),
                null => throw new ProtocolException("собеседник закрыл соединение, не представившись"),
                _ => throw new ProtocolException($"вместо представления пришёл {greeting.GetType().Name}"),
            };

            return new OutboundConnection(socket, stream, remote);
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);

            socket.Dispose();
            throw;
        }
    }

    /// <summary>Отправляет текст и дожидается подтверждения.</summary>
    public static async Task<IdentifyFrame> SendTextAsync(
        IPEndPoint target,
        IdentifyFrame localIdentity,
        TextFrame text,
        CancellationToken ct)
    {
        await using var connection = await ConnectAsync(target, localIdentity, ct).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TextTimeout);

        await FrameCodec.WriteAsync(connection.Stream, text, deadline.Token).ConfigureAwait(false);

        var reply = await FrameCodec.ReadAsync(connection.Stream, deadline.Token).ConfigureAwait(false);
        switch (reply)
        {
            case AckFrame ack when ack.MessageId == text.MessageId:
                return connection.Remote;

            case AckFrame ack:
                throw new ProtocolException($"подтверждение на чужое сообщение {ack.MessageId:N}");

            case ErrorFrame error:
                throw new ProtocolException($"собеседник отказал: {error.Reason}");

            case null:
                throw new ProtocolException("собеседник закрыл соединение без подтверждения");

            default:
                throw new ProtocolException($"вместо подтверждения пришёл {reply.GetType().Name}");
        }
    }

    /// <summary>
    /// Забирает картинку с известным отпечатком отдельным коротким соединением.
    /// </summary>
    /// <remarks>
    /// Отдельным — потому что представление уходит в каждом соединении, а картинка весит
    /// десятки килобайт: вези её представление, и она пересылалась бы заново в каждом
    /// сообщении и в каждой передаче файлов.
    /// </remarks>
    public static async Task<AvatarImage> FetchAvatarAsync(
        IPEndPoint target,
        IdentifyFrame localIdentity,
        PeerId expected,
        string tag,
        CancellationToken ct)
    {
        await using var connection = await ConnectAsync(target, localIdentity, ct).ConfigureAwait(false);

        // Та же проверка, что и перед передачей файлов: по адресу мог оказаться другой пир.
        if (connection.Remote.PeerId != expected)
            throw new ProtocolException($"по адресу {target} оказался другой пир");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(AvatarTimeout);

        await FrameCodec.WriteAsync(connection.Stream, new AvatarRequestFrame { Tag = tag }, deadline.Token)
            .ConfigureAwait(false);

        return await ReadAvatarAsync(connection.Stream, tag, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Читает и проверяет ответ на запрос картинки. Вынесено из
    /// <see cref="FetchAvatarAsync"/> ради тестов: враждебные ответы проверяются
    /// над обычным потоком, без сокета и без второго движка.
    /// </summary>
    /// <remarks>
    /// Присланные байты перехешируются и сверяются с запрошенным отпечатком. Без этого
    /// пир, у которого мы спросили картинку A, отдавал бы что угодно — и отравил бы кэш,
    /// из которого картинку берут все остальные пиры с тем же отпечатком.
    /// </remarks>
    internal static async Task<AvatarImage> ReadAvatarAsync(Stream stream, string tag, CancellationToken ct)
    {
        var reply = await FrameCodec.ReadAsync(stream, ct).ConfigureAwait(false);

        switch (reply)
        {
            case AvatarFrame avatar when string.Equals(avatar.Tag, tag, StringComparison.Ordinal):
                if (!Avatars.TryCreate(avatar.Bytes, out var image))
                    throw new ProtocolException("присланное не похоже на картинку");

                if (!string.Equals(image.Tag, tag, StringComparison.Ordinal))
                    throw new ProtocolException("картинка не соответствует отпечатку");

                return image;

            case AvatarFrame avatar:
                throw new ProtocolException($"пришла картинка с чужим отпечатком {avatar.Tag}");

            case ErrorFrame error:
                throw new ProtocolException($"собеседник отказал: {error.Reason}");

            case null:
                throw new ProtocolException("собеседник закрыл соединение без картинки");

            default:
                throw new ProtocolException($"вместо картинки пришёл {reply.GetType().Name}");
        }
    }

    private static async Task ConnectSocketAsync(Socket socket, IPEndPoint target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await socket.ConnectAsync(target, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{target} не ответил за {ConnectTimeout.TotalMilliseconds:0} мс");
        }
    }
}
