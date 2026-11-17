using Arcline.SteamGuard.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcline.SDA.Desktop.ViewModels;

public sealed partial class AccountItem(ulong steamId, string name) : ObservableObject
{
    public ulong SteamId { get; } = steamId;
    public string Name { get; } = name;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasPending))] private int _pending;
    [ObservableProperty] private bool _needsLogin;

    public bool HasPending => Pending > 0;
}

public sealed partial class ConfirmationItem(Confirmation c) : ObservableObject
{
    public Confirmation Confirmation { get; } = c;
    public ulong Id => Confirmation.Id;
    public string Headline => Confirmation.Headline ?? Confirmation.TypeName ?? "Confirmation";
    public string Summary => Confirmation.Description;
    public string TypeText => Confirmation.Type == ConfirmationType.Unknown ? Confirmation.TypeName ?? $"Type {Confirmation.RawType}" : Confirmation.Type.ToString();
    public string When => Confirmation.CreationTime > 0 ? Confirmation.Created.ToLocalTime().ToString("g") : "";
    public string AcceptText => string.IsNullOrWhiteSpace(Confirmation.AcceptText) ? "Accept" : Confirmation.AcceptText!;
    public string CancelText => string.IsNullOrWhiteSpace(Confirmation.CancelText) ? "Deny" : Confirmation.CancelText!;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsIdle))] private bool _busy;
    [ObservableProperty] private string? _error;

    public bool IsIdle => !Busy;
}
