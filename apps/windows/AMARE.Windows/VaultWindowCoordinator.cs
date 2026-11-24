namespace Amare.Windows;

internal static class VaultWindowCoordinator
{
    public static event Action<Guid>? LockRequested;
    public static event Action<Guid>? ProfileChanged;
    public static event Action<Guid, string>? Unlocked;

    public static void RequestLock(Guid sender) => LockRequested?.Invoke(sender);

    public static void NotifyProfileChanged(Guid sender) => ProfileChanged?.Invoke(sender);

    public static void NotifyUnlocked(Guid sender, string profileId) =>
        Unlocked?.Invoke(sender, profileId);
}
