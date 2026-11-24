using Amare.Windows.Core;

namespace Amare.Windows;

internal static class VaultIdleMonitor
{
    private static readonly object Gate = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static VaultIdlePolicy? _policy;
    private static Timer? _timer;
    private static bool _started;

    public static event Action? Expired;

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
            var seconds = 300;
            if (int.TryParse(Environment.GetEnvironmentVariable("AMARE_VAULT_IDLE_SECONDS"), out var configured)
                && configured > 0)
            {
                seconds = configured;
            }
            _policy = new VaultIdlePolicy(TimeSpan.FromSeconds(seconds));
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    public static void Touch()
    {
        lock (Gate)
        {
            _policy?.Touch();
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _started = false;
            _timer?.Dispose();
            _timer = null;
            _policy = null;
        }
    }

    private static void Tick()
    {
        VaultIdlePolicy? policy;
        lock (Gate)
        {
            policy = _policy;
        }
        if (policy?.TryConsumeExpiration() == true)
        {
            Expired?.Invoke();
        }
    }
}
