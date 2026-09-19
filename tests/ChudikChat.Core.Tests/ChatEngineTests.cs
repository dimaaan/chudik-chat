using System.Net;
using ChudikChat.Core;
using ChudikChat.Core.Model;

namespace ChudikChat.Core.Tests;

public class ChatEngineTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Text_reaches_the_other_engine_and_both_learn_each_other()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        var bobHeard = NextMessage(bob);
        var bobSawAlice = NextPeer(bob);

        await alice.StartAsync();
        await bob.StartAsync();

        // Алиса знает только адрес — ровно путь «добавить пира по IP».
        var sent = await alice.SendTextToAsync(Loopback(bob.ListenPort), "привет, Боб");

        Assert.Equal(MessageState.Delivered, sent.State);
        Assert.Equal(bob.LocalId, sent.Peer);

        var received = await bobHeard.WaitAsync(Patience);
        Assert.Equal("привет, Боб", received.Text);
        Assert.Equal(alice.LocalId, received.Peer);
        Assert.Equal(MessageDirection.Incoming, received.Direction);

        var aliceAsSeenByBob = await bobSawAlice.WaitAsync(Patience);
        Assert.Equal("Алиса", aliceAsSeenByBob.DisplayName);
        Assert.Equal(alice.ListenPort, aliceAsSeenByBob.PrimaryEndpoint!.Port);

        // Обратное направление уже идёт по выученному маршруту, без указания адреса.
        var aliceHeard = NextMessage(alice);
        var reply = await bob.SendTextAsync(alice.LocalId, "и тебе привет");

        Assert.Equal(MessageState.Delivered, reply.State);
        Assert.Equal("и тебе привет", (await aliceHeard.WaitAsync(Patience)).Text);
    }

    [Fact]
    public async Task Message_order_is_preserved_per_peer()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        await alice.StartAsync();
        await bob.StartAsync();

        var heard = new List<string>();
        var allArrived = new TaskCompletionSource();
        bob.MessageReceived += message =>
        {
            lock (heard)
            {
                heard.Add(message.Text);
                if (heard.Count == 10)
                    allArrived.TrySetResult();
            }
        };

        await alice.SendTextToAsync(Loopback(bob.ListenPort), "0");

        var rest = Enumerable.Range(1, 9)
            .Select(i => alice.SendTextAsync(bob.LocalId, i.ToString()))
            .ToArray();

        await Task.WhenAll(rest);
        await allArrived.Task.WaitAsync(Patience);

        lock (heard)
        {
            Assert.Equal(Enumerable.Range(0, 10).Select(i => i.ToString()), heard);
        }
    }

    [Fact]
    public async Task Sending_to_an_unknown_peer_fails_without_throwing()
    {
        await using var alice = new ChatEngine();
        await alice.StartAsync();

        var sent = await alice.SendTextAsync(PeerId.New(), "в пустоту");

        Assert.Equal(MessageState.Failed, sent.State);
    }

    [Fact]
    public async Task Sending_to_a_dead_address_fails_without_throwing()
    {
        await using var alice = new ChatEngine();
        await alice.StartAsync();

        // Порт 9 — discard: соединение либо отвергается, либо молчит до таймаута.
        var sent = await alice.SendTextToAsync(Loopback(9), "в пустоту");

        Assert.Equal(MessageState.Failed, sent.State);
    }

    [Fact]
    public async Task Engine_refuses_a_connection_to_itself()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };

        // Подписка до StartAsync: движок рассылает HELLO прямо из него, и всё,
        // что на это ответит, должно попасть в обработчики, а не проскочить мимо
        // и оставить тест зелёным по случайности.
        var appeared = false;
        alice.PeerAppeared += _ => appeared = true;

        ChatMessage? received = null;
        alice.MessageReceived += message => received = message;

        await alice.StartAsync();

        var sent = await alice.SendTextToAsync(Loopback(alice.ListenPort), "сам себе");

        Assert.False(appeared, "собственный идентификатор не должен попадать в список пиров");
        Assert.Null(received);
        Assert.Equal(MessageState.Failed, sent.State);
    }

    private static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);

    private static Task<ChatMessage> NextMessage(ChatEngine engine)
    {
        var source = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.MessageReceived += message => source.TrySetResult(message);
        return source.Task;
    }

    private static Task<PeerSnapshot> NextPeer(ChatEngine engine)
    {
        var source = new TaskCompletionSource<PeerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PeerAppeared += peer => source.TrySetResult(peer);
        return source.Task;
    }
}
