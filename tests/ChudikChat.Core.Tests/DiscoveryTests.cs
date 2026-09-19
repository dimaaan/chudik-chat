using ChudikChat.Core;
using ChudikChat.Core.Discovery;
using ChudikChat.Core.Model;

namespace ChudikChat.Core.Tests;

/// <summary>
/// Проверки, которым нужна настоящая сетевая подсистема машины.
/// Если тут что-то падает — смотреть на брандмауэр и на список интерфейсов,
/// а не на протокол.
/// </summary>
public class DiscoveryTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public void Scanner_finds_at_least_one_usable_adapter()
    {
        var adapters = InterfaceScanner.ScanPreferred();

        Assert.NotEmpty(adapters);
        Assert.All(adapters, adapter =>
        {
            Assert.NotEqual(0, adapter.InterfaceIndex);
            Assert.NotNull(adapter.DirectedBroadcast);
        });
    }

    [Fact]
    public void Directed_broadcast_is_computed_from_address_and_mask()
    {
        var adapter = new NetworkAdapter
        {
            InterfaceIndex = 1,
            Address = System.Net.IPAddress.Parse("192.168.1.37"),
            Mask = System.Net.IPAddress.Parse("255.255.255.0"),
            Name = "тест",
            Description = "тест",
            IsLikelyVirtual = false,
        };

        Assert.Equal(System.Net.IPAddress.Parse("192.168.1.255"), adapter.DirectedBroadcast);
    }

    [Fact]
    public async Task Two_engines_on_one_machine_find_each_other()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        var aliceSeesBob = WaitFor(alice, bob.LocalId);
        var bobSeesAlice = WaitFor(bob, alice.LocalId);

        await alice.StartAsync();
        await bob.StartAsync();

        var bobAsSeen = await aliceSeesBob.WaitAsync(Patience);
        var aliceAsSeen = await bobSeesAlice.WaitAsync(Patience);

        Assert.Equal("Боб", bobAsSeen.DisplayName);
        Assert.Equal(bob.ListenPort, bobAsSeen.PrimaryEndpoint!.Port);

        Assert.Equal("Алиса", aliceAsSeen.DisplayName);
        Assert.Equal(alice.ListenPort, aliceAsSeen.PrimaryEndpoint!.Port);
    }

    [Fact]
    public async Task Discovered_peer_can_be_written_to_without_knowing_its_address()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        await using var bob = new ChatEngine { LocalDisplayName = "Боб" };

        var aliceSeesBob = WaitFor(alice, bob.LocalId);
        var bobHeard = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += message => bobHeard.TrySetResult(message);

        await alice.StartAsync();
        await bob.StartAsync();

        await aliceSeesBob.WaitAsync(Patience);

        var sent = await alice.SendTextAsync(bob.LocalId, "нашёл тебя");

        Assert.Equal(MessageState.Delivered, sent.State);
        Assert.Equal("нашёл тебя", (await bobHeard.Task.WaitAsync(Patience)).Text);
    }

    [Fact]
    public async Task Peer_disappears_after_a_farewell()
    {
        await using var alice = new ChatEngine { LocalDisplayName = "Алиса" };
        var bob = new ChatEngine { LocalDisplayName = "Боб" };

        var aliceSeesBob = WaitFor(alice, bob.LocalId);
        var bobIsGone = new TaskCompletionSource<PeerId>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.PeerGone += id =>
        {
            if (id == bob.LocalId)
                bobIsGone.TrySetResult(id);
        };

        await alice.StartAsync();
        await bob.StartAsync();
        await aliceSeesBob.WaitAsync(Patience);

        // Штатный выход рассылает прощание — ждать истечения таймаута не нужно.
        await bob.DisposeAsync();

        Assert.Equal(bob.LocalId, await bobIsGone.Task.WaitAsync(Patience));
    }

    private static Task<PeerSnapshot> WaitFor(ChatEngine engine, PeerId expected)
    {
        var source = new TaskCompletionSource<PeerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Check(PeerSnapshot peer)
        {
            if (peer.Id == expected)
                source.TrySetResult(peer);
        }

        engine.PeerAppeared += Check;
        engine.PeerUpdated += Check;
        return source.Task;
    }
}
