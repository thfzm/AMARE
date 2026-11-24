namespace Amare.Windows.Core;

/// <summary>
/// Pure idle-lock policy used by the WinUI vault lifetime manager and tests.
/// Activity is global to the app (not per window).
/// </summary>
public sealed class VaultIdlePolicy
{
    private readonly object _gate = new();
    private DateTimeOffset _lastActivity;
    private bool _expired;

    public VaultIdlePolicy(TimeSpan timeout, DateTimeOffset? now = null)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        Timeout = timeout;
        _lastActivity = now ?? DateTimeOffset.UtcNow;
    }

    public TimeSpan Timeout { get; }

    public DateTimeOffset LastActivity
    {
        get
        {
            lock (_gate)
            {
                return _lastActivity;
            }
        }
    }

    public void Touch(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            _lastActivity = now ?? DateTimeOffset.UtcNow;
            _expired = false;
        }
    }

    /// <summary>Returns true once per idle interval.</summary>
    public bool TryConsumeExpiration(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_expired)
            {
                return false;
            }
            var current = now ?? DateTimeOffset.UtcNow;
            if (current - _lastActivity < Timeout)
            {
                return false;
            }
            _expired = true;
            return true;
        }
    }

    public bool IsExpired(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var current = now ?? DateTimeOffset.UtcNow;
            return current - _lastActivity >= Timeout;
        }
    }
}
