using System.Buffers;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Transfer;

/// <summary>Получатель отказался принимать передачу.</summary>
public sealed class TransferDeclinedException(string? reason)
    : Exception(reason is null ? "получатель отказался" : $"получатель отказался: {reason}")
{
    public string? Reason { get; } = reason;
}

/// <summary>
/// Отправляющая половина передачи. Работает поверх уже установленного соединения,
/// на котором обе стороны представились.
/// </summary>
public static class TransferSender
{
    public const int BufferBytes = 256 * 1024;

    /// <summary>Сколько ждём решения человека на той стороне.</summary>
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(
        Stream stream,
        Guid transferId,
        ITransferSource source,
        TransferSummary summary,
        Action<long> onBytesSent,
        Action<int> onEntryCompleted,
        CancellationToken ct)
    {
        await FrameCodec.WriteAsync(stream, new TransferOfferFrame
        {
            TransferId = transferId,
            RootName = source.RootName,
            IsDirectory = source.IsDirectory,
            EntryCount = summary.EntryCount,
            FileCount = summary.FileCount,
            TotalBytes = summary.TotalBytes,
        }, ct).ConfigureAwait(false);

        await ReadDecisionAsync(stream, transferId, ct).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            var index = 0;

            await foreach (var entry in source.EnumerateAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    await FrameCodec.WriteAsync(stream, new TransferEntryFrame
                    {
                        Index = index,
                        RelativePath = entry.RelativePath,
                        Size = 0,
                        IsDirectory = true,
                    }, ct).ConfigureAwait(false);
                }
                else
                {
                    await SendFileAsync(stream, entry, index, buffer, onBytesSent, ct).ConfigureAwait(false);
                }

                index++;
                onEntryCompleted(index);
            }

            await FrameCodec.WriteAsync(stream, new TransferEndFrame { TransferId = transferId }, ct)
                .ConfigureAwait(false);

            await ReadFinalAckAsync(stream, transferId, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task SendFileAsync(
        Stream stream,
        SourceEntry entry,
        int index,
        byte[] buffer,
        Action<long> onBytesSent,
        CancellationToken ct)
    {
        if (entry.Open is null)
            throw new InvalidOperationException($"нечем открыть {entry.RelativePath}");

        await using var content = await entry.Open(ct).ConfigureAwait(false);

        // Размер берём у уже открытого файла: между обходом дерева и отправкой
        // он мог измениться, и заявленная длина должна совпадать с тем, что реально уйдёт.
        var size = content.CanSeek ? content.Length : entry.Size;

        await FrameCodec.WriteAsync(stream, new TransferEntryFrame
        {
            Index = index,
            RelativePath = entry.RelativePath,
            Size = size,
            IsDirectory = false,
        }, ct).ConfigureAwait(false);

        var remaining = size;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = await content.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);

            if (read == 0)
            {
                // Файл укоротился под нами. Дописать нулями нельзя: получатель прочтёт
                // следующий заголовок из середины данных и тихо испортит всё остальное.
                throw new IOException($"{entry.RelativePath} короче заявленного на {remaining} Б");
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
            onBytesSent(read);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task ReadDecisionAsync(Stream stream, Guid transferId, CancellationToken ct)
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
        patience.CancelAfter(DecisionTimeout);

        WireFrame? frame;
        try
        {
            frame = await FrameCodec.ReadAsync(stream, patience.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("получатель не ответил на предложение");
        }

        switch (frame)
        {
            case TransferDecisionFrame decision when decision.TransferId != transferId:
                throw new ProtocolException("решение относится к чужой передаче");

            case TransferDecisionFrame { Accepted: true }:
                return;

            case TransferDecisionFrame declined:
                throw new TransferDeclinedException(declined.Reason);

            case ErrorFrame error:
                throw new ProtocolException($"получатель отказал: {error.Reason}");

            case null:
                throw new ProtocolException("получатель закрыл соединение, не ответив");

            default:
                throw new ProtocolException($"вместо решения пришёл {frame.GetType().Name}");
        }
    }

    private static async Task ReadFinalAckAsync(Stream stream, Guid transferId, CancellationToken ct)
    {
        var frame = await FrameCodec.ReadAsync(stream, ct).ConfigureAwait(false);

        switch (frame)
        {
            case AckFrame ack when ack.MessageId == transferId:
                return;

            case ErrorFrame error:
                throw new ProtocolException($"получатель не принял передачу: {error.Reason}");

            case null:
                throw new ProtocolException("получатель закрыл соединение, не подтвердив приём");

            default:
                throw new ProtocolException($"вместо подтверждения пришёл {frame.GetType().Name}");
        }
    }
}
