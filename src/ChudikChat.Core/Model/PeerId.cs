using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChudikChat.Core.Model;

/// <summary>
/// Личность пира: GUID, который живёт ровно столько, сколько процесс.
/// Не IP, не имя, не MAC — поэтому смена адреса не создаёт нового пира,
/// а машина с несколькими адаптерами не двоится в списке.
/// </summary>
[JsonConverter(typeof(PeerIdJsonConverter))]
public readonly record struct PeerId(Guid Value) : IComparable<PeerId>
{
    public static PeerId New() => new(Guid.NewGuid());

    /// <summary>Каноническая форма. Именно она сравнивается, а не <see cref="Guid"/>.</summary>
    public string Canonical => Value.ToString("N");

    /// <summary>Короткий хвост для UI, когда два пира называются одинаково.</summary>
    public string Short => Canonical[..6];

    /// <remarks>
    /// Ordinal по строке, а не <c>Guid.CompareTo</c>: последний сравнивает внутренние поля
    /// с разным порядком байт, и две стороны могут прийти к разным выводам.
    /// </remarks>
    public int CompareTo(PeerId other) => string.CompareOrdinal(Canonical, other.Canonical);

    public override string ToString() => Canonical;

    public static bool TryParse(string? text, out PeerId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new PeerId(guid);
            return true;
        }

        id = default;
        return false;
    }
}

internal sealed class PeerIdJsonConverter : JsonConverter<PeerId>
{
    public override PeerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(Guid.ParseExact(reader.GetString() ?? string.Empty, "N"));

    public override void Write(Utf8JsonWriter writer, PeerId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Canonical);
}
