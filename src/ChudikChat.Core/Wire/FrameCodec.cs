using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace ChudikChat.Core.Wire;

/// <summary>
/// Кадрирование управляющего обмена: 4 байта длины (big-endian) и UTF-8 JSON следом.
/// </summary>
public static class FrameCodec
{
    private const int HeaderBytes = 4;

    public static async ValueTask WriteAsync(Stream stream, WireFrame frame, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, typeof(WireFrame), WireJsonContext.Default);

        if (payload.Length > ProtocolConstants.MaxFrameBytes)
            throw new ProtocolException($"кадр {payload.Length} Б превышает потолок {ProtocolConstants.MaxFrameBytes} Б");

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Читает один кадр. Возвращает <c>null</c>, если собеседник штатно закрыл соединение
    /// на границе кадра — это не ошибка, а нормальное завершение обмена.
    /// </summary>
    public static async ValueTask<WireFrame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        var headerRead = await stream
            .ReadAtLeastAsync(header, HeaderBytes, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);

        if (headerRead == 0)
            return null;

        if (headerRead < HeaderBytes)
            throw new ProtocolException("соединение оборвалось внутри заголовка кадра");

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > ProtocolConstants.MaxFrameBytes)
            throw new ProtocolException($"недопустимая длина кадра: {length}");

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            return Deserialize(buffer.AsSpan(0, length));
        }
        catch (EndOfStreamException e)
        {
            throw new ProtocolException("соединение оборвалось внутри кадра", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static WireFrame Deserialize(ReadOnlySpan<byte> utf8)
    {
        WireFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize(utf8, typeof(WireFrame), WireJsonContext.Default) as WireFrame;
        }
        // NotSupportedException — это неизвестный дискриминатор "t": так выглядит кадр
        // из более новой сборки. Без этой ветки он вылетел бы мимо ProtocolException
        // и потерял причину по дороге.
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new ProtocolException("кадр не разбирается как JSON протокола", e);
        }

        if (frame is null)
            throw new ProtocolException("пустой кадр");

        if (frame.Version != ProtocolConstants.Version)
            throw new ProtocolException($"версия протокола {frame.Version}, ожидалась {ProtocolConstants.Version}");

        return frame;
    }
}
