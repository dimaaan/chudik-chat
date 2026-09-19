using System.Net;
using System.Net.Sockets;
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
