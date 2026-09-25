using System.Collections.ObjectModel;
using System.Net;
using ChudikChat.App.Services;
using ChudikChat.Core;
using ChudikChat.Core.Model;
using ChudikChat.Core.Transfer;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChudikChat.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ChatSession _session;
    private readonly Dictionary<PeerId, PeerViewModel> _byId = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial PeerViewModel? SelectedPeer { get; set; }

    [ObservableProperty]
    public partial string Draft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MyName { get; set; } = string.Empty;

    /// <summary>Своя картинка учётной записи. null — в шапке её просто нет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMyAvatar))]
    public partial ImageSource? MyAvatar { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "запускаюсь…";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public MainViewModel(ChatSession session)
    {
        _session = session;

        Engine.PeerAppeared += peer => OnUi(() => ApplyPeer(peer, isNew: true));
        Engine.PeerUpdated += peer => OnUi(() => ApplyPeer(peer, isNew: false));
        Engine.PeerGone += id => OnUi(() => ApplyPeerGone(id));
        Engine.MessageReceived += message => OnUi(() => ApplyIncoming(message));
        Engine.TransferChanged += progress => OnUi(() => ApplyTransfer(progress));
        Engine.Diagnostic += text => OnUi(() => Status = text);

        Engine.TransferDecider = AskAboutTransferAsync;
    }

    public ChatEngine Engine => _session.Engine;

    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    public bool HasSelection => SelectedPeer is not null;

    public bool HasMyAvatar => MyAvatar is not null;

    public bool CanSendFolders => PlatformEnvironment.CanSendFolders;

    public async Task InitializeAsync()
    {
        await _session.StartAsync();

        MyName = Engine.LocalDisplayName;
        Status = $"слушаю порт {Engine.ListenPort}";

        // Намеренно без await: картинка не должна задерживать появление окна.
        // Соседи узнают о ней со следующего объявления, то есть через несколько секунд.
        _ = LoadMyAvatarAsync();
    }

    /// <summary>
    /// Забирает картинку учётной записи и отдаёт её движку.
    /// </summary>
    /// <remarks>
    /// Годность решает ядро: оно же считает отпечаток, который поедет в сеть, и оно же
    /// проверяет чужие картинки — проверка должна быть одна на всех, иначе головы
    /// разойдутся в том, что считать картинкой.
    /// </remarks>
    private async Task LoadMyAvatarAsync()
    {
        try
        {
            var bytes = await _session.LocalAvatarAsync().ConfigureAwait(false);
            if (bytes is null || !Avatars.TryCreate(bytes, out var avatar))
                return;

            Engine.LocalAvatar = avatar;
            OnUi(() => MyAvatar = ImageSource.FromStream(avatar.OpenRead));
        }
        catch (Exception)
        {
            // Аватар — украшение. Ронять из-за него запуск не за что.
        }
    }

    // ─── События движка ──────────────────────────────────────────────────────

    private void ApplyPeer(PeerSnapshot snapshot, bool isNew)
    {
        if (!_byId.ContainsKey(snapshot.Id))
            Reclaim(snapshot);

        var peer = GetOrCreate(snapshot.Id);

        peer.DisplayName = snapshot.DisplayName;
        peer.Address = snapshot.PrimaryEndpoint?.ToString() ?? string.Empty;
        peer.IsOnline = true;
        peer.Platform = snapshot.Platform;
        peer.ApplyAvatar(snapshot.Avatar);

        if (isNew && Peers.Count == 1)
            SelectedPeer ??= peer;
    }

    /// <summary>
    /// Отдаёт вернувшемуся собеседнику его прежнюю погасшую строку.
    /// </summary>
    /// <remarks>
    /// Идентификатор пира — GUID процесса, поэтому собеседник, закрывший Чудика
    /// и открывший снова, приходит незнакомцем. Без этого в списке оказались бы
    /// две строки с одним именем: серая с перепиской и живая пустая — ровно та
    /// путаница, ради устранения которой список и научился различать присутствие.
    ///
    /// Живые строки не склеиваются никогда, и это не осторожность, а условие
    /// работы: два экземпляра на одной машине — два законных пира, на чём держится
    /// весь цикл отладки. Склеиться можно только с тем, кто уже ушёл.
    ///
    /// Безымянные не склеиваются тоже. Имени у них нет, и все они зовутся
    /// одинаково — «Без имени»; по такому совпадению два разных телефона сошлись
    /// бы в одного собеседника вместе с чужой перепиской.
    ///
    /// Совпадение по имени — это доверие имени, а его объявляет о себе сам пир.
    /// Сосед, назвавшийся как ваш собеседник, займёт его строку. В сети без
    /// шифрования он в любом случае появился бы в списке под этим именем;
    /// разница лишь в том, что к строке прилагается прочитанное раньше.
    /// </remarks>
    private void Reclaim(PeerSnapshot snapshot)
    {
        if (snapshot.DisplayName == DeviceNames.Fallback)
            return;

        foreach (var candidate in Peers)
        {
            if (candidate.IsOnline || candidate.Platform != snapshot.Platform)
                continue;

            if (!string.Equals(candidate.DisplayName, snapshot.DisplayName, StringComparison.Ordinal))
                continue;

            _byId.Remove(candidate.Id);
            candidate.Id = snapshot.Id;
            _byId[snapshot.Id] = candidate;
            return;
        }
    }

    /// <summary>
    /// Ушедшего собеседника не выбрасываем, если с ним есть переписка: строка,
    /// исчезающая из-под курсора посреди чтения, раздражает сильнее, чем лишняя строка.
    /// </summary>
    /// <remarks>
    /// Прощание пира, успевшего вернуться, сюда доходит уже безвредным: строку
    /// забрал новый идентификатор, и по старому ключу таблица ничего не находит.
    /// </remarks>
    private void ApplyPeerGone(PeerId id)
    {
        if (!_byId.TryGetValue(id, out var peer))
            return;

        // Признак гасим до всякого ветвления, в том числе у строки, которую сейчас
        // выбросим. Из списка она уходит, но объект живёт: ссылку на него держит
        // открытый диалог выбора файлов и начатое перетаскивание, и решают они
        // по этому самому признаку. Оставь мы его гореть — файл ушёл бы пиру,
        // которого движок уже забыл, а отчёт о передаче завёл бы в списке новую
        // строку, зелёную навсегда: гасить её больше нечему, прощание уже прошло.
        peer.IsOnline = false;

        if (peer.Messages.Count > 0 || peer.Transfers.Count > 0)
            return;

        _byId.Remove(id);
        Peers.Remove(peer);

        if (SelectedPeer == peer)
            SelectedPeer = null;
    }

    private void ApplyIncoming(ChatMessage message)
    {
        var peer = GetOrCreate(message.Peer);
        peer.Add(MessageViewModel.From(message));

        if (SelectedPeer != peer)
            peer.UnreadCount++;

        // Сюда попадают только входящие: свои сообщения добавляются в SendAsync.
        NotificationSound.Play();
    }

    private void ApplyTransfer(TransferProgress progress)
    {
        var peer = GetOrCreate(progress.Peer);

        foreach (var existing in peer.Transfers)
        {
            if (existing.TransferId != progress.TransferId)
                continue;

            existing.Apply(progress);
            return;
        }

        peer.Transfers.Add(TransferViewModel.From(progress));
    }

    private PeerViewModel GetOrCreate(PeerId id)
    {
        if (_byId.TryGetValue(id, out var existing))
            return existing;

        var peer = new PeerViewModel { Id = id };
        _byId[id] = peer;
        Peers.Add(peer);
        return peer;
    }

    /// <summary>
    /// Спрашивает пользователя про входящую передачу. Молча складывать чужие файлы
    /// на диск приложение не должно, поэтому по умолчанию — отказ.
    /// </summary>
    private async Task<TransferDecision> AskAboutTransferAsync(IncomingTransferOffer offer, CancellationToken ct)
    {
        var what = offer.IsDirectory
            ? $"папку «{offer.RootName}» ({offer.FileCount} файл(ов), {Size(offer.TotalBytes)})"
            : $"файл «{offer.RootName}» ({Size(offer.TotalBytes)})";

        var accepted = await ConfirmAsync(
            "Входящая передача",
            $"{offer.PeerName} предлагает {what}.\n\nСохранить в {Engine.DownloadRoot}?",
            "Принять",
            "Отклонить").ConfigureAwait(false);

        if (!accepted)
            return TransferDecision.Decline("получатель отказался");

        var root = await PlatformEnvironment.EnsureWritableAsync(Engine.DownloadRoot).ConfigureAwait(false);
        return TransferDecision.Accept(root);
    }

    // ─── Команды ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SendAsync()
    {
        var peer = SelectedPeer;
        var text = Draft.Trim();

        if (peer is null || !peer.IsOnline || text.Length == 0)
            return;

        Draft = string.Empty;

        var pending = new MessageViewModel
        {
            Id = Guid.NewGuid(),
            Text = text,
            At = DateTimeOffset.Now,
            IsMine = true,
            State = MessageState.Sending,
        };

        peer.Add(pending);

        var result = await Engine.SendTextAsync(peer.Id, text).ConfigureAwait(false);
        OnUi(() => pending.State = result.State);
    }

    [RelayCommand]
    private async Task PickFilesAsync()
    {
        var peer = SelectedPeer;
        if (peer is null || !peer.IsOnline)
            return;

        IEnumerable<FileResult>? picked;
        try
        {
            picked = await FilePicker.Default.PickMultipleAsync();
        }
        catch (Exception e)
        {
            Status = $"выбор файлов не удался: {e.Message}";
            return;
        }

        if (picked is null)
            return;

        foreach (var file in picked)
            _ = SendPathAsync(peer, file.FullPath, isFolder: false);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var peer = SelectedPeer;
        if (peer is null || !peer.IsOnline || !CanSendFolders)
            return;

        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (!result.IsSuccessful || result.Folder is null)
                return;

            _ = SendPathAsync(peer, result.Folder.Path, isFolder: true);
        }
        catch (Exception e)
        {
            Status = $"выбор папки не удался: {e.Message}";
        }
    }

    /// <summary>Отправка файла или папки по готовому пути — сюда же приходит перетаскивание.</summary>
    /// <remarks>
    /// Проверка на присутствие стоит именно здесь, хотя зовущие её команды проверяют
    /// то же самое сами. Сюда приходит перетаскивание из окна, минуя разметку и
    /// команды вовсе, и без этой строки файл, бро́шенный на погасшего собеседника,
    /// уходил бы в никуда молча.
    /// </remarks>
    public async Task SendPathAsync(PeerViewModel peer, string path, bool isFolder)
    {
        if (!peer.IsOnline)
        {
            OnUi(() => Status = $"{peer.DisplayName} не в сети — отправить нечем");
            return;
        }

        try
        {
            if (isFolder || Directory.Exists(path))
                await Engine.SendFolderAsync(peer.Id, path).ConfigureAwait(false);
            else
                await Engine.SendFileAsync(peer.Id, path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            OnUi(() => Status = $"не удалось отправить {Path.GetFileName(path)}: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await Engine.RefreshPeersAsync().ConfigureAwait(false);
            OnUi(() => Status = "спросил, кто в сети");
        }
        finally
        {
            OnUi(() => IsBusy = false);
        }
    }

    /// <summary>Запасной путь, когда в сети зарезан multicast.</summary>
    [RelayCommand]
    private async Task AddByAddressAsync()
    {
        var answer = await PromptAsync(
            "Подключиться по адресу",
            "Адрес и порт устройства, например 192.168.1.42:51000",
            string.Empty);

        if (string.IsNullOrWhiteSpace(answer))
            return;

        if (!IPEndPoint.TryParse(answer.Trim(), out var endpoint))
        {
            Status = $"не разбирается как адрес: {answer}";
            return;
        }

        var sent = await Engine.SendTextToAsync(endpoint, "Привет!").ConfigureAwait(false);

        OnUi(() =>
        {
            if (sent.State != MessageState.Delivered)
            {
                Status = $"{endpoint} не отвечает";
                return;
            }

            var peer = GetOrCreate(sent.Peer);
            peer.Add(MessageViewModel.From(sent));
            SelectedPeer = peer;
            Status = $"подключился к {endpoint}";
        });
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var answer = await PromptAsync("Как меня видят", "Имя в списке участников", MyName);
        if (string.IsNullOrWhiteSpace(answer))
            return;

        Engine.LocalDisplayName = answer.Trim();
        MyName = Engine.LocalDisplayName;
    }

    [RelayCommand]
    private void CancelTransfer(TransferViewModel? transfer)
    {
        if (transfer is not null)
            Engine.CancelTransfer(transfer.TransferId);
    }

    [RelayCommand]
    private async Task OpenFolderAsync(TransferViewModel? transfer)
    {
        if (transfer?.DestinationPath is not { } path)
            return;

        try
        {
            await Launcher.Default.OpenAsync(new Uri($"file://{path}"));
        }
        catch (Exception e)
        {
            Status = $"не удалось открыть папку: {e.Message}";
        }
    }

    /// <summary>
    /// Кладёт текст сообщения в буфер обмена целиком.
    /// </summary>
    /// <remarks>
    /// Перевода в главный поток здесь нет намеренно. Буфер обмена у всех трёх систем —
    /// главный поток и передний план: WinRT бросает RPC_E_WRONG_THREAD из чужого потока
    /// и отказывает свёрнутому окну, UIKit и Android ведут себя недетерминированно.
    /// Команда всегда приходит из нажатия, то есть уже оттуда, откуда надо. Звать её
    /// из фоновых задач нельзя.
    ///
    /// Android 13 и новее показывает своё «Скопировано» сам, и строка состояния его
    /// дублирует. Убирать её из-за этого не за что: на Windows и macOS система не
    /// показывает ничего, а без отклика непонятно, сработало ли.
    /// </remarks>
    [RelayCommand]
    private async Task CopyTextAsync(MessageViewModel? message)
    {
        if (message is null)
            return;

        try
        {
            await Clipboard.Default.SetTextAsync(message.Text);
            Status = "скопировал в буфер обмена";
        }
        catch (Exception e)
        {
            Status = $"не удалось скопировать: {e.Message}";
        }
    }

    [RelayCommand]
    private void GoBack() => SelectedPeer = null;

    /// <summary>
    /// Открывает экран «О программе».
    /// </summary>
    /// <remarks>
    /// Навигация здесь, а не в коде страницы, по той же причине, по которой здесь
    /// же живут диалоги: все кнопки шапки привязаны командами, и одна кнопка
    /// с обработчиком выбивалась бы из ряда. Возврат никто не пишет — его даёт
    /// стек навигации Shell, и на Android это аппаратная «Назад».
    /// </remarks>
    [RelayCommand]
    private static Task ShowAboutAsync() => Shell.Current.GoToAsync("about");

    partial void OnSelectedPeerChanged(PeerViewModel? value)
    {
        if (value is not null)
            value.UnreadCount = 0;
    }

    // ─── Мелочи ──────────────────────────────────────────────────────────────

    /// <summary>
    /// События движка приходят из его потока. WinUI бросает COMException на обновление
    /// CollectionView из чужого потока, Android даёт недетерминированные падения.
    /// </summary>
    private static void OnUi(Action action)
    {
        if (MainThread.IsMainThread)
            action();
        else
            MainThread.BeginInvokeOnMainThread(action);
    }

    private static Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, accept, cancel));

    private static Task<string?> PromptAsync(string title, string message, string initial) =>
        MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayPromptAsync(title, message, "Готово", "Отмена", initialValue: initial));

    public static string Size(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{value / (double)(1L << 10):0.#} КБ",
        _ => $"{value} Б",
    };
}
