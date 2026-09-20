using System.Collections.Specialized;
using System.ComponentModel;
using ChudikChat.App.Services;
using ChudikChat.App.ViewModels;

namespace ChudikChat.App;

public partial class MainPage : ContentPage
{
    /// <summary>Ширина, ниже которой список и переписка перестают помещаться рядом.</summary>
    private const double WideThreshold = 700;

    private readonly MainViewModel _model;
    private INotifyCollectionChanged? _watchedMessages;
    private bool _isWide = true;
    private bool _started;

    public MainPage(MainViewModel model)
    {
        InitializeComponent();

        _model = model;
        BindingContext = model;
        model.PropertyChanged += OnModelPropertyChanged;

        // Клавиатура ужимает список, а список при уменьшении держится за верхний
        // край — последние сообщения уходят вниз за границу видимой части.
        MessagesView.SizeChanged += (_, _) => ScrollToLastMessage();

        // Нативный обработчик доступен только после того, как создан платформенный вид.
        Loaded += (_, _) => FileDrop.Attach(this, OnFilesDropped);
    }

    private void OnFilesDropped(IReadOnlyList<string> paths)
    {
        if (_model.SelectedPeer is not { } peer)
        {
            _model.Status = "сначала выберите, кому отправить";
            return;
        }

        // Передачи идут по отдельным соединениям, поэтому запускаем их разом.
        foreach (var path in paths)
            _ = _model.SendPathAsync(peer, path, Directory.Exists(path));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_started)
            return;

        _started = true;

        try
        {
            await _model.InitializeAsync();
        }
        catch (Exception e)
        {
            await DisplayAlertAsync("Не удалось запуститься", e.Message, "Ладно");
        }
    }

    /// <summary>
    /// Раскладка решается здесь, а не в XAML: правило простое и зависит сразу
    /// от ширины окна и от того, выбран ли собеседник.
    /// </summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        var wide = width >= WideThreshold;
        if (wide == _isWide && width > 0)
        {
            ApplyLayout();
            return;
        }

        _isWide = wide;
        ApplyLayout();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedPeer))
            return;

        ApplyLayout();
        WatchMessages();
        ScrollToLastMessage();
    }

    private void ApplyLayout()
    {
        var hasPeer = _model.SelectedPeer is not null;

        if (_isWide)
        {
            PeersColumn.Width = new GridLength(300);
            ChatColumn.Width = GridLength.Star;
            PeersPane.IsVisible = true;
            ChatPane.IsVisible = true;
        }
        else if (hasPeer)
        {
            PeersColumn.Width = new GridLength(0);
            ChatColumn.Width = GridLength.Star;
            PeersPane.IsVisible = false;
            ChatPane.IsVisible = true;
        }
        else
        {
            PeersColumn.Width = GridLength.Star;
            ChatColumn.Width = new GridLength(0);
            PeersPane.IsVisible = true;
            ChatPane.IsVisible = false;
        }

        BackButton.IsVisible = !_isWide && hasPeer;
    }

    private void WatchMessages()
    {
        if (_watchedMessages is not null)
            _watchedMessages.CollectionChanged -= OnMessagesChanged;

        _watchedMessages = _model.SelectedPeer?.Messages;

        if (_watchedMessages is not null)
            _watchedMessages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToLastMessage();
    }

    private void ScrollToLastMessage()
    {
        var messages = _model.SelectedPeer?.Messages;
        if (messages is null || messages.Count == 0)
            return;

        MessagesView.ScrollTo(messages.Count - 1, position: ScrollToPosition.End, animate: false);
    }
}
