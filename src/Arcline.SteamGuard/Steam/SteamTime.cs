using System.Text.Json;

namespace Arcline.SteamGuard.Steam;

/// <summary>
/// Keeps the offset between the local clock and Steam's clock.
/// Unlike the old TimeAligner it re-syncs periodically, never blocks the UI thread,
/// and backs off when Steam is unreachable instead of retrying every second.
/// </summary>
public sealed class SteamTime
{
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(60);

    private readonly SteamHttp _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _offsetSeconds;
    private DateTimeOffset _nextSync = DateTimeOffset.MinValue;

    public SteamTime(SteamHttp http) => _http = http;

    public static SteamTime Shared { get; } = new(SteamHttp.Shared);

    public bool IsSynced { get; private set; }
    public long OffsetSeconds => Interlocked.Read(ref _offsetSeconds);

    /// <summary>Current Steam time using the last known offset (no network).</summary>
    public long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OffsetSeconds;

    /// <summary>Current Steam time, syncing first if the offset is unknown or stale.</summary>
    public async Task<long> NowAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow >= _nextSync) await SyncAsync(force: false, ct).ConfigureAwait(false);
        return Now;
    }

    public async Task SyncAsync(bool force = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && DateTimeOffset.UtcNow < _nextSync) return;
            var before = DateTimeOffset.UtcNow;
            try
            {
                var body = await _http.PostFormAsync(SteamEndpoints.ApiMethod("ITwoFactorService", "QueryTime"),
                    [new("steamid", "0")], ct: ct).ConfigureAwait(false);
                var after = DateTimeOffset.UtcNow;
                using var doc = JsonDocument.Parse(body);
                var st = doc.RootElement.GetProperty("response").GetProperty("server_time");
                var server = st.ValueKind == JsonValueKind.String ? long.Parse(st.GetString()!) : st.GetInt64();
                var localMid = (before.ToUnixTimeMilliseconds() + after.ToUnixTimeMilliseconds()) / 2000;
                Interlocked.Exchange(ref _offsetSeconds, server - localMid);
                IsSynced = true;
                _nextSync = after + ResyncInterval;
            }
            catch (Exception ex) when (ex is SteamApiException or JsonException or KeyNotFoundException or FormatException)
            {
                _nextSync = DateTimeOffset.UtcNow + FailureBackoff;
            }
        }
        finally { _gate.Release(); }
    }
}
