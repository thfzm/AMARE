# Windows 성능 측정

- 원칙: 실제 측정하지 않은 수치를 목표치처럼 쓰지 않습니다.
- Windows 하네스: `tools/windows-verify` memory probe
- Linux 서비스 benchmark: `service-benchmark`

---

## 1. 측정 항목/도구

| 항목 | 도구/방법 | 상태 |
|---|---|---|
| 앱 초기 실행 | Windows UI instrumentation 필요 | 미측정 |
| WebView2 환경/탭 생성/로컬 로딩/5·10·20탭 메모리 | `windows_browser_performance` probe | 구현, 미실행 |
| 첫 WebView2 초기화 | probe/stopwatch 추가 필요 | 미측정 |
| 새 탭 생성 | UI + WebView2 timing 필요 | 미측정 |
| 로컬 페이지 로딩 | LocalHttpServer + WebView2 | probe 구현, 미측정 |
| 외부 페이지 로딩 | HTTPS probe | 미측정 |
| 탭 전환 | UI instrumentation 필요 | 미측정 |
| 프로필 전환 | UI instrumentation 필요 | 미측정 |
| 5/10/20탭 메모리 | ProcessProbe + `windows_browser_performance` | 구현, 미실행 |
| 탭 닫기 후 메모리 | ProcessProbe | 미측정 |
| 앱 종료 시간 | UI instrumentation 필요 | 미측정 |

## 2. Windows 11 실측 절차

목표: i5-7500 / 16GB / GTX 1050 2GB

1. Release build, WebView2 Runtime 버전 기록
2. 앱 시작부터 첫 탭 ready까지 5회 측정
3. 로컬 `LocalHttpServer` 페이지와 외부 HTTPS 페이지 각각 10회
4. 5/10/20/30 탭 생성 후 10초 안정화 뒤 `ProcessProbe`
5. 탭 전환 100회, p50/p95 기록
6. 프로필 A↔B 전환 20회
7. 모든 탭 닫은 뒤 10초 후 메모리 재측정
8. JSON/로그와 환경 정보를 함께 보관

## 3. 해석 주의

- Windows Server Runner 수치는 실기기 대표값이 아닙니다.
- GPU/WebView2 버전/탭 콘텐츠에 따라 변동합니다.
- ProcessProbe는 사용자 계정의 모든 `msedgewebview2.exe`를 포함할 수 있습니다.
- 현재 Windows 실측값은 없습니다.
