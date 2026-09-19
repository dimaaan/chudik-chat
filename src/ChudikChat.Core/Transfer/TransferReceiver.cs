using System.Buffers;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Transfer;

/// <summary>
/// Принимающая половина передачи. Вызывается уже после согласия пользователя
/// и после создания отдельной папки под эту передачу.
/// </summary>
/// <remarks>
/// Отправитель недоверенный, поэтому здесь три рубежа: путь каждой записи проходит
/// <see cref="PathSanitizer"/>, содержимое пишется во временный <c>.part</c> и переименовывается
/// только целиком, а сама папка назначения создана пустой и принадлежит только этой передаче.
/// </remarks>
public static class TransferReceiver
{
    public const int BufferBytes = 256 * 1024;

    public static async Task RunAsync(
        Stream stream,
        TransferOfferFrame offer,
        string destinationRoot,
        Action<long> onBytesReceived,
        Action<int> onEntryCompleted,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(
            PathSanitizer.PathComparison == StringComparison.Ordinal
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            var expectedIndex = 0;
            var receivedBytes = 0L;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var frame = await FrameCodec.ReadAsync(stream, ct).ConfigureAwait(false);

                if (frame is TransferEndFrame end)
                {
                    if (end.TransferId != offer.TransferId)
                        throw new ProtocolException("завершение относится к чужой передаче");

                    return;
                }

                if (frame is null)
                    throw new ProtocolException("отправитель закрыл соединение, не завершив передачу");

                if (frame is not TransferEntryFrame entry)
                    throw new ProtocolException($"вместо записи пришёл {frame.GetType().Name}");

                // Отправитель обещал определённое количество записей и байт.
                // Без этой проверки он мог бы лить поток бесконечно.
                if (expectedIndex >= offer.EntryCount)
                    throw new ProtocolException($"записей больше обещанных {offer.EntryCount}");

                if (entry.Index != expectedIndex)
                    throw new ProtocolException($"запись №{entry.Index}, ожидалась №{expectedIndex}");

                var segments = PathSanitizer.Validate(entry.RelativePath);
                if (!seen.Add(string.Join('/', segments)))
                    throw new ProtocolException($"путь встречается дважды: «{entry.RelativePath}»");

                var destination = PathSanitizer.Resolve(destinationRoot, entry.RelativePath);

                if (entry.IsDirectory)
                {
                    if (entry.Size != 0)
                        throw new ProtocolException("у папки заявлен ненулевой размер");

                    Directory.CreateDirectory(PathSanitizer.ForFileSystem(destination));
                }
                else
                {
                    if (entry.Size < 0)
                        throw new ProtocolException($"отрицательный размер записи: {entry.Size}");

                    receivedBytes += entry.Size;
                    if (receivedBytes > offer.TotalBytes)
                        throw new ProtocolException($"байтов больше обещанных {offer.TotalBytes}");

                    await ReceiveFileAsync(stream, destination, entry.Size, buffer, onBytesReceived, ct)
                        .ConfigureAwait(false);
                }

                expectedIndex++;
                onEntryCompleted(expectedIndex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReceiveFileAsync(
        Stream stream,
        string destination,
        long size,
        byte[] buffer,
        Action<long> onBytesReceived,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(PathSanitizer.ForFileSystem(directory));

        var partial = PathSanitizer.ForFileSystem(destination + ".part");
        var final = PathSanitizer.ForFileSystem(destination);

        try
        {
            // CreateNew, а не Create: если по этому пути что-то есть — а в свежей папке
            // быть не может — лучше громко упасть, чем писать сквозь чужую ссылку.
            await using (var file = new FileStream(partial, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                PreallocationSize = size,
                BufferSize = 0,
            }))
            {
                var remaining = size;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(buffer.Length, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);

                    if (read == 0)
                        throw new ProtocolException("поток оборвался внутри файла");

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                    onBytesReceived(read);
                }
            }

            // Переименование — единственный момент, когда файл становится «настоящим».
            // Поэтому недописанного файла, выглядящего целым, не бывает.
            File.Move(partial, final, overwrite: false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Уборка по возможности: папку целиком всё равно удалит вызывающий.
        }
        catch (UnauthorizedAccessException)
        {
            // Там же.
        }
    }
}
