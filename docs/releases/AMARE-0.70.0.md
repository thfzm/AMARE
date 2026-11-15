# AMARE 0.70.0 — Window Lifecycle, Session Fidelity & Stability

0.70.0은 GPUI의 창 종료/복원 lifecycle과 Session Restore fidelity를
검증한 버전입니다. **GPUI를 기본 패키지로 전환하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- GPUI 최고 반복: **1 FAIL / 17 UNVERIFIED**
- 동일 코드 3회: **2 FAIL / 3 FAIL / 1 FAIL** — 3회 연속 0 FAIL **미달**
- Titlebar minimize/maximize: 실제 mouse **PASS**
- Titlebar restore: **UNVERIFIED**
- 다중 창 중 하나 닫기/마지막 창 종료: 다중 창 복원이 1개로 줄어 **UNVERIFIED**
- Session active URL: PASS/UNVERIFIED 혼재
- Session geometry: PASS/UNVERIFIED 혼재
- Multiwindow Vault delete: 늦은 `dave` delete 자동화 **1 FAIL**
- AI/updater/site permission: 미구현

## GPUI 0.70.0 변경

- WebView2 create/navigate/set_active/focus를 **GPUI entity borrow 밖**으로
  이동해 refcell 재진입 panic을 제거했습니다.
- 복원 창의 controller attach와 status poll이 서로 re-enter하지 않도록
  lifecycle을 분리했습니다.
- titlebar min/max/close를 GPUI action으로 처리하고, close/restore/Alt+F4/
  WM_CLOSE/다중 창 close를 별도 결과로 분리했습니다.
- session test에 active navigation source, scroll, maximized state,
  corrupt session fallback 검증을 추가했습니다.
- multiwindow password action은 실제 layout bounds와 focus retry를 사용합니다.

## GPUI 최고 반복 상세

| 영역 | PASS | FAIL | UNVERIFIED |
|---|---:|---:|---:|
| password | 51 | 0 | 1 |
| multiwindow | 12 | 1 | 0 |
| input | 25 | 0 | 4 |
| tab | 21 | 0 | 7 |
| library | 20 | 0 | 0 |
| download | 7 | 0 | 1 |
| settings | 22 | 0 | 4 |
| session | 10 | 0 | 5 |

Aggregate: **1 FAIL / 17 UNVERIFIED**.

### Titlebar/Close

- minimize **PASS (real mouse)**
- maximize **PASS (real mouse)**
- restore **UNVERIFIED**
- close one of multiple windows **UNVERIFIED** (복원 창이 1개만 남음)
- last-window exit / WebView2 cleanup **UNVERIFIED/PASS 혼재**

### Session Restore

- metadata/tab count/controller attach/completion **PASS**
- active URL **PASS/UNVERIFIED 혼재**
- geometry **PASS/UNVERIFIED 혼재** (1024×720 ↔ 1028×779)
- scroll **UNVERIFIED**
- corrupt session fallback **UNVERIFIED**
- multi-window restore **UNVERIFIED** (windows=1, expected 2)

### Multiwindow Vault

- 두/세 번째 창, 창별 탭 독립성, Vault 동기화/독립 편집/충돌 **PASS**
- 늦은 `dave` delete 자동화 1건 **FAIL**
- 삭제된 entry의 재등장/데이터 손상은 확인되지 않았습니다.

## WinUI 0.70.0

- Windows UI smoke **48/48**
- Windows assertions **143/0/9/1**
- C#/Rust interop **70/70**
- Linux **SUCCESS**
- 실제 Windows 11 사용자 재검증 **UNVERIFIED**

## 기능 격차

- **미구현**: AI Web Panel, updater, GPUI 사이트 권한 관리 UI
- **설계 문서**: `docs/GPUI_MIGRATION_GAPS.md`
- **UNVERIFIED**: titlebar restore/close, multi-window restore, session
  scroll/geometry/corrupt, GPUI DPI/Snap/Alt+Tab
- **검증됨**: WinUI 전체, GPUI password/input/tab/library/download/settings,
  session metadata/controller/window 복원 일부

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.70.0-win-x64.zip` (WinUI) | 126,906,473 bytes | `740d7fc0eed0dc104324657a7ee1836c56463f75aa0f07141273fd6b37c71b7b` |
| `AMARE-0.70.0-win-x64-unsigned.msix` | 126,841,776 bytes | `d03a1fc2ce1f35acc7550739c17a43fcf31ca879df4693d200632bd0cd6cf0ac` |
| `AMARE-0.70.0-gpui-win-x64.zip` | 5,919,096 bytes | `7392e47cf866d84e424147029653f2486c5d75cf913fb5cc821c6b6af388da8a` |

- UNSIGNED. MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**.
- 공개 GitHub Release 없음.

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.70.0/` — 7 PNG
- GPUI: `artifacts/windows/screenshots/0.70.0/gpui/` — 68 PNG
  - 초기/일반 페이지/여러 탭/Settings/다운로드/두 창 동기화/Session Restore/
    titlebar maximized·restored/실패 화면

## 사용자 재검증

1. WinUI 0.66.0 체크리스트 재확인
2. GPUI WebView2/주소창/Settings 단축키
3. GPUI titlebar 최소화/최대화/복원/닫기, Alt+F4
4. 두 창 중 하나 닫기, 마지막 창 정상 종료, WebView2 정리
5. GPUI Session Restore — active URL, scroll, 창 크기/최대화, 두 창
6. 손상된 세션 fallback
7. multiwindow Vault delete/edit/충돌
8. 마지막 탭 Ctrl+W recovery, tear-off/re-merge

## 남은 문제

- GPUI 3회 연속 0 FAIL 미달
- titlebar restore/close, multi-window restore
- session scroll/geometry/corrupt fallback
- multiwindow dave delete
- AI/updater/permission 미구현
- 실제 Windows 11 사용자 검증

## GPUI 전환 판단

**0.70.0에서 GPUI를 기본값으로 전환하지 않습니다.**

WebView2 re-entrant panic 제거, 탭/library/settings, titlebar minimize/maximize,
Session metadata/controller 복원은 개선됐지만, 창 종료 lifecycle, multi-window
session restore, scroll/geometry/corrupt fallback, Vault delete, 3회 연속
0 FAIL이 남아 있습니다.
