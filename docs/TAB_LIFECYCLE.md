# 탭 수명주기

- UI: `MainWindow.xaml`의 horizontal `ListView` + WebView2 host Grid
- 모델: `apps/windows/AMARE.Windows.Core/TabManager.cs`

---

## 1. 모델

`BrowserTabModel`:

- `Id` (Guid)
- `Title`, `Url`
- `IsLoading`, `CanGoBack`, `CanGoForward`
- `IsPinned`, `IsNewTabPage`

`TabManager`:

- `AddTab` — 새 모델 생성, 활성 탭 지정
- `Activate` — 탭 선택
- `Close` — 탭 제거, 마지막 탭이면 새 탭 모델 생성
- `Move` — drag reorder 순서 반영
- `SetPinned` — 고정 상태 구조
- `Snapshot` — URL/title/pinned + active index
- `Diagnostics` — created/closed/live/active counters
- `ClosedTabHistory` — 최근 닫은 탭 최대 20개, Ctrl+Shift+T 복원

`TabManager`는 WebView2를 모릅니다. 순수 C# 테스트로 수명주기 규칙을 검증합니다.

---

## 2. 생성/전환

1. `TabManager.AddTab`로 모델 생성
2. `WebView2` control 생성 → `WebViewHost`에 추가
3. Loaded 대기 → `EnsureCoreWebView2Async(environment)`
4. 이벤트 연결
5. 새 탭이면 local new-tab HTML, 아니면 URL navigate
6. UI 선택과 model active 동기화

## 3. 닫기

1. `CoreWebView2.Stop` / `Close`
2. visual tree에서 제거
3. `TabManager.Close`
4. 마지막 탭이면 새 탭 생성
5. 활성 탭 재선택, toolbar/address 갱신
6. 세션 저장 예약

## 4. 순서 변경

- WinUI `ListView`의 `CanReorderItems`, `CanDragItems`, `AllowDrop`을 사용합니다.
- `DragItemsCompleted`에서 visual 순서를 `TabManager.Move`로 모델에 반영합니다.
- 세션 저장은 drag 완료 후 예약됩니다.

## 5. 제한/미검증

- 고정 탭은 모델/세션 직렬화 구조만 구현됐고, UI에서 pin toggle 메뉴는 아직 제공하지 않습니다.
- 탭 검색, 탭 그룹, 탭 분리/이동, 닫은 탭 UI 목록은 이후 Phase입니다.
- Windows 실기기에서 drag reorder/DPI/IME 동작은 미검증입니다.
