using System.Collections.Concurrent;

namespace InventoryServer.Web;

/// <summary>Locks a key (client IP or username) for a while after too many failed logins.</summary>
public class LoginThrottle
{
    readonly int _maxFailures;
    readonly TimeSpan _lockout;
    readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> _state = new();

    public LoginThrottle(int maxFailures = 5, TimeSpan? lockout = null)
    {
        _maxFailures = maxFailures;
        _lockout = lockout ?? TimeSpan.FromMinutes(15);
    }

    public bool IsLocked(string key) =>
        _state.TryGetValue(key, out var s) && s.Until > DateTime.UtcNow;

    public void Failure(string key)
    {
        _state.AddOrUpdate(key,
            _ => (1, DateTime.MinValue),
            (_, s) =>
            {
                // A failure after a lockout has expired starts a fresh count.
                var failures = s.Until != DateTime.MinValue && s.Until <= DateTime.UtcNow ? 1 : s.Failures + 1;
                return failures >= _maxFailures ? (0, DateTime.UtcNow + _lockout) : (failures, DateTime.MinValue);
            });
    }

    public void Success(string key) => _state.TryRemove(key, out _);
}
