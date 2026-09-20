using System.Net;
using System.Net.Sockets;
using ChudikChat.Core;
using ChudikChat.Core.Model;
using ChudikChat.Core.Transport;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Tests;

/// <summary>
/// Обмен картинками: по loopback между двумя движками и отдельно — разбор ответа,
/// в том числе враждебного.
/// </summary>
public class AvatarExchangeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Avatar_travels_to_the_other_engine()
    {
        var picture = Picture(seed: 1);

        await using var alice = new ChatEngine { LocalDisplayName = "Алиса", LocalAvatar = picture };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        await alice.StartAsync();
        await bob.StartAsync();

        var bobSeesTheFace = PeerWithAvatar(bob, alice.LocalId);

        // Сообщение учит Боба и маршруту до Алисы, и отпечатку её картинки.
        await alice.SendTextToAsync(Loopback(bob.ListenPort), "привет");

        var seen = await bobSeesTheFace.WaitAsync(Patience);

        Assert.NotNull(seen.Avatar);
        Assert.Equal(picture.Tag, seen.Avatar.Tag);
        Assert.Equal(picture.Bytes.ToArray(), seen.Avatar.Bytes.ToArray());
    }

    [Fact]
    public async Task Peer_without_an_avatar_is_never_asked()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        await alice.StartAsync();
        await bob.StartAsync();

        await alice.SendTextToAsync(Loopback(bob.ListenPort), "привет");
        await bob.FlushAsync();

        Assert.Equal(0, await bob.CachedAvatarCountAsync());
    }

    /// <summary>
    /// Пир, который на запрос картинки отвечает обрывом, должен получить ровно один
    /// заход — сколько бы объявлений с этим отпечатком ни пришло следом.
    /// </summary>
    [Fact]
    public async Task A_failed_fetch_is_not_retried()
    {
        var picture = Picture(seed: 2);

        await using var liar = new RudePeer(picture.Tag);
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        await bob.StartAsync();

        // Три представления подряд об одном и том же отпечатке — как три объявления.
        for (var i = 0; i < 3; i++)
        {
            await liar.GreetAsync(Loopback(bob.ListenPort));
            await bob.FlushAsync();
        }

        await RudePeer.Settle();

        Assert.Equal(1, liar.AvatarRequests);
        Assert.Equal(0, await bob.CachedAvatarCountAsync());
    }

    /// <summary>
    /// Картинка не должна пережить собеседника. Тест небыстрый намеренно: уход по
    /// таймауту — основной механизм (процесс могли убить), и ждать приходится
    /// <see cref="ChatEngine.PeerTimeout"/> по-настоящему.
    /// </summary>
    [Fact]
    public async Task Cache_is_dropped_when_the_peer_goes()
    {
        var expiry = ChatEngine.PeerTimeout + TimeSpan.FromSeconds(15);
        var picture = Picture(seed: 3);

        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };
        await bob.StartAsync();

        var alice = new ChatEngine { LocalDisplayName = "Алиса", LocalAvatar = picture };
        var gone = PeerGone(bob, alice.LocalId);

        try
        {
            await alice.StartAsync();

            var bobSeesTheFace = PeerWithAvatar(bob, alice.LocalId);
            await alice.SendTextToAsync(Loopback(bob.ListenPort), "привет");
            await bobSeesTheFace.WaitAsync(Patience);

            Assert.Equal(1, await bob.CachedAvatarCountAsync());
        }
        finally
        {
            await alice.DisposeAsync();
        }

        await gone.WaitAsync(expiry);
        await WaitUntilAsync(async () => await bob.CachedAvatarCountAsync() == 0, expiry);
    }

    // ─── Разбор ответа ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_good_answer_is_accepted()
    {
        var picture = Picture(seed: 4);
        using var stream = await Answer(new AvatarFrame { Tag = picture.Tag, Bytes = picture.Buffer });

        var received = await OutboundExchange.ReadAvatarAsync(stream, picture.Tag, CancellationToken.None);

        Assert.Equal(picture.Tag, received.Tag);
    }

    /// <summary>Правильный отпечаток, но байты не те: подмена картинки.</summary>
    [Fact]
    public async Task Bytes_that_do_not_match_the_tag_are_refused()
    {
        var wanted = Picture(seed: 5);
        var other = Picture(seed: 6);

        using var stream = await Answer(new AvatarFrame { Tag = wanted.Tag, Bytes = other.Buffer });

        var error = await Assert.ThrowsAsync<ProtocolException>(
            async () => await OutboundExchange.ReadAvatarAsync(stream, wanted.Tag, CancellationToken.None));

        Assert.Contains("отпечатку", error.Message);
    }

    /// <summary>Годная картинка, но отвечают не на то, о чём спрашивали.</summary>
    [Fact]
    public async Task An_answer_about_another_tag_is_refused()
    {
        var wanted = Picture(seed: 7);
        var other = Picture(seed: 8);

        using var stream = await Answer(new AvatarFrame { Tag = other.Tag, Bytes = other.Buffer });

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await OutboundExchange.ReadAvatarAsync(stream, wanted.Tag, CancellationToken.None));
    }

    [Fact]
    public async Task Junk_instead_of_a_picture_is_refused()
    {
        var wanted = Picture(seed: 9);
        var junk = new byte[4096];
        Random.Shared.NextBytes(junk);
        junk[0] = 0x4D;   // TIFF: формат, который мы не пускаем
        junk[1] = 0x4D;

        using var stream = await Answer(new AvatarFrame { Tag = wanted.Tag, Bytes = junk });

        var error = await Assert.ThrowsAsync<ProtocolException>(
            async () => await OutboundExchange.ReadAvatarAsync(stream, wanted.Tag, CancellationToken.None));

        Assert.Contains("картинк", error.Message);
    }

    [Fact]
    public async Task A_refusal_is_reported_as_a_refusal()
    {
        using var stream = await Answer(new ErrorFrame { Reason = "такой картинки у меня нет" });

        var error = await Assert.ThrowsAsync<ProtocolException>(
            async () => await OutboundExchange.ReadAvatarAsync(stream, Picture(seed: 10).Tag, CancellationToken.None));

        Assert.Contains("отказал", error.Message);
    }

    [Fact]
    public async Task Silence_instead_of_an_answer_is_refused()
    {
        using var empty = new MemoryStream();

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await OutboundExchange.ReadAvatarAsync(empty, Picture(seed: 11).Tag, CancellationToken.None));
    }

    // ─── Оснастка ────────────────────────────────────────────────────────────

    private static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);

    /// <summary>Картинка с предсказуемым содержимым: разный seed — разный отпечаток.</summary>
    private static AvatarImage Picture(int seed)
    {
        var bytes = new byte[2048];
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        png.CopyTo(bytes);

        for (var i = png.Length; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 31 + seed * 97) % 251);

        Assert.True(Avatars.TryCreate(bytes, out var image));
        return image;
    }

    private static async Task<MemoryStream> Answer(WireFrame frame)
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;
        return stream;
    }

    private static Task<PeerSnapshot> PeerWithAvatar(ChatEngine engine, PeerId expected)
    {
        var source = new TaskCompletionSource<PeerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Check(PeerSnapshot peer)
        {
            if (peer.Id == expected && peer.Avatar is not null)
                source.TrySetResult(peer);
        }

        engine.PeerAppeared += Check;
        engine.PeerUpdated += Check;
        return source.Task;
    }

    private static Task PeerGone(ChatEngine engine, PeerId expected)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        engine.PeerGone += id =>
        {
            if (id == expected)
                source.TrySetResult();
        };

        return source.Task;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(50);
        }

        Assert.Fail("условие так и не выполнилось");
    }

    /// <summary>
    /// Пир, который представляется как полагается, а на запрос картинки обрывает
    /// соединение. Считает, сколько раз его спросили.
    /// </summary>
    private sealed class RudePeer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;
        private int _requests;

        public RudePeer(string tag)
        {
            Tag = tag;

            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(8);

            Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
            _loop = Task.Run(RunAsync);
        }

        public PeerId PeerId { get; } = PeerId.New();

        public string Tag { get; }

        public int Port { get; }

        public int AvatarRequests => Volatile.Read(ref _requests);

        /// <summary>
        /// Представляется движку — ровно то, что делает настоящий пир, объявляя себя.
        /// После этого движок знает и маршрут, и отпечаток картинки.
        /// </summary>
        public async Task GreetAsync(IPEndPoint target)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(target, _lifetime.Token);

            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, Identity(), _lifetime.Token);
            await FrameCodec.ReadAsync(stream, _lifetime.Token);
        }

        /// <summary>Даёт опоздавшим соединениям доехать, чтобы счётчик не соврал в нашу пользу.</summary>
        public static Task Settle() => Task.Delay(500);

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Dispose();

            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // Остановка — не ошибка.
            }

            _lifetime.Dispose();
        }

        private IdentifyFrame Identity() =>
            new() { PeerId = PeerId, DisplayName = "Врун", ListenPort = Port, AvatarTag = Tag };

        private async Task RunAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptAsync(_lifetime.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(socket));
            }
        }

        private async Task ServeAsync(Socket socket)
        {
            try
            {
                using (socket)
                await using (var stream = new NetworkStream(socket, ownsSocket: false))
                {
                    if (await FrameCodec.ReadAsync(stream, _lifetime.Token) is not IdentifyFrame)
                        return;

                    await FrameCodec.WriteAsync(stream, Identity(), _lifetime.Token);

                    if (await FrameCodec.ReadAsync(stream, _lifetime.Token) is AvatarRequestFrame)
                        Interlocked.Increment(ref _requests);

                    // И молча закрываемся: ровно та неудача, которую нельзя повторять.
                }
            }
            catch (Exception)
            {
                // Обрыв — это и есть роль.
            }
        }
    }
}
