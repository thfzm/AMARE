# AMARE Vault Test Plan — Phase 1.2

- 상태: **테스트 계획 (구현 전)**
- 작성일: 2026-11-23
- 대상: WinUI 3 + WebView2 Vault, Rust `amare-security`/`amare-store`/FFI
- 원칙: 소스 존재 ≠ 검증. GPUI 결과를 WinUI 결과로 합산하지 않는다.

---

## 1. 테스트 계층

| 계층 | 목적 | 실행 시점 | 예상 시간 |
|---|---|---|---|
| A. Unit | crypto/key/format/origin/merge | 모든 변경 | 1–5분 |
| B. Feature integration | Vault save/load/lock/autofill/FFI | 기능 완료 시 | 20–40분 |
| C. Full regression | WinUI 48+ smoke, Windows, interop, real Windows 11 | 패키지/마일스톤 전 | 2–4시간 |

A는 mock/in-memory, B는 실제 SQLite/DPAPI/WebView2, C는 실제 Windows 사용자 세션이다.
CI Windows runner와 Windows 11 실기기 결과를 별도로 기록한다.

---

## 2. 테스트 픽스처

- `vault-v1-fixture.bin`: format 1, entry 2개, tombstone 1개
- `vault-v0-legacy.bin`: 알 수 없는 format
- `vault-corrupt-*.bin`: ciphertext/tag/nonce 1-byte 변조
- `vault-wrong-key.bin`: 다른 key로 암호화
- `login.html`: top-level login form
- `login-cross-frame.html`: cross-origin iframe 안 login form
- `login-cross-action.html`: form action이 다른 origin
- `login-lookalike.html`: `example.com.evil.test` / punycode 유사 도메인
- `login-http.html`: HTTP 로그인
- `login-hidden.html`: hidden/readonly password field
- `vault-backup-password.bin`: Master Password export fixture

픽스처에는 실제 사용자 비밀번호를 넣지 않는다. 테스트 전용 값만 사용한다.

---

## 3. 필수 실패 테스트

각 테스트는 **입력**, **기대 결과**, **검증 방법**, **분류**를 가진다.

### T01 잘못된 키로 복호화

- 입력: 정상 Vault blob + 다른 32-byte key
- 기대: `VAULT_AUTH`/generic error, plaintext 없음, 원본 blob 변경 없음
- 검증: API 오류 코드, DB/blob SHA-256 불변, 로그에 key/password 없음
- 분류: `PRODUCT_DEFECT` if plaintext/overwrite occurs

### T02 암호문 1바이트 변조

- 입력: nonce 이후 ciphertext 중간 1 byte XOR
- 기대: AEAD 인증 실패, generic error, 원본 파일 보존
- 검증: 오류 코드, blob hash 불변, 재시도 시 여전히 실패
- 분류: `PRODUCT_DEFECT` if accepts

### T03 인증 태그 변조

- 입력: 마지막 16-byte tag 중 1 byte XOR
- 기대: 복호화 실패, 평문 없음
- 검증: T02와 동일
- 비고: 기존 `amare-security` tag tamper test가 있으나 Vault 통합 경로도 확인

### T04 Nonce 재사용 방지

- 입력: 동일 plaintext를 2회 저장
- 기대: 두 blob의 nonce가 다르고, nonce 재사용 없음
- 검증: `nonce(24)` 비교, 10,000회 저장 샘플에서 중복 0
- 추가: nonce 1 byte 변조 시 복호화 실패
- 분류: `PRODUCT_DEFECT` if deterministic/reused

### T05 잘못된 Vault 데이터 버전

- 입력: `format=0`, `format=2`, `format=999`, 누락
- 기대: `VAULT_UNSUPPORTED`, 원본 보존, 자동 재작성/강제 format 변환 금지
- 검증: 오류 코드, blob hash 불변
- 비고: GPUI `normalise()`가 format을 2로 강제하는 결함을 반복하지 않는다.

### T06 잘못된 DB 스키마

- 입력: `secrets` 테이블 누락/컬럼 불일치, future schema DB
- 기대: 명확한 오류, 자동 migration/데이터 변경 없음, 원본 보존
- 검증: `PRAGMA integrity_check`, row count/hash 비교
- 비고: Vault v1은 DB schema 변경이 없으므로 이 테스트는 “기존 DB 보호” 목적

### T07 저장 도중 프로세스 종료

- 입력: 큰 Vault 저장 중 process kill
- 기대: 이전 정상 blob 또는 새 blob 중 하나만 존재, partial JSON/암호문 없음
- 검증: 재시작 후 복호화, `integrity_check`, temp file 정리
- 분류: `PRODUCT_DEFECT` if half-written blob accepted

### T08 동시 저장

- 입력: 두 창/두 연결이 동시에 서로 다른 entry 수정
- 기대: lost update 없음, CAS/merge, 충돌 시 명시적 conflict
- 검증: 최종 entry revision/값, 두 변경이 모두 보존되거나 conflict 반환
- 비고: GPUI는 프로세스 내 lock만 있고 cross-process 검증이 약하다.

### T09 DPAPI 복호화 실패

- 입력: key file을 다른 DPAPI context/사용자로 보호, key file 손상
- 기대: `VAULT_AUTH` generic error, Vault blob 보존, 새 key 자동 생성 금지
- 검증: 오류 코드, blob/key hash 불변
- 분류: `PRODUCT_DEFECT` if key regenerated/overwrites

### T10 Windows 계정이 달라진 경우

- 입력: 사용자 A에서 만든 key file + Vault를 사용자 B에서 열기
- 기대: 복호화 실패, 데이터 손상 없음, Master Password export가 있으면 복구 가능
- 검증: 실제 Windows 계정/프로필 변경, DPAPI 오류, 로그에 secret 없음
- 분류: **ENVIRONMENT/BLOCKED** if 별도 계정 실기기 없음

### T11 Vault 잠금 후 평문 접근 차단

- 입력: unlock → list → lock → list/get_for_fill/status
- 기대: entry 0, password 없음, API `VAULT_LOCKED`, UI 목록 비움
- 검증: API/status/UI, 메모리에서 가능한 범위 zeroize, 로그 확인
- 추가: 여러 창 global lock 전파, pending login 취소

### T12 잘못된 origin 자동완성 차단

- 입력: `https://example.com` entry로 `https://example.com.evil.test` /
  `http://example.com` / `https://www.example.com` / 다른 port에서 Fill
- 기대: 모두 차단, exact match만 허용
- 검증: DOM 값 미변경, 오류 코드, audit log에 origin mismatch
- 분류: `PRODUCT_DEFECT` if fills

### T13 iframe 자동완성 차단

- 입력: cross-origin iframe 안 login form, top-level은 다른 origin
- 기대: top-level에만 주입, iframe에는 fill/capture 없음
- 검증: frame DOM 값, `window.top===window` guard, `args.Source` 검증
- 추가: same-origin iframe도 기본적으로 fill하지 않음 (top-level 정책)

### T14 유사 도메인/피싱 차단

- 입력: IDN/punycode, `example.com.evil.test`, Unicode homograph
- 기대: exact canonical origin 불일치로 차단, 표시 시 punycode 병기
- 검증: fill 미실행, 사용자에게 전체 origin 표시
- 잔여: 사용자가 직접 입력/승인하는 피싱은 완전 방어 불가

### T15 비밀번호 삭제 후 데이터 잔존

- 입력: entry 삭제 → 저장 → 재시작 → DB/WAL/backup 검사
- 기대: 논리 삭제, tombstone, API에서 조회 불가, 평문 없음
- 검증: DB row/백업/로그 scan, `secure_delete`/VACUUM 정책 확인
- 한계: SSD wear-leveling/기존 백업의 물리적 완전 삭제는 보장하지 않음
- 추가: 전체 삭제 시 key rotation

### T16 로그/status 평문 노출

- 입력: 정상/실패/자동완성/충돌/손상 시나리오 전체
- 기대: password/key/평문/민감 origin+username 조합 없음
- 검증: logs, status JSON, WER dump, UI error text, clipboard history
- 분류: `PRODUCT_DEFECT` if secret appears

---

## 4. 추가 보안 테스트

| ID | 테스트 | 기대 |
|---|---|---|
| T17 | AAD/profile 변조 | 다른 profile id로 blob 복사 시 복호화 실패 |
| T18 | key file missing + vault exists | 오류, Vault 보존, 새 key 생성 금지 |
| T19 | vault missing + key exists | 빈 Vault, 기존 key 유지/정책 확인 |
| T20 | Master Password export/import | 잘못된 password 거부, 정상 import 후 entry 보존 |
| T21 | backup에 key가 없는 경우 | 명확한 “복구 불가” 경고, 자동 성공 보고 금지 |
| T22 | WebView2 내장 autofill | `IsPasswordAutosaveEnabled=false`, `IsGeneralAutofillEnabled=false` 확인 |
| T23 | 기존 WebView2 autofill 데이터 | 사용자 동의 후 `ClearBrowsingDataAsync` 확인 |
| T24 | HTTP vs HTTPS | HTTPS만 자동완성, HTTP는 차단/경고 |
| T25 | form action cross-origin | fill 차단 또는 명시적 경고 |
| T26 | hidden/readonly/disabled field | fill 금지 |
| T27 | username field 오선택 | 검색/결제 field에 입력하지 않음 |
| T28 | 클립보드 자동 정리 | 30초 후 동일 값이면 clear, 변경된 값은 보존 |
| T29 | idle auto-lock | 설정 시간 후 lock, 평문/목록 제거 |
| T30 | Windows session lock | session lock 시 Vault lock (API 확인) |
| T31 | 다중 창 lock 전파 | 다른 창도 즉시 lock |
| T32 | 다중 창 동시 편집 | conflict/revision 보존, lost update 없음 |
| T33 | crash dump 정책 | WER/dump에 secret이 남지 않도록 설정/문서 확인 |
| T34 | 로그 redaction | exception message에 secret이 섞여도 출력 금지 |
| T35 | ABI version mismatch | C#/Rust ABI 불일치 시 명확한 오류, 데이터 변경 없음 |

---

## 5. 자동완성 시나리오 상세

### 5.1 정상

1. Vault unlock
2. `https://example.com/login` 접속
3. Vault 패널에 exact origin entry 1개 표시
4. 사용자가 Fill 클릭
5. username/password 입력, **submit 없음**
6. DOM 값 확인, password는 status/log에 없음

### 5.2 실패

| 시나리오 | 기대 |
|---|---|
| HTTP page | fill 차단/경고 |
| subdomain | fill 차단 |
| lookalike | fill 차단 |
| cross-origin iframe | fill 차단 |
| hidden/readonly password | fill 차단 |
| lock 상태 | Fill 버튼 비활성/`VAULT_LOCKED` |
| navigation 중 | navigation id 불일치로 fill 취소 |
| form action cross-origin | fill 차단/경고 |
| password field 0개 | fill 실패, 아무 것도 입력하지 않음 |
| 복수 password field | 자동 선택 금지, 사용자 선택 또는 실패 |

### 5.3 저장 제안

1. top-level login form submit
2. source/action exact origin
3. native “저장할까요?” 표시
4. 저장 선택 → upsert + save
5. 거부 → pending 삭제
6. 다른 창에서 수정/삭제 → conflict, 자동 덮어쓰기 금지

---

## 6. 증거 형식

각 테스트는 다음을 기록한다.

```text
test_id:
head:
environment:
fixture:
first_failure:
retry_result:
product_code_reached:
webview_event_received:
vault_error_code:
plaintext_seen_in_logs: yes/no
data_changed_on_failure: yes/no
classification: PRODUCT_DEFECT | TEST_AUTOMATION_BUG | ENVIRONMENT_LIMITATION | UNRESOLVED
```

- 최초 실패와 재시도 결과를 모두 보존한다.
- 실패를 숨기기 위해 assertion을 완화하지 않는다.
- CI Windows와 실제 Windows 11 실기기 결과를 합산하지 않는다.
- GPUI 결과는 참고만 하고 WinUI 결과로 쓰지 않는다.

---

## 7. 실행 순서

1. A. Unit: crypto/format/origin/merge/FFI contract
2. B. Feature: Vault CRUD/lock/DPAPI/backup
3. B. Feature: WebView2 autofill/capture/origin/frame
4. C. Full: WinUI UI smoke + Windows + interop
5. C. Real Windows 11: DPAPI account change, session lock, IME, DPI
6. Security review: logs/status/dump/clipboard/backup scan

---

## 8. 통과 기준

- T01–T16 전부 PASS 또는 명시적 `ENVIRONMENT_LIMITATION`/`BLOCKED`
- T17–T35 중 v1 범위 항목 PASS
- 평문/키가 log/status/DB/backup에 없음
- 잘못된 origin/iframe/lookalike에서 fill 0회
- DPAPI/key 손실 시 원본 보존
- Windows 11 실기기에서 계정 변경/세션 잠금/자동 잠금 확인
- 기존 WinUI 49/49, 143/0, interop 76/76 유지

하나라도 미충족이면 Vault 구현 PASS로 판정하지 않는다.
