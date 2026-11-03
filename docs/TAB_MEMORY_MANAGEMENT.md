# 탭 및 메모리 관리

- 모델: `TabManager`, `BrowserTabModel`
- 진단: `TabLifecycleSnapshot`
- Windows memory probe: `memory_30_tabs`

---

## 1. 탭 수명주기

- 생성: model + WebView2 + environment 공유
- 전환: visibility/active model
- 닫기: `CoreWebView2.Stop` → `Close` → visual tree 제거 → model 제거
- 마지막 탭: replacement 탭 생성
- drag reorder: `DragItemsCompleted`에서 model order 반영
- 닫은 탭: `ClosedTabHistory` 최대 20개, Ctrl+Shift+T 복원

## 2. 진단 카운터

`TabManager.Diagnostics()`:

- `Created`
- `Closed`
- `Live`
- `ActiveIndex`

MainWindow는 탭 생성/닫기/clear 시 Debug 로그를 남깁니다.

## 3. 메모리 probe

Windows `memory_30_tabs`:

- before
- 30 tabs create
- suspend
- close
- `ProcessProbe`로 browser/renderer/gpu/utility working set 수집
- 평균 renderer per tab과 reclaimed 여부 기록

주의: `msedgewebview2.exe` 전체 프로세스를 세므로 다른 WebView2 앱이 섞일 수 있고, per-tab renderer 매핑은 근사값입니다.

## 4. 이번 환경 결과

- Linux C# tab lifecycle 테스트: PASS
- Windows 30탭 메모리: **미측정**
- Windows 5/10/20탭 메모리: **미측정**
- 탭 종료 후 WebView2 process 감소 여부: **미측정**
- 실제 Windows 11에서 측정해야 합니다.
