namespace Amare.Windows.Core;

public sealed class VaultStatusDto
{
    public string ProfileId { get; init; } = "";
    public bool Initialized { get; init; }
    public bool Unlocked { get; init; }
    public int EntryCount { get; init; }
    public int TombstoneCount { get; init; }
    public ulong Generation { get; init; }
    public bool KeyPresent { get; init; }
}

public class VaultEntryViewDto
{
    public ulong Id { get; init; }
    public string Origin { get; init; } = "";
    public string Username { get; init; } = "";
    public string Title { get; init; } = "";
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }
    public ulong Revision { get; init; }
}

/// <summary>
/// A secret-bearing response. It is produced only by explicit get/get_for_fill
/// calls. The password is a .NET immutable string, so callers must use it
/// immediately (the future fill path), never log it, and avoid retaining it.
/// <see cref="ClearPassword"/> is best-effort; it cannot erase the original
/// string allocation.
/// </summary>
public sealed class VaultEntryDto : VaultEntryViewDto
{
    public string Password { get; set; } = "";

    public void ClearPassword() => Password = string.Empty;
}

public sealed class VaultListResponseDto
{
    public List<VaultEntryViewDto> Entries { get; init; } = [];
}

public sealed class VaultEntryResponseDto
{
    public VaultEntryViewDto Entry { get; init; } = new();
}

public sealed class VaultDeleteResponseDto
{
    public ulong Revision { get; init; }
}

public sealed class VaultBackupExportDto
{
    public uint Format { get; init; }
    public string PayloadHex { get; init; } = "";
}

public sealed class VaultBackupSummaryDto
{
    public uint Format { get; init; }
    public ulong Generation { get; init; }
    public int EntryCount { get; init; }
    public int TombstoneCount { get; init; }
}

/// <summary>
/// A stored site-permission decision. The app never stores passwords or
/// authentication data here; only origin, permission kind and decision.
/// </summary>
public sealed class SitePermissionDto
{
    public string ProfileId { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Decision { get; init; } = "";
    public string Scope { get; init; } = "";
    public long Revision { get; init; }
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }
}

public sealed class SitePermissionListDto
{
    public List<SitePermissionDto> Permissions { get; init; } = [];
}

public sealed class SitePermissionResponseDto
{
    public SitePermissionDto? Permission { get; init; }
}

public sealed class SitePermissionDeleteDto
{
    public bool Deleted { get; init; }
}

public sealed class SitePermissionCountDto
{
    public int Removed { get; init; }
}
