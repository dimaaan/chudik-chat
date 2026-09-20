namespace ChudikChat.App;

/// <summary>
/// Что это за программа, какой версии и кто её сделал.
/// </summary>
/// <remarks>
/// Без модели представления, в отличие от главной страницы. Показывать здесь нечего:
/// две строки берутся у системы один раз при создании, остальное — постоянные,
/// наблюдать не за чем. Модель ради этого завелась бы пустой, а её ещё пришлось бы
/// регистрировать в контейнере.
/// </remarks>
public partial class AboutPage : ContentPage
{
    private const string ReleasesUrl = "https://github.com/dimaaan/chudik-chat/releases";

    public AboutPage()
    {
        InitializeComponent();

        // Версию спрашиваем у системы, а не держим своей константой: она и так уже
        // задана в csproj как ApplicationDisplayVersion, и второе место разошлось бы
        // с первым в первый же выпуск. Вдобавок сборочная автоматика дописывает
        // к ней номер прогона — на своей машине это «1.1», в выложенной сборке
        // «1.1.42», — и константа показывала бы не то, что человек скачал.
        VersionLabel.Text = $"Версия {AppInfo.Current.VersionString}";
        BuildLabel.Text = $"Сборка {AppInfo.Current.BuildString}";
    }

    private async void OnReleasesClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync(new Uri(ReleasesUrl));
        }
        catch (Exception)
        {
            // Открывать нечем или система отказала. Ругаться здесь некуда — строки
            // состояния на этом экране нет, — да и не нужно: адрес остался текстом
            // кнопки, и его видно. Тот же размен, что в MessageTextConverter.
        }
    }
}
