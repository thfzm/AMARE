# AMARE Vault Threat Model — Phase 1.2

- 상태: **보안 감사/설계 (구현 전)**
- 작성일: 2026-11-23
- 대상: WinUI 3 + WebView2 + C# + Rust `amare-security`/`amare-platform`/`amare-store`
- 기준 HEAD: 제품/테스트 `bcd476d`, 문서 `63aa2b1`
- 이 문서는 **위협 모델**이며, 구현/검증 완료를 주장하지 않는다.

---

## 1. 범위

포함:

- 로컬 비밀번호 Vault (자격 증명 저장/조회/삭제)
- Vault 잠금/해제, 자동 잠금
- WebView2 로그인 폼 자동완성
- 저장 제안(로그인 submit 후 사용자 승인)
- Vault 데이터 백업/복구
- Windows DPAPI 기반 키 보호
- 암호화/무결성/삭제/로그/메모리 처리

제외:

- 서버 동기화, 클라우드 Vault
- 브라우저 확장 프로그램
- Android/GPUI 구현 변경
- Windows Hello를 암호학적 키 보호로 사용하는 설계 확정
- DRM/OS 악성코드/관리자 권한 공격의 완전 방어

---

## 2. 자산

| ID | 자산 | 민감도 |
|---|---|---|
| A1 | 자격 증명 평문 (origin, username, password, title) | 최상 |
| A2 | Vault 데이터 키 (32 bytes) | 최상 |
| A3 | 암호화된 Vault blob | 높음 |
| A4 | DPAPI로 보호된 키 파일/메타데이터 | 높음 |
| A5 | 프로세스 메모리의 평문/복호화 결과 | 최상 |
| A6 | WebView2 DOM에 입력된 값, 클립보드 | 최상 |
| A7 | 백업/복구 패키지 | 최상 |
| A8 | 로그, status JSON, 진단 덤프 | 중~높음 |
| A9 | WebView2 프로필의 내장 password/autofill 데이터 | 높음 |

---

## 3. 공격자와 신뢰 경계

### 3.1 공격자

1. 같은 Windows 사용자로 실행되는 악성 프로세스/스크립트
2. 다른 Windows 사용자 계정
3. 물리적/오프라인 디스크 접근자
4. 악성 웹사이트/피싱 사이트/XSS에 감염된 정상 사이트
5. 악성 iframe/광고/서드파티 스크립트
6. 백업 파일을 획득한 공격자
7. Windows 관리자/시스템 권한 공격자
8. 메모리 덤프/크래시 덤프 접근자

### 3.2 신뢰 경계

```text
[Web content / renderer]  ← untrusted
        │  WebMessage / JS injection
[WinUI host process]      ← trusted app, same-user attack surface
        │  FFI JSON
[Rust vault service]      ← key/plaintext minimized
        │  DPAPI
[Windows user profile / DPAPI] ← OS boundary
        │
[Disk / backup / clipboard] ← at-rest / exfiltration boundary
```

중요: **DPAPI는 Windows 사용자 계정 경계에 의존한다.** 같은 사용자로 실행되는
악성 프로세스, 관리자 권한, 로그인된 세션의 메모리 덤프를 막지 못한다.

---

## 4. 위협 평가

분류:

- **보호 가능**: 설계/구현으로 현실적인 방어 가능
- **부분 보호**: 특정 조건에서만 방어, 우회/잔여 위험 존재
- **보호 불가능**: 현재 범위에서 완전 방어 불가
- **추가 설계 필요**: 정책/UX/키 복구 설계가 먼저 필요

| # | 위협 | 분류 | 근거 / 잔여 위험 |
|---:|---|---|---|
| 1 | 로컬 DB 파일 탈취 | **부분 보호** | Vault는 AEAD 암호문만 DB에 저장. 키는 별도 DPAPI 파일. 같은 사용자/관리자는 DPAPI를 풀 수 있음. |
| 2 | Windows 사용자 계정 외부에서 파일 접근 | **보호 가능(조건부)** | DPAPI user scope + 사용자 프로필 ACL. 관리자/시스템, 오프라인 크랙, 프로필 백업 탈취는 예외. |
| 3 | 다른 프로세스의 Vault 접근 | **부분 보호 / 보호 불가능(동일 사용자)** | 같은 사용자 프로세스는 파일/DPAPI/메모리에 접근 가능. Windows ACL은 동일 사용자 격리가 아님. |
| 4 | 프로세스 메모리 평문 노출 | **부분 보호** | Rust에서 byte buffer zeroize, 최소 복사, 잠금 시 폐기. C# string/JS DOM/WebView2 renderer 복사본은 완전 제거 불가. |
| 5 | Vault 파일 변조 | **보호 가능(AEAD) / 부분(rollback)** | 1바이트 변조/태그 변조는 복호화 실패. 이전 정상 blob으로 되돌리는 rollback은 별도 monotonic anchor 없이는 완전 방어 불가. |
| 6 | 암호화 데이터 손상 | **보호 가능(탐지) / 부분(가용성)** | AEAD 실패 시 원본 보존, 백업 복구. 랜섬웨어/디스크 손상은 백업 정책에 의존. |
| 7 | 비밀번호 입력/자동완성 과정 노출 | **부분 보호** | 사용자 gesture, top-level only, 로그 금지, 클립보드 정리. DOM/IME/클립보드 기록/화면 캡처는 잔여. |
| 8 | 악성 웹사이트의 자격 증명 탈취 | **부분 보호** | exact origin, no auto-submit, user approval. 정상 origin의 XSS/악성 스크립트는 방어 불가. |
| 9 | 피싱/유사 도메인 | **부분 보호** | exact origin + IDN punycode 정규화로 오입력 방지. 사용자가 직접 입력/승인하는 피싱, homograph 표시는 잔여. |
| 10 | 브라우저 프로세스 비정상 종료 | **보호 가능(조건부)** | SQLite transaction/원자적 저장, 마지막 정상 commit 보존. 저장 중 전원 손실/FAT 손상은 파일시스템 의존. |
| 11 | Windows 프로필/DPAPI 키 접근 불가 | **보호 불가능 / 추가 설계 필요** | DPAPI user scope는 계정/프로필 변경 시 복호화 실패. Master Password/암호화 export 없이는 복구 불가. |
| 12 | Vault 암호 분실/복구 불가 | **추가 설계 필요** | 기본 DPAPI-only는 비밀번호 분실 개념이 없지만 계정 손실 시 복구 불가. Master Password/복구 키 정책 필요. |

추가 위협:

| # | 위협 | 분류 | 비고 |
|---:|---|---|---|
| 13 | WebView2 내장 password autofill과 충돌 | **추가 설계 필요** | `IsPasswordAutosaveEnabled` 기본 false이나 기존 저장 데이터는 자동 채움 가능. `IsGeneralAutofillEnabled` 기본 true. 명시적 비활성/삭제 정책 필요. |
| 14 | WebView2 프로필 데이터에서 자격 증명 잔존 | **부분 보호** | AMARE Vault와 별개로 WebView2 프로필에 데이터가 남을 수 있음. |
| 15 | 백업에 암호문만 포함되고 키는 빠짐 | **추가 설계 필요** | 현재 `BackupData.secrets=true`지만 키 파일은 백업 대상이 아님. 복구 불가 위험. |
| 16 | 삭제 후 SQLite/WAL/백업 잔존 | **부분 보호** | 논리 삭제는 보장, 포렌식 완전 삭제는 SSD wear-leveling/백업 때문에 보장 불가. |
| 17 | 크래시 덤프/진단 로그의 평문 | **부분 보호** | 덤프 수집 정책/리디악션 필요. WER dump에 메모리 평문 포함 가능. |
| 18 | 다른 앱 인스턴스의 동시 저장 | **부분 보호** | SQLite transaction/CAS 필요. GPUI는 프로세스 내 lock만 있음. |

---

## 5. 보안 목표

1. DB/백업 파일만으로 Vault 평문을 복호화할 수 없어야 한다.
2. Vault 키는 DB와 분리하고 DPAPI로 보호한다.
3. 모든 저장은 AEAD 인증/무결성 검증을 통과해야 한다.
4. 잠금 상태에서는 API/UI/status에서 자격 증명 평문이 노출되지 않아야 한다.
5. 자동완성은 exact origin + top-level + 사용자 승인 + no auto-submit이어야 한다.
6. 저장/삭제/백업 실패 시 기존 정상 Vault를 보존해야 한다.
7. 로그/status/에러 메시지에 비밀번호/키/평문이 없어야 한다.
8. 계정/프로필 손실 시 복구 정책을 사용자에게 명확히 알려야 한다.

---

## 6. 비목표 / 보장하지 않는 것

- 같은 사용자 악성코드로부터의 완전 보호
- 관리자/커널/메모리 덤프 공격 방어
- Windows 계정/DPAPI 프로필 손실 후 무조건 복구
- 정상 사이트의 XSS/악성 스크립트가 DOM 값을 읽는 것 방지
- 클립보드 기록/클라우드 동기화/화면 캡처의 완전 차단
- SSD/백업에서의 물리적 완전 삭제
- Windows Hello만으로 암호학적 Vault 키 보호
- GPUI Vault와 WinUI Vault의 자동 데이터 공유

---

## 7. DPAPI 관련 명시

- 사용 범위: **user scope** (`CryptProtectData` 기본, `CRYPTPROTECT_LOCAL_MACHINE` 미사용).
- `CRYPTPROTECT_UI_FORBIDDEN`으로 UI 프롬프트 없이 실패한다.
- DPAPI는 Windows 로그인 자격 증명/사용자 프로필의 보호에 의존한다.
- Windows 계정 변경, 프로필 재생성, 도메인/로밍 프로필 정책, 관리자 비밀번호 재설정에 따라
  복호화가 실패할 수 있다.
- 따라서 DPAPI-only Vault는 **같은 Windows 계정/프로필에서는 편리하지만,
  계정 외부 복구/이전은 별도 Master Password/암호화 export가 필요**하다.

---

## 8. 잔여 위험과 승인 필요 항목

1. Master Password를 v1에 포함할지, DPAPI-only + export로 시작할지
2. Vault를 프로필별로 둘지, 모든 프로필 공유로 둘지
3. WebView2 내장 password/autofill 데이터를 언제/어떻게 정리할지
4. 자동 잠금 기본 시간 (예: 5분)과 정책
5. 백업에 Vault를 포함할지, 별도 export만 허용할지
6. Windows Hello를 UX 게이트로만 쓸지, 키 보호에 쓸지
7. 삭제 후 키 회전/`secure_delete`/VACUUM 정책
8. 실제 Windows 11 실기기 검증 범위

이 항목들은 `docs/VAULT_SECURITY_DESIGN.md`의 설계 결정과 함께 승인받아야 한다.
