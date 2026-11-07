# AMARE Windows Release Candidate 빌드/실행

- 작성일: 2026-11-05
- 버전: AMARE Windows **0.38.0**
- 주의: 현재는 **portable ZIP + updater helper를 제공하는 개발 빌드**이며 signed 설치 프로그램/MSIX 자동 업데이트/공개 Release는 없습니다.

---

## 1. 전제

- Windows 10/11 x64
- .NET 8 SDK
- Rust stable + `x86_64-pc-windows-msvc`
- Visual Studio 2022 Build Tools (C++/Windows SDK)
- WebView2 Evergreen Runtime
- Windows App SDK Runtime (WinUI 3 unpackaged 실행에 필요할 수 있음; 설치/부트스트랩 방식 **UNVERIFIED/REVIEW**)

## 2. 빌드

```powershell
git clone <YOUR_REPOSITORY_URL> AMARE
cd AMARE

rustup target add x86_64-pc-windows-msvc
cargo build -p amare-ffi --release
dotnet restore apps/windows/AMARE.Windows/AMARE.Windows.csproj -p:Platform=x64
dotnet build apps/windows/AMARE.Windows/AMARE.Windows.csproj -c Release -p:Platform=x64
```

`amare_ffi.dll`은 csproj의 `CopyAmareFfi` target이 다음 경로 중 하나에서 앱 출력으로 복사합니다.

- `target\release\amare_ffi.dll`
- `target\x86_64-pc-windows-msvc\release\amare_ffi.dll`

DLL이 없으면 빌드는 경고를 남기며, 앱 실행 시 native service를 찾지 못합니다.

## 3. 실행 파일 찾기

```powershell
Get-ChildItem apps\windows\AMARE.Windows\bin -Recurse -Filter AMARE.Windows.exe |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 FullName
```

WinUI 3 output 경로는 SDK/플랫폼에 따라 `bin\x64\Release\net8.0-windows10.0.19041.0\...` 아래에 생성됩니다.

## 4. 데이터/로그

```text
%LOCALAPPDATA%\AMARE\
├─ data\                       # Rust root DB + profiles\<id>\amare.db
├─ webview2\<profile-id>\       # WebView2 cookies/cache/storage
├─ logs\amare-YYYYMMDD.log     # 앱 진단 로그
└─ cleanup\pending-webview2-cleanup.json
```

로그에는 startup, profile switch/delete, tab counts, session save failure, cleanup 상태만 기록하며 URL/검색어/페이지 내용/secret은 기록하지 않습니다.

## 5. 초기화

앱 데이터를 초기화하려면 **AMARE 종료 후** 아래를 실행합니다. 이 작업은 로컬 프로필/설정/Library/AI 데이터를 삭제합니다.

```powershell
Remove-Item "$env:LOCALAPPDATA\AMARE" -Recurse -Force
```

백업이 필요한 데이터는 먼저 `%LOCALAPPDATA%\AMARE\data`를 복사하세요.

## 6. 재실행/종료

- 종료: 창 닫기
- 종료 시 마지막 세션을 최대 8초 flush
- 재실행 시 `tabs.restore_session` 설정이 true이면 마지막 세션 복원
- WebView2 프로필 폴더가 잠겨 정리되지 않으면 다음 실행에서 pending cleanup 재시도

## 7. 배포 상태

| 항목 | 상태 |
|---|---|
| Release build | WinUI CI build **PASS** |
| Portable ZIP | `artifacts/windows/AMARE-0.38.0-win-x64.zip` |
| Unsigned MSIX | `artifacts/windows/AMARE-0.38.0-win-x64-unsigned.msix` |
| MSIX install | **BLOCKED** — publisher unsigned namespace / no certificate |
| 코드 서명 | **BLOCKED** |
| Authenticode 서명 | **없음** |
| 자동 업데이트 | **없음** |
| Windows App SDK Runtime 의존성 | **UNVERIFIED — R-03 REVIEW** |
| GitHub Release 배포 | **없음** |

이 문서는 개발자/테스터가 소스에서 재현 가능한 RC를 실행하기 위한 절차이며, 최종 사용자용 설치 패키지가 아닙니다.

## 8. 소스 공개와 바이너리 배포의 구분

- 소스 저장소는 Public + All rights reserved 정책으로 공개 후보에 포함합니다.
- `Microsoft.WindowsAppSDK`, `Microsoft.Web.WebView2` 같은 SDK 의존성을 소스에 선언하는 것과
  해당 바이너리/Runtime을 재배포하는 것은 별개입니다.
- WebView2 Evergreen Runtime과 Windows App SDK Runtime의 설치/번들/고지 방식은 아직
  확정되지 않았습니다. (R-03 REVIEW)
- 설치 프로그램, 코드 서명, 자동 업데이트, 바이너리 재배포는 이번 RC 범위에 포함되지 않습니다.
