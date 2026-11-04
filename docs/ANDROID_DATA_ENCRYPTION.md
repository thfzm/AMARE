# Android 데이터 암호화 및 마이그레이션

- 기준: Android 0.4.0
- 관련 문서: [Android Keystore](ANDROID_KEYSTORE.md), [다음 단계 설계](ANDROID_NEXT_STEPS.md)

## 1. 데이터 유형별 보호 방식

| 데이터 | 저장 필드 | 보호 방식 | 이유 |
|---|---|---|---|
| 북마크 | URL, 제목 | AES-256-GCM encrypted payload | 검색 대상이며 프라이버시 민감 |
| 방문 기록 | URL, 제목 | AES-256-GCM encrypted payload | 검색 대상이며 프라이버시 민감 |
| 다운로드 메타데이터 | 파일명, 원본 URL, MIME, 로컬 URI, 오류 | AES-256-GCM encrypted payload | URL/파일 경로가 민감 |
| 사이트 권한 | origin, decision | AES-256-GCM encrypted payload + SHA-256 origin_key | origin 노출 최소화, 조회 인덱스 유지 |
| 세션 snapshot | URL, 제목, SessionState | AES-256-GCM encrypted payload | 폼 데이터/방문 이력 포함 |
| 정렬/상태 필드 | created_at, visited_at, size, status, position | 평문 | 민감 콘텐츠가 아니며 인덱스/정렬에 필요 |

- AES-GCM AAD는 데이터 종류별로 분리했습니다.
- 암호화 실패 시 평문 fallback은 없습니다.
- 인증 태그 불일치/변조는 레코드 단위로 거부합니다.

## 2. DB 스키마

DB version 3에서 추가:

```text
bookmarks_enc(id, payload BLOB, created_at)
history_enc(id, payload BLOB, visited_at)
downloads_enc(id, payload BLOB, received_bytes, total_bytes, status, created_at)
site_permissions_enc(id, origin_key, permission, payload BLOB, updated_at)
```

`origin_key`는 `SHA-256(lowercase(origin))`이며 원문 origin은 암호화 payload에만 저장됩니다.

## 3. 기존 DB 마이그레이션

- v1/v2의 평문 테이블 `bookmarks`, `history`, `downloads`, `site_permissions`를 감지
- 모든 레코드를 먼저 암호화한 뒤 하나의 SQLite transaction에서 신규 테이블에 기록
- transaction 성공 시에만 기존 평문 테이블 DROP
- 암호화/기록 실패 시 transaction rollback, 기존 평문 테이블 유지
- 성공 후 legacy SharedPreferences URL도 삭제
- 신규 설치에는 평문 테이블을 생성하지 않음

## 4. 검색 및 성능

SQL LIKE로 검색할 수 없는 암호화 payload 대신:

- 북마크: 최대 1,000개를 복호화해 메모리 캐시, 검색은 메모리에서 수행
- 방문 기록: 최대 5,000개 캐시, 검색 결과 최대 500개
- 다운로드: 최대 200개 캐시
- 사이트 권한: 최대 500개를 복호화해 origin/permission 결정 캐시 유지

캐시는 추가/삭제 시 갱신 또는 무효화됩니다. 기록 추가 시 history 캐시는 다음 조회에서 재구성됩니다.

## 5. 키 무효화/재설치

- Android Keystore 키가 없거나 무효화되면 복호화는 `KEY_INVALIDATED`로 실패
- 세션 snapshot은 안전하게 삭제하고 새 세션으로 시작
- 북마크/방문 기록/다운로드/권한은 평문 fallback 없이 복호화 실패 레코드를 건너뜀
- 기존 평문 테이블이 남아 있는 마이그레이션 실패 상태에서는 신규 UI가 평문 테이블을 읽지 않음
- 실제 재설치/키 삭제 동작은 기기/에뮬레이터 검증 필요

## 6. 로그

- Keystore/DB 오류 로그에 URL, 제목, SessionState, 키 material을 출력하지 않음
- 세션 decode 오류는 고정 메시지만 기록
- GeckoView remote debugging 및 console output 비활성화

## 7. 테스트 상태

| 항목 | 상태 |
|---|---|
| AES-GCM round-trip | instrumentation 작성, 실행 UNVERIFIED |
| IV/ciphertext 변조 거부 | instrumentation 작성, 실행 UNVERIFIED |
| AAD 불일치 거부 | instrumentation 작성, 실행 UNVERIFIED |
| 잘못된 키 복호화 거부 | instrumentation 작성, 실행 UNVERIFIED |
| 키 삭제/무효화 | instrumentation 작성, 실행 UNVERIFIED |
| 세션 snapshot 복구 | instrumentation 작성, 실행 UNVERIFIED |
| 평문 DB 마이그레이션 | instrumentation 작성, 실행 UNVERIFIED |
| Keystore blob format | JVM PASS |

이 환경에는 Android 기기/에뮬레이터가 없어 instrumentation 테스트를 실행하지 못했습니다.
