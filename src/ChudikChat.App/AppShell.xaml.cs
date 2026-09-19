namespace ChudikChat.App;

public partial class AppShell : Shell
{
    /// <summary>
    /// Страница подставляется здесь, а не через <c>ContentTemplate</c> в XAML:
    /// шаблон создаёт её сам и мимо контейнера, а странице нужна модель представления.
    /// </summary>
    public AppShell(MainPage page)
    {
        InitializeComponent();

        Items.Add(new ShellContent
        {
            Title = "Чудик",
            Route = "MainPage",
            Content = page,
        });
    }
}
