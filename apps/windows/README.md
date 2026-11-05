# AMARE Windows

AMARE의 주력 플랫폼은 Windows입니다. 이 디렉터리는 WinUI 3 + WebView2 Evergreen 기반 데스크톱 브라우저입니다.

## 0.17.0 구현 상태

| 영역 | 상태 |
|---|---|
| WinUI 3 XAML/C# build | **PASS** (Windows Runner, Windows Server 2025) |
| 실제 GUI UIAutomation | **PASS 12/12** (window/address/tabs/Find/Reader/AI/settings/API settings/update check/session) |
| WebView2 tabs/address/navigation | 구현, GUI automation PASS |
| Bookmarks/History Rust persistence | **PASS** (C# native interop round-trip) |
| Downloads detection/persistent list | 구현, GUI **UNVERIFIED** |
| Session restore | 구현, GUI UIAutomation **PASS** |
| Reader Mode | 구현, GUI UIAutomation **PASS** |
| Find in Page (WebView2 Find API) | 구현, GUI UIAutomation **PASS** (1/2) |
| AI Web Panel (ChatGPT/Gemini/Claude web) | 패널 open/close **PASS**; 실제 로그인/OAuth **UNVERIFIED** |
| Logo/ICO | 원본 `AMARE_logo.png`에서 파생, package 포함 |
| MSIX package | **PASS (unsigned)** |
| MSIX install | **BLOCKED** — unsigned publisher namespace |
| Windows 11 real device/GUI | **UNVERIFIED** (Runner is Windows Server 2025) |
| DPI 100/125/150/200%, multi-monitor, Korean IME | **UNVERIFIED** |
| Code signing | **BLOCKED** — no distribution certificate |
| GitHub Releases update check | 구현, **Release 0개 / 실제 update UNVERIFIED** |
| API-based AI | 구현, no-key/실제 API **UNVERIFIED** |

## Build

```powershell
cd apps/windows
dotnet restore AMARE.Windows/AMARE.Windows.csproj -p:Platform=x64
dotnet build AMARE.Windows/AMARE.Windows.csproj -c Release -p:Platform=x64
```

`amare_ffi.dll`은 Rust FFI build 결과에서 앱 출력으로 복사됩니다.

## Run

```powershell
dotnet run --project AMARE.Windows/AMARE.Windows.csproj -p:Platform=x64
```

또는 Release build의 `AMARE.Windows.exe`를 실행합니다. Windows App SDK Runtime과 WebView2 Evergreen Runtime이 필요합니다.

## Portable package

`tools/verify-windows.ps1 -CI`가 다음 portable ZIP을 생성합니다.

```text
artifacts/windows/AMARE-0.17.0-win-x64.zip
```

이 ZIP은 개발/테스트용 portable package이며 서명된 MSIX 설치 프로그램이 아닙니다.

## Security / data

- WebView2 UserDataFolder는 프로필별로 분리됩니다.
- AI 패널은 별도 `ai-webview2` UserDataFolder를 사용합니다.
- 페이지 정보/본문은 사용자가 “Page info”/“Article”을 누를 때만 클립보드로 복사되며 자동 전송되지 않습니다.
- Release signing key는 저장소에 포함하지 않습니다.
- 로그에 URL/검색어/페이지 본문/비밀키를 기록하지 않습니다.

## Logo

원본 `AMARE_logo.png`(1254x1254 RGBA)는 보존합니다. Windows용 `Assets/AMARE.ico`와 `Assets/AMARE_logo_256.png`는 원본에서 비율/투명도를 유지해 파생했습니다. 새 로고를 디자인하거나 텍스트를 추가하지 않았습니다.
