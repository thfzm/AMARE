using Microsoft.Win32;

namespace Amare.Windows;

internal static class VaultSessionMonitor
{
    private static readonly object Gate = new();
    private static bool _started;

    public static event Action? Locked;

    public static bool IsSupported { get; private set; }

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                IsSupported = true;
            }
            catch
            {
                IsSupported = false;
            }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started)
            {
                return;
            }
            _started = false;
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            catch
            {
                // Session events are best-effort.
            }
        }
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect)
        {
            Locked?.Invoke();
        }
    }
}
