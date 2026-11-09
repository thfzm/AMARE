param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$DownloadDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareDl {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, out System.Drawing.Point point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f,int x,int y,int d,UIntPtr e);
}
'@ -ReferencedAssemblies System.Drawing
$script:results=[System.Collections.Generic.List[object]]::new()
function Add-Result([string]$name,[string]$state,[string]$detail){$script:results.Add([pscustomobject]@{name=$name;result=$state;detail=$detail});Write-Host "$state ${name}: $detail"}
function Read-State { if(-not(Test-Path $Status)){return $null};try{return Get-Content $Status -Raw|ConvertFrom-Json}catch{return $null} }
function Wait-State([scriptblock]$cond,[int]$sec,[string]$label){$d=(Get-Date).AddSeconds($sec);while((Get-Date)-lt$d){$s=Read-State;if($null-ne$s -and (& $cond $s)){return $s};Start-Sleep -Milliseconds 250};throw "Timed out waiting for $label"}
function Shot([string]$name){$r=New-Object AmareDl+RECT;[void][AmareDl]::GetWindowRect($script:hwnd,[ref]$r);$w=[Math]::Max(1,$r.Right-$r.Left);$h=[Math]::Max(1,$r.Bottom-$r.Top);$b=New-Object System.Drawing.Bitmap($w,$h);$g=[System.Drawing.Graphics]::FromImage($b);$g.CopyFromScreen($r.Left,$r.Top,0,0,$b.Size);$p=Join-Path $ScreenshotDir $name;$b.Save($p,[System.Drawing.Imaging.ImageFormat]::Png);$g.Dispose();$b.Dispose();Add-Result $name "PASS" "$p ($w x $h, $((Get-Item $p).Length) bytes)"}
function Click-Web([int]$x,[int]$y){$pt=New-Object System.Drawing.Point;[void][AmareDl]::ClientToScreen($script:hwnd,[ref]$pt);[void][AmareDl]::SetForegroundWindow($script:hwnd);[System.Windows.Forms.Cursor]::Position=New-Object System.Drawing.Point(($pt.X+$x),($pt.Y+88+$y));[AmareDl]::mouse_event(2,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 80;[AmareDl]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 300}
function PageState { $s=Read-State;if($null-eq$s-or-not$s.title.StartsWith("AMARE_TEST:")){return $null};return [Uri]::UnescapeDataString($s.title.Substring(11))|ConvertFrom-Json }
$script:hwnd=[IntPtr]::Zero;$app=$null;$server=$null;$before=@(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
Remove-Item $Status -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir,$DownloadDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataDir,$DownloadDir,$ScreenshotDir|Out-Null
$page=(Resolve-Path "apps/amare-gpui/test-page").Path
$downloadFile=Join-Path $page "download-test.bin"
[IO.File]::WriteAllBytes($downloadFile,(New-Object byte[] (5*1024*1024)))
$server=Start-Process python -ArgumentList @("tools/slow_http_server.py",$page,"8766") -PassThru
$env:AMARE_GPUI_STATUS=$Status;$env:AMARE_GPUI_DATA_DIR=$DataDir;$env:AMARE_GPUI_DOWNLOAD_DIR=$DownloadDir
$env:AMARE_GPUI_START_URL="http://127.0.0.1:8766/index.html?tab=DL";$env:AMARE_GPUI_SELF_TEST="0"
try {
 Start-Sleep -Seconds 2
 $app=Start-Process $Exe -PassThru
 [void](Wait-State {param($s) $s.source -like "*tab=DL*" -and $null-ne$s.address} 60 "download test page")
 $app.Refresh();$script:hwnd=$app.MainWindowHandle;if($script:hwnd-eq[IntPtr]::Zero){throw "window missing"}
 [void][AmareDl]::SetForegroundWindow($script:hwnd)
 $pageState=PageState
 if($null-eq$pageState-or$null-eq$pageState.downloadRect){throw "download link rect unavailable"}
 Click-Web ([int]$pageState.downloadRect.left+5) ([int]$pageState.downloadRect.top+5)
 $started=Wait-State {param($s) @($s.downloads).Count -ge 1} 20 "download start"
 [System.Windows.Forms.SendKeys]::SendWait("^+d")
 [void](Wait-State {param($s) $s.panel -eq "downloads"} 10 "downloads panel")
 Shot "09-download-progress.png"
 $complete=Wait-State {param($s) @($s.downloads|Where-Object{$_.state-eq"completed"}).Count -ge 1} 90 "download complete"
 Add-Result "download-complete" "PASS" "count=$(@($complete.downloads).Count)"
 Shot "10-download-complete.png"
 [System.Windows.Forms.SendKeys]::SendWait("^+d");Start-Sleep -Milliseconds 300
 $pageState=PageState
 if($null-ne$pageState.downloadFailRect){
   Click-Web ([int]$pageState.downloadFailRect.left+5) ([int]$pageState.downloadFailRect.top+5)
   Start-Sleep -Seconds 2
   [System.Windows.Forms.SendKeys]::SendWait("^+d");Start-Sleep -Milliseconds 500
   Shot "11-download-failed-or-cancel.png"
 }
 Add-Result "download-context-menu" "UNVERIFIED" "custom context menu not implemented; default WebView2 menu not asserted"
 [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
 if(-not$app.WaitForExit(20000)){throw "download app did not exit"}
 Start-Sleep -Seconds 2
 $after=@(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
 if($after-gt$before){throw "WebView2 remained after download app exit"}
 Add-Result "download-exit-cleanup" "PASS" "exit=$($app.ExitCode) webview2=$before->$after"
} catch {Add-Result "fatal" "FAIL" $_.Exception.Message;try{Shot "99-download-failure.png"}catch{}}
finally {if($null-ne$app-and-not$app.HasExited){try{$app.Kill()}catch{}};if($null-ne$server){try{Stop-Process -Id $server.Id -Force}catch{}};$summary=[pscustomobject]@{generated_at=(Get-Date).ToUniversalTime().ToString("o");results=$script:results;pass=@($script:results|? result -eq "PASS").Count;fail=@($script:results|? result -eq "FAIL").Count;unverified=@($script:results|? result -eq "UNVERIFIED").Count};$summary|ConvertTo-Json -Depth 8|Set-Content (Join-Path $ScreenshotDir "gpui-download-results.json") -Encoding UTF8;Write-Host "GPUI download summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)";if($summary.fail-gt 0){exit 1}}
