using ChudikChat.Core.Transfer;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChudikChat.App.ViewModels;

public partial class TransferViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    public partial TransferState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial double Fraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    public partial string? DestinationPath { get; set; }

    public required Guid TransferId { get; init; }

    public required string RootName { get; init; }

    public required bool IsIncoming { get; init; }

    public string Title => IsIncoming ? $"⇩ {RootName}" : $"⇧ {RootName}";

    public bool CanCancel => State is TransferState.Offered or TransferState.Running;

    public bool IsFinished => !CanCancel;

    public bool CanOpenFolder => State == TransferState.Completed
        && IsIncoming
        && !string.IsNullOrEmpty(DestinationPath);

    public string StateText => State switch
    {
        TransferState.Offered => IsIncoming ? "ждём вашего решения" : "ждём ответа",
        TransferState.Running => $"{Fraction:P0}",
        TransferState.Completed => IsIncoming ? "принято" : "отправлено",
        TransferState.Declined => Error is null ? "отклонено" : $"отклонено: {Error}",
        TransferState.Cancelled => "отменено",
        _ => Error is null ? "не удалось" : $"не удалось: {Error}",
    };

    public void Apply(TransferProgress progress)
    {
        State = progress.State;
        Fraction = progress.Fraction;
        Error = progress.Error;

        if (!string.IsNullOrEmpty(progress.DestinationPath))
            DestinationPath = progress.DestinationPath;
    }

    public static TransferViewModel From(TransferProgress progress)
    {
        var model = new TransferViewModel
        {
            TransferId = progress.TransferId,
            RootName = progress.RootName,
            IsIncoming = progress.Direction == TransferDirection.Incoming,
        };

        model.Apply(progress);
        return model;
    }
}
