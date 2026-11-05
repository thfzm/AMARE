# AMARE Windows UI 설계

- 파일: `apps/windows/AMARE.Windows/MainWindow.xaml`
- 테마: WinUI 3 theme resources, `theme` setting (`system/light/dark`)
- 원칙: 익숙한 브라우저, 과도한 투명/애니메이션 금지, 콘텐츠 집중

---

## 1. 레이아웃

```text
┌──────────────────────────────────────────────┐
│ horizontal tabs                     [+ new]  │
├──────────────────────────────────────────────┤
│ ← → ↻ │ address bar │ engine │ ... │ profile │
├──────────────────────────────────────────────┤
│                                              │
│              WebView2 content                │
│                                              │
├──────────────────────────────────────────────┤
│ status / loading message                     │
└──────────────────────────────────────────────┘
```

- 탭은 `ListView` horizontal + scroll, rounded item, active selection highlight.
- 새 탭/닫기 버튼.
- toolbar: back/forward/reload-stop, AutoSuggest address, search engine, bookmarks, downloads, profile, AI, menu, loading ring.
- Find bar는 필요할 때만 펼쳐지고, Reader는 콘텐츠 전체를 덮는 읽기 화면으로 표시됩니다.
- AI panel은 오른쪽 380px 보조 패널로 열리며, 별도 WebView2 UserDataFolder를 사용합니다.
- 창 크기/DPI는 WinUI Grid와 WebView2가 자동 대응합니다.

## 2. 동작 표시 원칙

- 실제 구현: back, forward, reload/stop, new tab/close tab, address navigation/로컬 제안, search engine, bookmarks, history, persistent downloads, settings, Reader, Find, AI 웹 패널, profile.
- 제한: AI OAuth popup/로그인 서비스별 호환성과 Windows 11 실기기 동작은 UNVERIFIED입니다.
- 주소창은 URL이면 navigation, 일반 텍스트면 settings의 search engine으로 검색합니다.
- 로딩/뒤로/앞으로 상태는 WebView2 실제 이벤트에서 갱신합니다.

## 3. 새 탭 페이지

- `NewTabPageBuilder`가 로컬 HTML 생성.
- AMARE 로고 텍스트, 중앙 검색창, 검색엔진 이름, frequently visited (실제 Rust history 상위 항목), 빈 상태 안내.
- Bookmarks/History/AI는 0.15.0에서 toolbar/menu로 접근할 수 있습니다.
- 외부 서버/위젯/날씨/시계/배경 사용자화는 없습니다.
- 검색 form은 `window.chrome.webview.postMessage` + nonce로 C#에 전달됩니다.
- HTML title/URL은 escape해 script injection을 방지합니다.

## 4. 키보드 단축키

| 단축키 | 동작 | 처리 위치 |
|---|---|---|
| Ctrl+L | 주소창 focus | WinUI KeyboardAccelerator |
| Ctrl+T | 새 탭 | WinUI |
| Ctrl+W | 활성 탭 닫기 | WinUI |
| Ctrl+Shift+T | 닫은 탭 복원 (최근 20개) | WinUI + ClosedTabHistory |
| Ctrl+R | 새로고침 | WinUI |
| Ctrl+F | Find in Page | WinUI |
| Alt+Left / Alt+Right | 뒤로 / 앞으로 | WinUI |
| Enter | 주소창 이동 | AutoSuggestBox QuerySubmitted |

브라우저 단축키는 WinUI `KeyboardAccelerator`로 처리해 WebView2 페이지 단축키와 충돌할 때 브라우저 명령이 우선하도록 했습니다. 실제 IME/페이지 단축키 충돌은 Windows 11 실기기에서 미검증입니다.

## 5. 테마/접근성

- `theme=system`이면 WinUI `ElementTheme.Default`로 OS 테마를 따릅니다.
- light/dark는 설정값에 따라 `RootGrid.RequestedTheme`을 변경합니다.
- 버튼은 최소 34px 높이, 주소창은 34px, 툴바 아이콘은 Segoe Fluent Icons 사용.
- 고대비/스크린리더 전체 검증은 Windows 실기기에서 미검증입니다.
