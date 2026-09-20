using System.Buffers.Binary;
using System.Text;
using ChudikChat.Core;
using ChudikChat.Core.Model;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Tests;

public class FrameCodecTests
{
    [Fact]
    public async Task Identify_survives_round_trip()
    {
        var sent = new IdentifyFrame
        {
            PeerId = PeerId.New(),
            DisplayName = "Ноутбук Даши",
            ListenPort = 51234,
        };

        var received = Assert.IsType<IdentifyFrame>(await RoundTripAsync(sent));

        Assert.Equal(sent.PeerId, received.PeerId);
        Assert.Equal(sent.DisplayName, received.DisplayName);
        Assert.Equal(sent.ListenPort, received.ListenPort);
    }

    [Fact]
    public async Task Text_survives_round_trip()
    {
        var sent = new TextFrame
        {
            MessageId = Guid.NewGuid(),
            SentAt = DateTimeOffset.Now,
            Text = "Привет 👋 — как дела?",
        };

        var received = Assert.IsType<TextFrame>(await RoundTripAsync(sent));

        Assert.Equal(sent.MessageId, received.MessageId);
        Assert.Equal(sent.Text, received.Text);
        Assert.Equal(sent.SentAt.ToUnixTimeMilliseconds(), received.SentAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Ack_survives_round_trip()
    {
        var sent = new AckFrame { MessageId = Guid.NewGuid() };
        var received = Assert.IsType<AckFrame>(await RoundTripAsync(sent));

        Assert.Equal(sent.MessageId, received.MessageId);
    }

    [Fact]
    public async Task Several_frames_read_back_in_order()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new AckFrame { MessageId = Guid.Empty }, CancellationToken.None);
        await FrameCodec.WriteAsync(stream, new ErrorFrame { Reason = "нет" }, CancellationToken.None);
        stream.Position = 0;

        Assert.IsType<AckFrame>(await FrameCodec.ReadAsync(stream, CancellationToken.None));
        Assert.IsType<ErrorFrame>(await FrameCodec.ReadAsync(stream, CancellationToken.None));
        Assert.Null(await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Clean_close_on_frame_boundary_is_not_an_error()
    {
        using var empty = new MemoryStream();

        Assert.Null(await FrameCodec.ReadAsync(empty, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(ProtocolConstants.MaxFrameBytes + 1)]
    public async Task Impossible_length_is_refused_without_allocating(int declaredLength)
    {
        using var stream = new MemoryStream(Header(declaredLength));

        var error = await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));

        Assert.Contains("длина кадра", error.Message);
    }

    [Fact]
    public async Task Truncated_header_is_refused()
    {
        using var stream = new MemoryStream([0x00, 0x00]);

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_payload_is_refused()
    {
        var payload = Encoding.UTF8.GetBytes("""{"t":"ack","messageId":"...""");
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length + 100);
        payload.CopyTo(buffer, 4);

        using var stream = new MemoryStream(buffer);

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Garbage_payload_is_refused()
    {
        var payload = "не json"u8.ToArray();
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
        payload.CopyTo(buffer, 4);

        using var stream = new MemoryStream(buffer);

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Frame_over_the_cap_is_not_written()
    {
        var frame = new TextFrame
        {
            MessageId = Guid.NewGuid(),
            SentAt = DateTimeOffset.Now,
            Text = new string('я', ProtocolConstants.MaxFrameBytes),
        };

        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.WriteAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task Avatar_survives_round_trip()
    {
        var sent = new AvatarFrame
        {
            Tag = new string('a', Avatars.TagLength),
            Bytes = Png(1024),
        };

        var received = Assert.IsType<AvatarFrame>(await RoundTripAsync(sent));

        Assert.Equal(sent.Tag, received.Tag);
        Assert.Equal(sent.Bytes, received.Bytes);
    }

    /// <summary>
    /// Потолок картинки и потолок кадра живут в разных файлах. Этот тест — единственное,
    /// что не даёт им разъехаться: base64 раздувает байты на треть.
    /// </summary>
    [Fact]
    public void Avatar_at_the_cap_fits_a_frame()
    {
        Assert.True(Avatars.MaxBytes * 4 / 3 + 4096 < ProtocolConstants.MaxFrameBytes);
    }

    [Fact]
    public async Task Avatar_over_the_frame_cap_is_not_written()
    {
        var frame = new AvatarFrame
        {
            Tag = new string('a', Avatars.TagLength),
            Bytes = new byte[ProtocolConstants.MaxFrameBytes],
        };

        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.WriteAsync(stream, frame, CancellationToken.None));
    }

    /// <summary>
    /// Представление от старой сборки: поля отпечатка в нём нет вовсе.
    /// </summary>
    [Fact]
    public async Task Identify_without_an_avatar_tag_parses()
    {
        var received = Assert.IsType<IdentifyFrame>(await ReadHandwrittenAsync(
            $$"""{"t":"id","version":1,"peerId":"{{Guid.NewGuid():N}}","displayName":"Даша","listenPort":51234}"""));

        Assert.Null(received.AvatarTag);
    }

    /// <summary>
    /// Кадр от БОЛЕЕ НОВОЙ сборки с полем, которого мы не знаем. Совместимость вперёд
    /// держится на умолчании System.Text.Json, и проверить это надо, а не предположить.
    /// </summary>
    [Fact]
    public async Task Identify_with_an_unknown_field_parses()
    {
        var received = Assert.IsType<IdentifyFrame>(await ReadHandwrittenAsync(
            $$$"""
            {"t":"id","version":1,"peerId":"{{{Guid.NewGuid():N}}}","displayName":"Даша",
             "listenPort":51234,"чегоТоНовое":{"вложенное":[1,2,3]}}
            """));

        Assert.Equal(51234, received.ListenPort);
    }

    /// <summary>
    /// Кадр незнакомого вида — так выглядит запрос картинки, пришедший в старую сборку.
    /// Он обязан разворачиваться в ProtocolException, а не в что попало: иначе причина
    /// теряется по дороге, а соединение всё равно рвётся.
    /// </summary>
    [Fact]
    public async Task Unknown_frame_kind_is_reported_as_a_protocol_error()
    {
        await Assert.ThrowsAsync<ProtocolException>(
            async () => await ReadHandwrittenAsync("""{"t":"нетакогокадра","version":1}"""));
    }

    private static async Task<WireFrame?> ReadHandwrittenAsync(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
        payload.CopyTo(buffer, 4);

        using var stream = new MemoryStream(buffer);
        return await FrameCodec.ReadAsync(stream, CancellationToken.None);
    }

    private static byte[] Png(int length)
    {
        var bytes = new byte[length];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    private static byte[] Header(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        return header;
    }

    private static async Task<WireFrame> RoundTripAsync(WireFrame frame)
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;

        var received = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        return Assert.IsAssignableFrom<WireFrame>(received);
    }
}
