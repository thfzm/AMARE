namespace Amare.Windows.Core;

/// <summary>
/// Canonical names shared by the WinUI permission UI and the Rust store. These
/// are the only permission kinds AMARE can persist; WebView2 unknown kinds are
/// denied without a prompt.
/// </summary>
public static class SitePermissionCatalog
{
    public static readonly IReadOnlyList<string> Kinds =
    [
        "microphone",
        "camera",
        "geolocation",
        "notifications",
        "other_sensors",
        "clipboard_read",
        "multiple_automatic_downloads",
        "file_read_write",
        "autoplay",
        "local_fonts",
        "midi_system_exclusive_messages",
        "window_management",
        "persistent_storage"
    ];

    public static bool IsSupported(string? kind) =>
        kind is not null && Kinds.Contains(kind, StringComparer.Ordinal);

    public static string LocalizedDisplayName(string kind) => kind switch
    {
        "microphone" => L10n.T("permissions.kind.microphone"),
        "camera" => L10n.T("permissions.kind.camera"),
        "geolocation" => L10n.T("permissions.kind.geolocation"),
        "notifications" => L10n.T("permissions.kind.notifications"),
        "other_sensors" => L10n.T("permissions.kind.other_sensors"),
        "clipboard_read" => L10n.T("permissions.kind.clipboard_read"),
        "multiple_automatic_downloads" => L10n.T("permissions.kind.multiple_automatic_downloads"),
        "file_read_write" => L10n.T("permissions.kind.file_read_write"),
        "autoplay" => L10n.T("permissions.kind.autoplay"),
        "local_fonts" => L10n.T("permissions.kind.local_fonts"),
        "midi_system_exclusive_messages" => L10n.T("permissions.kind.midi_system_exclusive_messages"),
        "window_management" => L10n.T("permissions.kind.window_management"),
        "persistent_storage" => L10n.T("permissions.kind.persistent_storage"),
        _ => L10n.T("permissions.kind.unknown")
    };

    public static string DisplayName(string kind) => kind switch
    {
        "microphone" => "Microphone",
        "camera" => "Camera",
        "geolocation" => "Location",
        "notifications" => "Notifications",
        "other_sensors" => "Motion and light sensors",
        "clipboard_read" => "Clipboard read",
        "multiple_automatic_downloads" => "Multiple automatic downloads",
        "file_read_write" => "File access",
        "autoplay" => "Autoplay",
        "local_fonts" => "Local fonts",
        "midi_system_exclusive_messages" => "MIDI devices",
        "window_management" => "Window management",
        "persistent_storage" => "Persistent storage",
        _ => "Unknown permission"
    };
}
