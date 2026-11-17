function Get-AmareExitAssessment {
    param(
        [Nullable[int64]]$ExitCode,
        [bool]$TimedOut = $false,
        [bool]$Cancelled = $false,
        [bool]$ProcessHandleValid = $true
    )
    if ($Cancelled) {
        return [pscustomobject]@{ State = "UNVERIFIED"; Classification = "ENVIRONMENT_LIMITATION"; Hex = ""; Detail = "runner cancelled the job" }
    }
    if ($TimedOut) {
        return [pscustomobject]@{ State = "UNVERIFIED"; Classification = "TIMEOUT"; Hex = ""; Detail = "process did not exit before timeout; harness force-stopped it" }
    }
    if (-not $ProcessHandleValid) {
        return [pscustomobject]@{ State = "UNVERIFIED"; Classification = "PROCESS_HANDLE_INVALID"; Hex = ""; Detail = "process handle is invalid; exit code unavailable" }
    }
    if ($null -eq $ExitCode) {
        return [pscustomobject]@{ State = "UNVERIFIED"; Classification = "NO_EXIT_CODE"; Hex = ""; Detail = "exit code was not observed" }
    }
    $unsigned = [uint32]([int64]$ExitCode -band 0xFFFFFFFFL)
    $hex = ('0x{0:X8}' -f $unsigned)
    if ($unsigned -eq 0) {
        return [pscustomobject]@{ State = "PASS"; Classification = "NORMAL_EXIT"; Hex = $hex; Detail = "exit=0" }
    }
    if ($hex -eq "0xC0000409") {
        return [pscustomobject]@{ State = "FAIL"; Classification = "NTSTATUS"; Hex = $hex; Detail = "exit=$ExitCode hex=$hex STATUS_STACK_BUFFER_OVERRUN" }
    }
    if ($unsigned -ge [uint32]2147483648) {
        return [pscustomobject]@{ State = "FAIL"; Classification = "NTSTATUS"; Hex = $hex; Detail = "exit=$ExitCode hex=$hex NTSTATUS" }
    }
    return [pscustomobject]@{ State = "FAIL"; Classification = "NONZERO_EXIT"; Hex = $hex; Detail = "exit=$ExitCode hex=$hex non-zero" }
}
