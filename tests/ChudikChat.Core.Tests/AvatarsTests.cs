using ChudikChat.Core.Model;

namespace ChudikChat.Core.Tests;

public class AvatarsTests
{
    [Fact]
    public void Tag_is_addressed_by_content()
    {
        var first = Create(Png(512));
        var second = Create(Png(512));

        Assert.Equal(first.Tag, second.Tag);
    }

    [Fact]
    public void One_flipped_byte_changes_the_tag()
    {
        var bytes = Png(512);
        var before = Create(bytes).Tag;

        bytes[400] ^= 0xFF;

        Assert.NotEqual(before, Create(bytes).Tag);
    }

    [Fact]
    public void Tag_has_the_declared_shape()
    {
        var tag = Create(Png(512)).Tag;

        Assert.Equal(Avatars.TagLength, tag.Length);
        Assert.True(Avatars.IsValidTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]   // верхний регистр
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]   // не шестнадцатеричное
    [InlineData("../../etc/passwd0000000000000000")]   // путь под видом ключа
    [InlineData("0123456789abcdef0123456789abcdef0")]  // длиннее на символ
    public void Bad_tags_are_refused(string? tag)
    {
        Assert.False(Avatars.IsValidTag(tag));
        Assert.Null(Avatars.SanitizeTag(tag));
    }

    [Fact]
    public void Png_and_jpeg_are_accepted()
    {
        Assert.True(Avatars.TryCreate(Png(512), out _));
        Assert.True(Avatars.TryCreate(Jpeg(512), out _));
    }

    [Theory]
    [InlineData(0x42, 0x4D)]              // BMP
    [InlineData(0x47, 0x49)]              // GIF
    [InlineData((byte)'<', (byte)'s')]    // SVG, то есть разметка, а не картинка
    [InlineData(0x4D, 0x4D)]              // TIFF: ровно то, что отдаёт macOS без перекодирования
    [InlineData(0x00, 0x00)]
    public void Other_formats_are_refused(byte first, byte second)
    {
        var bytes = new byte[512];
        bytes[0] = first;
        bytes[1] = second;

        Assert.False(Avatars.TryCreate(bytes, out var image));
        Assert.Null(image);
    }

    [Fact]
    public void Sizes_outside_the_bounds_are_refused()
    {
        Assert.False(Avatars.TryCreate(Png(Avatars.MaxBytes + 1), out _));
        Assert.False(Avatars.TryCreate(Png(Avatars.MinBytes - 1), out _));
        Assert.False(Avatars.TryCreate([], out _));

        Assert.True(Avatars.TryCreate(Png(Avatars.MaxBytes), out _));
        Assert.True(Avatars.TryCreate(Png(Avatars.MinBytes), out _));
    }

    /// <summary>
    /// Копия обязательна: голова могла оставить массив себе и изменить его позже,
    /// а отпечаток к тому моменту уже уехал бы в сеть.
    /// </summary>
    [Fact]
    public void Bytes_are_copied_away_from_the_caller()
    {
        var bytes = Png(512);
        var image = Create(bytes);
        var tag = image.Tag;

        bytes[100] ^= 0xFF;

        Assert.Equal(tag, image.Tag);
        Assert.NotEqual(bytes[100], image.Bytes.Span[100]);
    }

    [Fact]
    public void Each_read_gets_its_own_stream()
    {
        var image = Create(Png(512));

        using var first = image.OpenRead();
        using var second = image.OpenRead();

        first.ReadByte();

        Assert.NotSame(first, second);
        Assert.Equal(0, second.Position);
    }

    [Fact]
    public void Equality_is_by_tag()
    {
        var bytes = Png(512);

        Assert.Equal(Create(bytes), Create(bytes));
        Assert.NotEqual(Create(Png(512)), Create(Jpeg(512)));
    }

    private static AvatarImage Create(byte[] bytes)
    {
        Assert.True(Avatars.TryCreate(bytes, out var image));
        return image;
    }

    private static byte[] Png(int length) => WithSignature(length, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static byte[] Jpeg(int length) => WithSignature(length, [0xFF, 0xD8, 0xFF]);

    private static byte[] WithSignature(int length, ReadOnlySpan<byte> signature)
    {
        var bytes = new byte[length];
        signature[..Math.Min(signature.Length, length)].CopyTo(bytes);

        // Немного содержимого, чтобы картинки одного размера различались.
        for (var i = signature.Length; i < length; i++)
            bytes[i] = (byte)(i * 31 % 251);

        return bytes;
    }
}
