namespace ChudikChat.Core.Transfer;

/// <summary>
/// Источник поверх обычной файловой системы: один файл или дерево папок.
/// </summary>
/// <remarks>
/// Дерево обходится дважды — сначала ради сводки, потом ради содержимого.
/// Это сознательный размен: расход памяти не зависит от размера дерева,
/// а второй обход по тёплому кешу каталогов стоит дёшево.
/// </remarks>
public sealed class FileSystemTransferSource : ITransferSource
{
    private readonly string _path;

    private FileSystemTransferSource(string path, bool isDirectory)
    {
        _path = path;
        IsDirectory = isDirectory;
        RootName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(RootName))
            RootName = "передача";
    }

    public string RootName { get; }

    public bool IsDirectory { get; }

    public static FileSystemTransferSource ForFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("файл не найден", path);

        return new FileSystemTransferSource(Path.GetFullPath(path), isDirectory: false);
    }

    public static FileSystemTransferSource ForDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"папка не найдена: {path}");

        return new FileSystemTransferSource(Path.GetFullPath(path), isDirectory: true);
    }

    public static FileSystemTransferSource For(string path) =>
        Directory.Exists(path) ? ForDirectory(path) : ForFile(path);

    public Task<TransferSummary> ScanAsync(CancellationToken ct)
    {
        if (!IsDirectory)
        {
            var info = new FileInfo(_path);
            return Task.FromResult(new TransferSummary(1, 1, info.Length));
        }

        var entries = 0;
        var files = 0;
        var bytes = 0L;

        foreach (var entry in Walk(_path, ct))
        {
            entries++;
            if (entry.IsDirectory)
                continue;

            files++;
            bytes += entry.Size;
        }

        return Task.FromResult(new TransferSummary(entries, files, bytes));
    }

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!IsDirectory)
        {
            var info = new FileInfo(_path);
            yield return new SourceEntry
            {
                RelativePath = RootName,
                IsDirectory = false,
                Size = info.Length,
                Open = _ => ValueTask.FromResult(OpenForReading(_path)),
            };

            yield break;
        }

        foreach (var entry in Walk(_path, ct))
            yield return entry;

        await Task.CompletedTask;
    }

    /// <summary>
    /// Обход дерева. Пути в передаче всегда начинаются с имени верхней папки,
    /// чтобы у получателя дерево воспроизводилось целиком, а не вываливалось наружу.
    /// </summary>
    private IEnumerable<SourceEntry> Walk(string root, CancellationToken ct)
    {
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, RootName));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (current, relative) = pending.Pop();

            yield return new SourceEntry
            {
                RelativePath = relative,
                IsDirectory = true,
                Size = 0,
            };

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // Недоступную ветку пропускаем: одна закрытая папка не повод срывать передачу.
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                // Симлинки и точки повторного разбора не разворачиваем: иначе можно уйти
                // в бесконечный цикл или утащить полдиска по чужой ссылке.
                if (child.LinkTarget is not null)
                    continue;

                var childRelative = $"{relative}/{child.Name}";

                if (child is DirectoryInfo)
                {
                    pending.Push((child.FullName, childRelative));
                    continue;
                }

                var file = (FileInfo)child;
                var fullPath = file.FullName;

                yield return new SourceEntry
                {
                    RelativePath = childRelative,
                    IsDirectory = false,
                    Size = file.Length,
                    Open = _ => ValueTask.FromResult(OpenForReading(fullPath)),
                };
            }
        }
    }

    /// <remarks>
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> — чтобы открытый в другой программе
    /// файл журнала не срывал передачу целиком.
    /// </remarks>
    private static Stream OpenForReading(string path) => new FileStream(
        PathSanitizer.ForFileSystem(path),
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
}
