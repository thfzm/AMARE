# AMARE 0.55.0 — GPUI Password Vault & Login Autofill

0.55.0은 Windows DPAPI로 보호되는 로컬 비밀번호 보관함과 exact-origin 로그인 자동 완성을 추가했다.

## Highlights

- Windows DPAPI user-scope key + XChaCha20-Poly1305 암호화 vault file. SQLite에는 비밀번호를 저장하지 않는다.
- 계정 추가·수정·삭제 UI, 사이트/사용자 검색, 다중 계정, 비밀번호 기본 마스킹/표시 전환.
- 보안 저장소 잠금/잠금 해제, 잠금 시 메모리상 계정 목록 제거, 앱 재시작 후 DPAPI 복원.
- 현재 top-level origin과 exact match하는 계정만 자동 완성 제안; HTTP/HTTPS·서브도메인·포트를 구분.
- 자동 완성은 사용자가 선택한 뒤에만 입력하며 자동 제출하지 않는다.
- 실제 로그인 submit event를 감지해 저장/업데이트 제안; 사용자가 승인해야 저장한다.
- iframe은 hook에서 제외하고, password field가 없는 일반 field에는 채우지 않는다.
- WebView2 built-in password autosave/general autofill을 비활성화한다.
- 사이트 권한 설정 화면은 현재 WebView2 기본 프롬프트 상태를 read-only로 표시하고, 제어할 수 없는 기능은 활성화하지 않는다.
- 실제 0.55 Windows screenshots와 password/origin/DPAPI 테스트를 CI artifact로 업로드.

## Unverified / blocked

- 삭제 UI의 작은 target 실제 mouse 자동 클릭(delete 동작 자체는 구현, 자동화는 UNVERIFIED).
- iframe/교차 출처 submit 실제 공격 시나리오 자동화.
- vault 접근 실패(DPAPI 손상/권한 거부) UI와 multi-window 동시 vault 쓰기.
- 쿠키/사이트 데이터/사이트별 권한 초기화 제어 UI; WebView2 ClearBrowsingData API는 미연결.
- clipboard copy/auto-clear(현재 copy 기능 없음), download prompt/pause, DPI/multi-monitor, Windows 11/local CA/signing.
