using System.Globalization;
using ChudikChat.Core.Text;

namespace ChudikChat.App.Converters;

/// <summary>
/// Превращает текст сообщения в размеченную строку, где ссылки подчёркнуты
/// и открываются в браузере по нажатию.
/// </summary>
/// <remarks>
/// Размечается один Label, а не собирается раскладка из отдельных подписей:
/// иначе текст перестал бы переноситься по словам на границах кусков,
/// и длинная ссылка вылезала бы за пузырёк.
///
/// Цвет ссылки намеренно не меняется, только подчёркивание. Исходящее
/// сообщение — это белый текст на синем фоне, входящее — тёмный на светлом,
/// и обе темы вдобавок переключаются на ходу. Подчёркивание читается на любом
/// из этих фонов, а любой выбранный цвет на каком-нибудь да потеряется.
/// </remarks>
public sealed class MessageTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var formatted = new FormattedString();

        foreach (var segment in LinkScanner.Split(value as string))
        {
            var span = new Span { Text = segment.Text };

            if (segment.Url is { } url)
            {
                span.TextDecorations = TextDecorations.Underline;

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Open(url);
                span.GestureRecognizers.Add(tap);
            }

            formatted.Spans.Add(span);
        }

        return formatted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("разметка сообщения обратно в текст не разбирается");

    /// <summary>
    /// Открывает адрес тем, чем в системе принято открывать http и https, —
    /// то есть браузером по умолчанию. Схему уже проверил <see cref="LinkScanner"/>:
    /// сюда не доберётся ни file:, ни ms-settings:, ни чья-нибудь своя.
    /// </summary>
    private static async void Open(string url)
    {
        try
        {
            await Launcher.Default.OpenAsync(new Uri(url));
        }
        catch (Exception)
        {
            // Открывать нечем или система отказала. Ронять из-за этого переписку
            // не за что: адрес остаётся на экране, его можно скопировать руками.
        }
    }
}
