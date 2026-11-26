using System.Text.Json.Serialization;

namespace Amare.Windows.Core;

public sealed class BackupRecordDto
{
    public string FileName { get; init; } = "";
    public long CreatedAtMs { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public bool Encrypted { get; init; }
    public uint FormatVersion { get; init; }
    public string AppVersion { get; init; } = "";
    public string Scope { get; init; } = "";
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public IReadOnlyList<string> DataKinds { get; init; } = [];
    public bool ContainsSecrets { get; init; }
    public bool ContainsVault { get; init; }
}

public sealed class BackupManifestDto
{
    public uint FormatVersion { get; init; }
    public string AppVersion { get; init; } = "";
    public long CreatedAtMs { get; init; }
    public string Scope { get; init; } = "";
    public IReadOnlyList<string> DataKinds { get; init; } = [];
    public IReadOnlyList<BackupProfileDto> Profiles { get; init; } = [];
    public uint? RootSchemaVersion { get; init; }
    public string Compression { get; init; } = "";
    public string Encryption { get; init; } = "";
    public bool ContainsSecrets { get; init; }
    public bool ContainsVault { get; init; }
    public IReadOnlyList<string> VaultProfiles { get; init; } = [];
    public string Consistency { get; init; } = "";
    public IReadOnlyList<BackupFileDto> Files { get; init; } = [];
}

public sealed class BackupProfileDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public uint SchemaVersion { get; init; }
}

public sealed class BackupFileDto
{
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "";
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class BackupVaultSummaryDto
{
    public string Profile { get; init; } = "";
    public uint Format { get; init; }
    public ulong Generation { get; init; }
    public int EntryCount { get; init; }
    public int TombstoneCount { get; init; }
}

public sealed class BackupOperationResult
{
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public long? SizeBytes { get; init; }
    public bool ContainsVault { get; init; }
    public BackupRecordDto? Record { get; init; }
    public BackupManifestDto? Manifest { get; init; }
    public IReadOnlyList<BackupVaultSummaryDto> Vault { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> RestoredProfiles { get; init; } = [];
    public string? RecoveryDir { get; init; }
    public bool Verified { get; init; }
    public bool Recovered { get; init; }

    [JsonIgnore]
    public bool Succeeded => Status is "ok" or "Valid" or "ValidWithWarnings";

    [JsonIgnore]
    public bool Valid => Status is "Valid" or "ValidWithWarnings";
}

public enum BackupVerificationStatus
{
    Valid,
    ValidWithWarnings,
    UnsupportedVersion,
    Corrupt,
    InvalidPassword,
    UnsafeArchive,
    IncompatibleSchema
}
