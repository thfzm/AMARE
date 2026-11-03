# AMARE 공개 정책 및 라이선스 선택지

- 작성일: 2026-11-03
- 목적: 공개 정책/라이선스 결정 자료 및 최종 결정 기록
- 주의: 이 문서는 법률 자문이 아닙니다.
- **최종 결정: B — GitHub Public + All rights reserved** (2026-11-03)

---

## 1. 세 가지 공개 정책

### A. 공개 저장소 + 오픈소스

#### A-1. MIT

| 항목 | 내용 |
|---|---|
| 성격 | 매우 관대한 permissive license |
| 허용 | 사용, 복제, 수정, 병합, 배포, sublicense, 상업적 사용 |
| 조건 | copyright/license notice 포함, 보증 없음 |
| patent | 명시적 patent grant 없음 |
| AMARE 영향 | 외부 채택/실험/기여가 쉬움. 브라우저 코드가 널리 재사용될 수 있음 |

적합한 경우:

- 빠른 채택, 예제/생태계 확장을 우선
- 법무 부담을 낮춘 단순한 공개를 원함
- 프로젝트 이름/로고의 상표 정책을 별도로 두려는 경우

주의:

- 코드/브랜드에 대한 통제력을 잃을 수 있음
- patent grant가 없어 patent risk가 완전히 해소되지 않음
- 외부 기여자와의 라이선스/CLA 정책을 정해야 함

#### A-2. Apache-2.0

| 항목 | 내용 |
|---|---|
| 성격 | permissive license + 명시적 patent grant |
| 허용 | 사용, 수정, 배포, 상업적 사용 |
| 조건 | license/NOTICE 유지, 변경 고지, patent grant/termination 조항 |
| patent | 기여자의 특허 사용 허가 포함 |
| AMARE 영향 | 기업/상용 통합에 상대적으로 친화적. NOTICE/변경 고지 관리가 필요 |

적합한 경우:

- 장기적으로 외부 기업/기여자와 협업 가능성을 고려
- patent grant와 명확한 기여 조건을 중시
- NOTICE/attribution 관리 부담을 감수할 수 있음

주의:

- MIT보다 문서/고지 관리가 복잡함
- trademark/logo는 별도 정책 필요
- 외부 기여 시 DCO/CLA 정책을 정해야 함

---

### B. 공개 저장소 + 저작권 보유

| 항목 | 내용 |
|---|---|
| 저장소 | Public |
| 라이선스 | 별도 사용 허가 없음 / All rights reserved |
| GitHub | 코드 열람 및 GitHub 플랫폼 범위의 fork 가능 |
| 재사용 | 소스 복제·수정·배포·상업적 사용에 대한 일반 허가 없음 |
| 향후 | 권리자가 명시적으로 라이선스를 추가/변경할 수 있음 |

AMARE에 대한 의미:

- 소스/개발 과정은 공개할 수 있음
- 외부 재사용/상업적 통합을 자동 허용하지 않음
- 라이선스·로고·브랜드 정책이 확정되기 전까지 보수적인 선택
- 기여를 받을 경우 기여물 사용 권한에 대한 별도 정책이 필요

주의:

- GitHub의 공개/fork 기능과 오픈소스 라이선스는 다릅니다. GitHub ToS가 일반적인 오픈소스 재사용 허가를 대신하지 않습니다.
- 외부 기여자가 만든 코드의 권리 관계를 정리해야 함
- GitHub 공개 저장소는 검색/복제될 수 있으므로 secret/개인정보가 없어야 함

AMARE 최종 결정:

> **B. GitHub Public + All rights reserved**를 적용합니다. 소스는 공개 열람하지만 MIT/Apache-2.0 또는 기타 오픈소스 라이선스를 적용하지 않으며, 별도 서면 허가 없이 재사용·수정·재배포·상업적 사용을 허용하지 않습니다. A-1/A-2/C는 선택되지 않았습니다.

---

### C. 비공개 저장소

| 항목 | 내용 |
|---|---|
| 저장소 | Private |
| 소스 | 비공개 |
| Actions | GitHub Actions 사용 가능 |
| 배포 | 공개 Release는 별도 전략/라이선스 필요 |

장점:

- 초기 개발/보안/법무 검토를 비공개로 진행 가능
- 외부 재사용/포크 노출이 제한됨
- 라이선스 결정을 나중으로 미룰 수 있음

단점:

- 외부 기여/공개 검증/커뮤니티 채택이 어려움
- Public repo 대비 Actions/저장소 정책/비용 조건이 다를 수 있음
- 공개 전환 시 secret/이력/의존성 재감사가 필요

적합한 경우:

- RC 검증과 법무 검토를 먼저 끝내고 싶음
- 소스 공개 시점을 통제하고 싶음

---

## 2. 비교 요약

| 기준 | A-1 MIT | A-2 Apache-2.0 | B All rights reserved | C Private |
|---|---|---|---|---|
| 소스 공개 | 공개 | 공개 | 공개 | 비공개 |
| 재사용 허가 | 넓음 | 넓음 + patent | 없음(별도 허가 필요) | 없음 |
| 상업적 사용 | 허용 | 허용 | 별도 허가 | 별도 허가 |
| patent grant | 없음 | 있음 | 없음 | 없음 |
| NOTICE 관리 | 낮음 | 중간 | 낮음 | 해당 없음 |
| 외부 기여 | 쉬움 | 쉬움/명확 | 정책 필요 | 제한 |
| 브랜드 통제 | 별도 정책 | 별도 정책 | 높음 | 높음 |
| AMARE 초기 부담 | 낮음 | 중간 | 낮음 | 중간 |
| 사용자 결정 필요 | 라이선스 선택 | 라이선스/NOTICE | 권리 표시/기여 정책 | 공개 전환 계획 |

---

## 3. 라이선스 변경 대상 파일

사용자가 정책을 결정한 뒤 다음 파일을 변경해야 합니다.

| 정책 | 변경 대상 |
|---|---|
| A-1 MIT | `LICENSE` 추가, root `Cargo.toml`의 `license = "MIT"`, README 라이선스 절 갱신 |
| A-2 Apache-2.0 | `LICENSE` 추가, `NOTICE` 검토/추가, root `Cargo.toml`의 `license = "Apache-2.0"`, README 갱신 |
| B Proprietary | `COPYRIGHT.md`에 All rights reserved 고지, `Cargo.toml`의 `license-file` 설정, 각 crate 상속, README 갱신 — **반영 완료** |
| C Private | 공개 전환 전까지 LICENSE 결정 보류 가능. 배포 시 별도 EULA/라이선스 필요 |

현재 반영 상태:

- `Cargo.toml` workspace: `license-file = "COPYRIGHT.md"`
- 모든 crate: `license-file.workspace = true`
- root `COPYRIGHT.md`: All rights reserved 고지
- root `LICENSE`: 없음 (오픈소스 라이선스가 아니므로 MIT/Apache LICENSE를 만들지 않음)
- README: Public + All rights reserved 명시
- `docs/ASSET_ATTRIBUTION.md`: 로고 C2PA/권리 한계 기록
- `cargo metadata --no-deps`: package `license=None`, `license_file=../../COPYRIGHT.md`
- `cargo package -p amare-core --allow-dirty --no-verify`: 성공

**Cargo/copyright notice 반영은 완료했습니다.**

---

## 4. 로고 검토

`AMARE_logo.png`:

- 1254 × 1254, 8-bit RGBA PNG
- 501,249 bytes
- C2PA provenance chunk 포함
- AI-generated / trainedAlgorithmicMedia 표시
- ChatGPT/gpt-image 제작 도구 메타데이터
- C2PA created/converted/watermarked action 기록
- 개인 이메일/계정/로컬 경로는 발견되지 않음

판단:

- C2PA metadata 자체는 secret이 아님
- 이미지의 저작권/배포 권리는 메타데이터만으로 확정할 수 없음
- 사용자가 제공했다는 사실 외에 제작 계약/사용 허가를 확인할 수 없음
- C2PA metadata를 임의 제거하지 않음
- 이미지를 삭제/재생성하지 않음

REVIEW R-02는 유지합니다. 공개 전 다음 중 하나가 필요합니다.

1. 사용자가 로고 제작/사용/배포 권리를 확인
2. 별도 라이선스/출처 표기 결정
3. 사용 권리가 불확실하면 배포 전 교체 검토

---

## 5. Windows 재배포 고지

| 패키지 | 확인된 라이선스 | 재배포/고지 메모 | 상태 |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` 2.6.5 | NuGet `license.txt`, requireLicenseAcceptance=true | binplaced files 배포 허용 조항 있음. significant primary functionality, end-user terms, indemnity, trademark/copyleft 제한 검토 필요. `NOTICE.txt` 존재 | **REVIEW** |
| `Microsoft.Web.WebView2` SDK 1.0.4314.35 | Microsoft BSD-style `LICENSE.txt`, `NOTICE.txt` | SDK 재배포 조건과 별개로 WebView2 Runtime은 Microsoft Edge WebView2 Runtime 약관 적용 | **REVIEW** |
| `System.Management` 8.0.0 | MIT (`LICENSE.TXT`), `THIRD-PARTY-NOTICES.TXT` | 현재 `tools/windows-verify`에서만 사용. 앱 배포물에 포함하지 않으면 앱 고지 대상 아님. 포함 시 MIT notice 유지 | **REVIEW/PACKAGING** |
| WebView2 Evergreen Runtime | 별도 Microsoft 제품/약관 | 현재 RC는 Runtime을 번들하지 않고 설치 전제로 함. Evergreen 배포/설치 방식 검토 필요 | **REVIEW** |

공식 자료:

- Windows App SDK: <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- Windows App SDK NuGet: <https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.6.5>
- WebView2 SDK NuGet: <https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35>
- WebView2 distribution docs: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution>
- WebView2 Evergreen: <https://developer.microsoft.com/microsoft-edge/webview2/>
- System.Management NuGet: <https://www.nuget.org/packages/System.Management/8.0.0>
- .NET Runtime license: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>

위 링크와 로컬 NuGet license/NOTICE 파일은 확인했지만, 실제 앱 배포물에 어떤 NOTICE를 포함할지는 패키징 단계에서 별도 검토가 필요합니다. 이 문서는 법률 자문이 아닙니다.

---

## 6. AMARE 결정

**최종 결정은 B(공개 저장소 + 저작권 보유)**입니다.

이유:

1. 라이선스/로고/Windows 재배포 정책이 아직 확정되지 않음
2. 공개 소스 검증과 GitHub Actions는 사용할 수 있음
3. MIT/Apache-2.0을 지금 선택하면 이후 방향 전환 비용/기대가 커질 수 있음
4. 소스 공개를 하되 재사용 허가를 자동 부여하지 않는 보수적 정책이 가능

B를 적용하더라도 다음은 별도 결정이 필요합니다.

- 외부 기여를 받을지
- 기여자에게 어떤 권한/CLA를 요구할지
- 로고/AMARE 이름의 상표 정책
- 향후 MIT/Apache-2.0으로 전환할지

---

## 7. 사용자가 결정해야 할 항목

- [ ] A-1 MIT
- [ ] A-2 Apache-2.0
- [x] B Public + All rights reserved — **선택됨**
- [ ] C Private repository
- [ ] 로고 사용/배포 권리 확인 (파일 유지 결정은 완료, 법적 권리 확인은 미완료)
- [ ] WindowsAppSDK/WebView2 Runtime 고지/배포 방식
- [ ] 외부 기여 정책(CLA/DCO 여부)

최종 결정 B에 따라 `COPYRIGHT.md`와 `license-file`을 반영했습니다. MIT/Apache-2.0 `LICENSE`는 생성하지 않았습니다. 저작권 보유자의 법적 실체와 상업적 배포 조건은 필요 시 별도 확인합니다.
