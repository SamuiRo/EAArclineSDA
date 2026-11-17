using System.Collections.Concurrent;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Steam;

/// <summary>
/// Where the services persist things that change at runtime: refreshed tokens and the
/// "trade protection acknowledged" flag. The vault implements it; external programs can use
/// <see cref="InMemoryAccountStateStore"/> or their own storage.
/// </summary>
public interface IAccountStateStore
{
    Task SessionUpdatedAsync(SteamGuardAccount account, CancellationToken ct);
    bool IsTradeProtectionAcknowledged(ulong steamId);
    Task SetTradeProtectionAcknowledgedAsync(ulong steamId, bool value, CancellationToken ct);
}

public sealed class InMemoryAccountStateStore : IAccountStateStore
{
    private readonly ConcurrentDictionary<ulong, bool> _acks = new();
    public Task SessionUpdatedAsync(SteamGuardAccount account, CancellationToken ct) => Task.CompletedTask;
    public bool IsTradeProtectionAcknowledged(ulong steamId) => _acks.TryGetValue(steamId, out var v) && v;
    public Task SetTradeProtectionAcknowledgedAsync(ulong steamId, bool value, CancellationToken ct)
    {
        _acks[steamId] = value;
        return Task.CompletedTask;
    }
}
