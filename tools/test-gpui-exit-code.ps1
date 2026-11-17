$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-exit-code.ps1"

$cases = @(
    @{ Name = "zero"; Args = @{ ExitCode = 0 }; State = "PASS"; Classification = "NORMAL_EXIT"; Hex = "0x00000000" },
    @{ Name = "nonzero"; Args = @{ ExitCode = 3 }; State = "FAIL"; Classification = "NONZERO_EXIT"; Hex = "0x00000003" },
    @{ Name = "ntstatus-signed"; Args = @{ ExitCode = -1073740791 }; State = "FAIL"; Classification = "NTSTATUS"; Hex = "0xC0000409" },
    @{ Name = "ntstatus-unsigned"; Args = @{ ExitCode = 3221226505 }; State = "FAIL"; Classification = "NTSTATUS"; Hex = "0xC0000409" },
    @{ Name = "timeout"; Args = @{ ExitCode = $null; TimedOut = $true }; State = "UNVERIFIED"; Classification = "TIMEOUT"; Hex = "" },
    @{ Name = "cancelled"; Args = @{ ExitCode = $null; Cancelled = $true }; State = "UNVERIFIED"; Classification = "ENVIRONMENT_LIMITATION"; Hex = "" },
    @{ Name = "invalid-handle"; Args = @{ ExitCode = $null; ProcessHandleValid = $false }; State = "UNVERIFIED"; Classification = "PROCESS_HANDLE_INVALID"; Hex = "" }
)

$failed = 0
foreach ($case in $cases) {
    $arguments = $case.Args
    $result = Get-AmareExitAssessment @arguments
    $ok = $result.State -eq $case.State -and $result.Classification -eq $case.Classification -and $result.Hex -eq $case.Hex
    if ($ok) {
        Write-Host "PASS exit-code-$($case.Name): state=$($result.State) class=$($result.Classification) hex=$($result.Hex)"
    } else {
        Write-Host "FAIL exit-code-$($case.Name): got state=$($result.State) class=$($result.Classification) hex=$($result.Hex)"
        $failed++
    }
}

if ($failed -gt 0) { exit 1 }
Write-Host "exit-code classifier: 7/7 PASS"
