# AMARE 설정 아키텍처

- 구현: `SettingsService` (`crates/amare-services/src/settings.rs`)
- 저장: `settings` 테이블 (`SettingsRepository`)
- 관련 문서: [서비스 아키텍처](SERVICE_ARCHITECTURE.md), [보안 설계](SECURITY_DESIGN.md)

---

## 1. 원칙

1. 기본값과 사용자 변경값을 구분합니다.
2. 등록된 설정만 서비스 API로 변경합니다.
3. 타입과 허용 범위를 쓰기 전에 검증합니다.
4. 여러 설정 변경은 원자적입니다.
5. 비밀번호/API 키/토큰/credential은 일반 설정에 저장하지 않습니다.
6. 아직 소비자가 없는 설정은 **정책 값**일 뿐 실제 브라우저 동작을 보장하지 않습니다.

---

## 2. 그룹

초기 그룹은 확장 가능한 enum입니다.

`appearance`, `browsing`, `search`, `privacy`, `security`, `downloads`, `tabs`, `performance`, `ai`, `agent`, `notifications`, `updates`.

현재 등록된 설정은 그룹당 1개 수준으로 최소화했습니다. 미래 설정은 `SettingDefinition`을 추가해 확장합니다.

| 키 | 그룹 | 기본값 | 규칙 |
|---|---|---|---|
| `theme` | appearance | `system` | system/light/dark |
| `home_page` | browsing | `about:blank` | text <=8192 |
| `engine` | search | `duckduckgo` | duckduckgo/google/bing |
| `telemetry` | privacy | `false` | boolean |
| `https_only` | security | `true` | boolean |
| `ask_where_to_save` | downloads | `true` | boolean |
| `restore_session` | tabs | `true` | boolean |
| `background_throttling` | performance | `true` | boolean |
| `default_mode` | ai | `web_ai` | web_ai/api |
| `enabled` | agent | `false` | boolean |
| `enabled` | notifications | `false` | boolean |
| `channel` | updates | `stable` | stable/beta |

`agent.enabled`, `ai.default_mode`, `https_only` 등은 저장 정책이며, 실행/강제는 각 소비 계층에서 구현해야 합니다. 이번 Phase는 UI/AI/브라우저 엔진을 구현하지 않습니다.

---

## 3. API

| 기능 | 메서드 |
|---|---|
| 단일 조회 | `get(context, key) -> ResolvedSetting` |
| 그룹 조회 | `list_group(context, group)` |
| 전체 조회 | `list_all(context)` |
| 단일 변경 | `set(context, key, value) -> SettingsChangeSet` |
| 다중 원자 변경 | `set_many(context, &[(key, value)])` |
| 키 초기화 | `reset_key` |
| 그룹 초기화 | `reset_group` |
| 전체 초기화 | `reset_all` |

`ResolvedSetting`은 `source=Default|User`와 `updated_at_ms`를 포함합니다.
`SettingsChangeSet`은 key/group/old/new/source를 포함해 UI가 변경 결과를 표시할 수 있게 합니다.

---

## 4. 검증

- 알 수 없는 key → `UnknownSetting`.
- 민감 key(`password`, `secret`, `token`, `api_key`, `credential`, `private_key` 등) → `SensitiveSetting`.
- 타입 불일치 → `InvalidSettingType`.
- choice/range 위반 → `SettingOutOfRange`.
- 한 번의 `set_many`에 중복 key → `DuplicateSetting`.
- 모든 입력을 검증한 뒤 `SettingsRepository::set_many` 트랜잭션으로 기록합니다. 하나라도 실패하면 아무 값도 변경되지 않습니다.

민감 값은 기존 `SecretRepository`만 사용합니다. 설정 테이블에는 평문 secret을 저장하지 않습니다.

---

## 5. 동시성/일관성

- 각 변경은 프로필 context를 검증합니다.
- 다중 변경은 `BEGIN IMMEDIATE` 트랜잭션입니다.
- 동시 설정 변경 테스트: 두 스레드가 서로 다른 key를 변경해도 모두 보존됩니다(WAL + busy timeout).
- 설정 변경은 프로필 DB 파일 단위로 격리됩니다.
