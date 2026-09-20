using System.Net;
using System.Net.Sockets;
using ChudikChat.Core.Model;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Transport;

/// <summary>
/// Обслуживание одного входящего соединения.
/// Порядок обмена: собеседник представляется, мы представляемся в ответ,
/// дальше идут полезные кадры, каждый со своим подтверждением.
/// </summary>
public static class InboundExchange
{
    /// <summary>Сколько ждём следующий кадр, прежде чем счесть собеседника зависшим.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <param name="localAvatar">
    /// Наша картинка — та самая, чей отпечаток стоит в <paramref name="localIdentity"/>.
    /// Передаётся параметром, а не спрашивается у стока: сток обязан быть неблокирующим,
    /// а так видно, что отпечаток в представлении и отдаваемые байты взяты из одного
    /// чтения поля движка.
    /// </param>
    public static async Task HandleAsync(
        Socket socket,
        IdentifyFrame localIdentity,
        AvatarImage? localAvatar,
        IExchangeSink sink,
        CancellationToken ct)
    {
        socket.NoDelay = true;
        var remote = (IPEndPoint)socket.RemoteEndPoint!;

        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var first = await ReadWithTimeoutAsync(stream, ct).ConfigureAwait(false);
        if (first is null)
            return;

        if (first is not IdentifyFrame identify)
        {
            await TryWriteErrorAsync(stream, "ожидался кадр представления", ct).ConfigureAwait(false);
            return;
        }

        if (identify.ListenPort is <= 0 or > 65535)
        {
            await TryWriteErrorAsync(stream, "недопустимый порт в представлении", ct).ConfigureAwait(false);
            return;
        }

        // Соединение с самим собой. Встречается не только при отладке: человек
        // может ввести собственный адрес в «добавить по адресу».
        //
        // Отказ стоит здесь, а не на разборе текстового кадра, и это важно.
        // Проверка ниже по течению закрыла бы только переписку, а предложение
        // передачи пошло бы дальше — и приёмник послушно разложил бы файлы
        // самому себе в папку загрузок. Здесь одним условием закрыты оба пути.
        //
        // Отвечаем отказом, а не молча закрываем соединение: OutboundExchange
        // разворачивает ErrorFrame в текст причины, и человек видит, что он
        // указал свой же адрес, вместо невнятного обрыва.
        if (identify.PeerId == localIdentity.PeerId)
        {
            await TryWriteErrorAsync(stream, "это соединение с самим собой", ct).ConfigureAwait(false);
            return;
        }

        var displayName = DeviceNames.Sanitize(identify.DisplayName);
        sink.OnIdentified(
            identify.PeerId,
            displayName,
            remote,
            identify.ListenPort,
            Avatars.SanitizeTag(identify.AvatarTag));

        // Отвечаем своим представлением: так звонящий узнаёт, кто ему ответил,
        // даже если соединение начато вручную по IP и discovery не участвовал.
        await FrameCodec.WriteAsync(stream, localIdentity, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadWithTimeoutAsync(stream, ct).ConfigureAwait(false);
            if (frame is null)
                return;

            switch (frame)
            {
                case TextFrame text:
                    sink.OnText(identify.PeerId, text.MessageId, text.SentAt, text.Text);
                    await FrameCodec.WriteAsync(stream, new AckFrame { MessageId = text.MessageId }, ct)
                        .ConfigureAwait(false);
                    break;

                // Сравниваем отпечатки, а не отдаём «хоть что-нибудь». Ответив свежими
                // байтами на устаревший отпечаток, мы завалили бы спрашивающему проверку
                // хеша, а попытка у него единственная — второй раз он не придёт. Отказ же
                // честен: он вернётся, когда объявление принесёт новый отпечаток.
                case AvatarRequestFrame request:
                    if (localAvatar is null || !string.Equals(request.Tag, localAvatar.Tag, StringComparison.Ordinal))
                    {
                        await TryWriteErrorAsync(stream, "такой картинки у меня нет", ct).ConfigureAwait(false);
                        return;
                    }

                    await FrameCodec
                        .WriteAsync(stream, new AvatarFrame { Tag = localAvatar.Tag, Bytes = localAvatar.Buffer }, ct)
                        .ConfigureAwait(false);
                    break;

                case TransferOfferFrame offer:
                    // Дальше соединением распоряжается приёмник: ждать решения человека
                    // и вычитывать гигабайты в общем цикле с таймаутом на кадр нельзя.
                    await sink.HandleTransferAsync(identify.PeerId, displayName, offer, stream, ct)
                        .ConfigureAwait(false);
                    return;

                default:
                    await TryWriteErrorAsync(stream, $"неожиданный кадр {frame.GetType().Name}", ct)
                        .ConfigureAwait(false);
                    return;
            }
        }
    }

    private static async ValueTask<WireFrame?> ReadWithTimeoutAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleTimeout);

        try
        {
            return await FrameCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProtocolException($"собеседник молчал дольше {IdleTimeout.TotalSeconds:0} с");
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
            // Соединение уже разваливается — сообщить о причине было попыткой вежливости.
        }
    }
}
