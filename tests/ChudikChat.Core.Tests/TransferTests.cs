using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ChudikChat.Core;
using ChudikChat.Core.Model;
using ChudikChat.Core.Transfer;

namespace ChudikChat.Core.Tests;

public sealed class TransferTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "chudik-tests",
        Guid.NewGuid().ToString("N")[..8]);

    public TransferTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка переживёт.
        }
    }

    [Fact]
    public async Task Single_file_arrives_byte_for_byte()
    {
        var source = Path.Combine(_sandbox, "исходный.bin");
        var content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        await File.WriteAllBytesAsync(source, content, TestToken);

        var (progress, destination) = await SendAsync(source, engine => engine.SendFileAsync);

        Assert.Equal(TransferState.Completed, progress.State);

        var received = Path.Combine(destination, "исходный.bin");
        Assert.True(File.Exists(received), $"не найдено: {received}");
        Assert.Equal(content, await File.ReadAllBytesAsync(received, TestToken));
    }

    [Fact]
    public async Task Awkward_tree_survives_the_round_trip()
    {
        var root = Path.Combine(_sandbox, "дерево");
        var expected = BuildAwkwardTree(root);

        var (progress, destination) = await SendAsync(root, engine => engine.SendFolderAsync);

        Assert.Equal(TransferState.Completed, progress.State);

        var received = Path.Combine(destination, "дерево");
        Assert.True(Directory.Exists(received));

        foreach (var (relative, bytes) in expected)
        {
            var path = Path.Combine(received, relative.Replace('/', Path.DirectorySeparatorChar));

            if (bytes is null)
            {
                Assert.True(Directory.Exists(path), $"пустая папка не доехала: {relative}");
                continue;
            }

            Assert.True(File.Exists(path), $"файл не доехал: {relative}");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestToken));
        }

        // Ничего лишнего: .part-файлов после успешной передачи остаться не должно.
        Assert.Empty(Directory.GetFiles(received, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Declined_transfer_leaves_nothing_behind()
    {
        var source = Path.Combine(_sandbox, "ненужный.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1024), TestToken);

        var downloads = Path.Combine(_sandbox, "принято");
        Directory.CreateDirectory(downloads);

        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        bob.TransferDecider = (_, _) => Task.FromResult(TransferDecision.Decline("не сегодня"));

        await StartAndIntroduceAsync(alice, bob);

        var progress = await alice.SendFileAsync(bob.LocalId, source, TestToken);

        Assert.Equal(TransferState.Declined, progress.State);
        Assert.Contains("не сегодня", progress.Error);
        Assert.Empty(Directory.GetDirectories(downloads));
    }

    [Fact]
    public async Task Cancelled_transfer_leaves_no_partial_files()
    {
        var downloads = Path.Combine(_sandbox, "принято");
        Directory.CreateDirectory(downloads);

        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        bob.TransferDecider = (_, _) => Task.FromResult(TransferDecision.Accept(downloads));

        var running = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.TransferChanged += p =>
        {
            if (p.State == TransferState.Running && p.TransferredBytes > 0)
                running.TrySetResult(p.TransferId);
        };

        await StartAndIntroduceAsync(alice, bob);

        // Источник отдаёт байты медленно, чтобы отмена пришлась на середину, а не на конец.
        var source = new ThrottledSource("медленный.bin", 16 * 1024 * 1024, TimeSpan.FromMilliseconds(40));
        var send = alice.SendAsync(bob.LocalId, source, TestToken);

        var transferId = await running.Task.WaitAsync(Patience);
        Assert.True(alice.CancelTransfer(transferId), "передача должна быть известна движку");

        var progress = await send.WaitAsync(Patience);
        Assert.Equal(TransferState.Cancelled, progress.State);

        // Получателю нужен миг, чтобы заметить обрыв и снести свою папку.
        await WaitUntilAsync(
            () => Directory.GetDirectories(downloads).Length == 0,
            "папка назначения должна быть удалена целиком");
    }

    [Fact]
    public async Task Sender_that_escapes_the_destination_is_refused()
    {
        var downloads = Path.Combine(_sandbox, "принято");
        Directory.CreateDirectory(downloads);

        await using var alice = new ChatEngine { LocalDisplayName = "Злоумышленник" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        bob.TransferDecider = (_, _) => Task.FromResult(TransferDecision.Accept(downloads));

        await StartAndIntroduceAsync(alice, bob);

        var progress = await alice.SendAsync(bob.LocalId, new HostileSource(), TestToken);

        Assert.Equal(TransferState.Failed, progress.State);
        Assert.Empty(Directory.GetDirectories(downloads));
        Assert.False(File.Exists(Path.Combine(_sandbox, "побег.txt")), "запись ушла за пределы папки назначения");
    }

    [Fact]
    public async Task Transfer_does_not_block_the_chat()
    {
        var downloads = Path.Combine(_sandbox, "принято");
        Directory.CreateDirectory(downloads);

        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        bob.TransferDecider = (_, _) => Task.FromResult(TransferDecision.Accept(downloads));

        await StartAndIntroduceAsync(alice, bob);

        // Подписываемся после знакомства, иначе поймаем приветственное сообщение.
        var heard = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += m => heard.TrySetResult(m);

        var source = new ThrottledSource("долгий.bin", 8 * 1024 * 1024, TimeSpan.FromMilliseconds(30));
        var send = alice.SendAsync(bob.LocalId, source, TestToken);

        // Пока идёт передача, переписка обязана работать: на то и отдельное соединение.
        var sent = await alice.SendTextAsync(bob.LocalId, "я пока пишу", TestToken);

        Assert.Equal(MessageState.Delivered, sent.State);
        Assert.Equal("я пока пишу", (await heard.Task.WaitAsync(Patience)).Text);

        Assert.Equal(TransferState.Completed, (await send.WaitAsync(Patience)).State);
    }

    private static CancellationToken TestToken => CancellationToken.None;

    /// <summary>Строит дерево из случаев, на которых обычно и ломается передача папок.</summary>
    private static Dictionary<string, byte[]?> BuildAwkwardTree(string root)
    {
        var expected = new Dictionary<string, byte[]?>();

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "пустая папка"));
        expected["пустая папка"] = null;

        Add("файл.txt", RandomNumberGenerator.GetBytes(1000));
        Add("нулевой.bin", []);
        Add("имя с пробелами.и.точками.txt", RandomNumberGenerator.GetBytes(64));
        Add("вложенная/один.dat", RandomNumberGenerator.GetBytes(128 * 1024));
        Add("вложенная/ещё/два.dat", RandomNumberGenerator.GetBytes(4096));

        var deep = string.Join('/', Enumerable.Range(1, 10).Select(i => $"уровень{i}"));
        Add($"{deep}/глубокий.txt", RandomNumberGenerator.GetBytes(16));

        return expected;

        void Add(string relative, byte[] bytes)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            expected[relative] = bytes;
        }
    }

    private async Task<(TransferProgress Progress, string Destination)> SendAsync(
        string what,
        Func<ChatEngine, Func<PeerId, string, CancellationToken, Task<TransferProgress>>> pick)
    {
        var downloads = Path.Combine(_sandbox, "принято");
        Directory.CreateDirectory(downloads);

        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        string? destination = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bob.TransferDecider = (_, _) => Task.FromResult(TransferDecision.Accept(downloads));
        bob.TransferChanged += p =>
        {
            if (p.State != TransferState.Completed)
                return;

            destination = p.DestinationPath;
            completed.TrySetResult();
        };

        await StartAndIntroduceAsync(alice, bob);

        var progress = await pick(alice)(bob.LocalId, what, TestToken);

        await completed.Task.WaitAsync(Patience);

        Assert.NotNull(destination);
        return (progress, destination!);
    }

    /// <summary>Знакомит движки напрямую по адресу, чтобы тест не зависел от обнаружения.</summary>
    private static async Task StartAndIntroduceAsync(ChatEngine alice, ChatEngine bob)
    {
        await alice.StartAsync();
        await bob.StartAsync();

        var hello = await alice.SendTextToAsync(
            new IPEndPoint(IPAddress.Loopback, bob.ListenPort),
            "знакомство",
            TestToken);

        Assert.Equal(MessageState.Delivered, hello.State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(50, TestToken);
        }

        Assert.Fail(because);
    }

    /// <summary>Источник, отдающий байты порциями с паузой: нужен, чтобы успеть отменить.</summary>
    private sealed class ThrottledSource(string name, long size, TimeSpan delayPerChunk) : ITransferSource
    {
        public string RootName => name;

        public bool IsDirectory => false;

        public Task<TransferSummary> ScanAsync(CancellationToken ct) =>
            Task.FromResult(new TransferSummary(1, 1, size));

        public async IAsyncEnumerable<SourceEntry> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SourceEntry
            {
                RelativePath = name,
                IsDirectory = false,
                Size = size,
                Open = _ => ValueTask.FromResult<Stream>(new SlowStream(size, delayPerChunk)),
            };

            await Task.CompletedTask;
        }
    }

    private sealed class SlowStream(long length, TimeSpan delay) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);

            var remaining = length - _position;
            if (remaining <= 0)
                return 0;

            var count = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..count].Fill(0xAB);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => _position;

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Отправитель, пытающийся записать за пределы папки назначения.</summary>
    private sealed class HostileSource : ITransferSource
    {
        public string RootName => "подарок";

        public bool IsDirectory => true;

        public Task<TransferSummary> ScanAsync(CancellationToken ct) =>
            Task.FromResult(new TransferSummary(2, 1, 8));

        public async IAsyncEnumerable<SourceEntry> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SourceEntry { RelativePath = "подарок", IsDirectory = true, Size = 0 };

            yield return new SourceEntry
            {
                RelativePath = "../../побег.txt",
                IsDirectory = false,
                Size = 8,
                Open = _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[8])),
            };

            await Task.CompletedTask;
        }
    }
}
