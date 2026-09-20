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

        // «О программе» — маршрут, а не второй ShellContent: это не раздел приложения,
        // а страница, с которой возвращаются. Возврат даёт стек навигации, и вместе
        // с ним даром достаются аппаратная «Назад» на Android и жест возврата.
        // Страницу по маршруту создаёт контейнер, как и всё остальное здесь.
        Routing.RegisterRoute("about", typeof(AboutPage));
    }
}
