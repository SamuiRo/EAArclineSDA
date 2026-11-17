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

public enum ConfirmationFilter { All, Trades, Market, Other }

public sealed partial class ConfirmationItem(Confirmation c, AccountItem account, bool showAccount) : ObservableObject
{
    public Confirmation Confirmation { get; } = c;
    public AccountItem Account { get; } = account;
    public bool ShowAccount { get; } = showAccount;
    public string AccountName => Account.Name;
    public ulong Id => Confirmation.Id;
    public string Headline => Confirmation.Headline ?? Confirmation.TypeName ?? "Confirmation";
    public string Summary => Confirmation.Description;
    public string TypeText => Confirmation.Type == ConfirmationType.Unknown ? Confirmation.TypeName ?? $"Type {Confirmation.RawType}" : Confirmation.Type.ToString();
    public string When => Confirmation.CreationTime > 0 ? Confirmation.Created.ToLocalTime().ToString("g") : "";
    public string AcceptText => string.IsNullOrWhiteSpace(Confirmation.AcceptText) ? "Accept" : Confirmation.AcceptText!;
    public string CancelText => string.IsNullOrWhiteSpace(Confirmation.CancelText) ? "Deny" : Confirmation.CancelText!;

    public bool Matches(ConfirmationFilter f) => f switch
    {
        ConfirmationFilter.Trades => Confirmation.Type == ConfirmationType.Trade,
        ConfirmationFilter.Market => Confirmation.Type == ConfirmationType.MarketListing,
        ConfirmationFilter.Other => Confirmation.Type is not (ConfirmationType.Trade or ConfirmationType.MarketListing),
        _ => true,
    };

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsIdle))] private bool _busy;
    [ObservableProperty] private string? _error;

    public bool IsIdle => !Busy;
}
