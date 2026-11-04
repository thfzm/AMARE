# Android 1.0.0 출시 기준 및 체크리스트

- 기준: Android 0.11.0
- 현재 판정: **출시 불가 / UNVERIFIED 항목 존재**

## 1. 필수 기능

- [x] HTTPS 기본 브라우징
- [x] 탭 생성/전환/종료
- [x] SessionState 탭 복구
- [x] 최근 닫은 탭 복구(메모리)
- [x] 비밀번호 저장/자동완성 코드
- [x] 웹 알림 코드
- [x] 다운로드/열기/위험 파일 경고
- [x] 데이터 관리 UI
- [x] HTTPS-only/외부 URL 검증
- [x] Keystore AES-GCM 코드
- [x] DB migration 코드
- [x] Debug/Release/AAB 빌드
- [x] JVM 33 tests / Lint 0 errors
- [x] Instrumentation 12 tests compile
- [x] Linux/Windows/Android CI

## 2. 출시 전 실제 실행 필수

- [ ] Keystore AES-GCM 실제 round-trip/변조/키 무효화
- [ ] DB v1/v2 → v3 migration 실제 실행/rollback
- [ ] GeckoView 렌더링/입력/SSL 오류
- [ ] HTTPS-only/Mixed Content 실제 사이트
- [ ] window.open/target=_blank/팝업 차단
- [ ] 비밀번호 로그인 감지/저장/자동완성/cross-origin
- [ ] 웹 알림 권한/표시/클릭/프로세스 재시작/채널 비활성
- [ ] 데이터 삭제 각 범위/부분 실패
- [ ] 기본 브라우저 role/외부 링크
- [ ] 다운로드 완료/실패/중복 파일명/실제 파일 열기
- [ ] 세션 복구/백그라운드/강제 종료
- [ ] 실제 기기 성능 측정
- [ ] Release 서명 키로 signed APK/AAB 검증
- [ ] 설치/업데이트/서명 키 호환성

## 3. 출시 차단 조건

- 실제 Android 기기 통합 테스트 미완료
- Keystore/DB migration 실제 실행 미완료
- Release 서명 미완료
- 치명적 보안 이슈 존재
- 알림/자동완성/팝업/데이터 삭제의 실기기 동작 미확인

## 4. 1.0.0 제외 범위

- WebExtension
- AI 패널
- Research/Library Android 연동
- Android 시스템 전체 AutofillService
- Play Store 배포/심사

위 제외 기능은 1.0.0 필수 조건으로 간주하지 않습니다.
