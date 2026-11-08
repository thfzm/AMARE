# AMARE Windows 코드 서명 / MSIX 상태 (0.42.0)

## 현재 상태

- Portable ZIP: 빌드/체크섬/실행 **PASS**. 서명 없이 배포 가능한 개발/RC 경로다.
- unsigned MSIX: 빌드 **PASS**, 설치는 **BLOCKED** (`0x80073D2C`, unsigned publisher namespace).
- 공개 GitHub Release: **0개**. 공개 Release는 만들지 않는다.
- 공개 배포용 코드 서명 인증서: **없음**. 임의의 자체 서명 인증서를 배포용으로 생성하지 않는다.

## MSIX 구성

- Identity: `Amare.Browser`
- Publisher: `CN=AMARE`
- Version: `0.42.0.0`
- Architecture: x64

`Publisher`는 서명 인증서의 Subject와 일치해야 한다. 현재 `CN=AMARE`는 개발용 manifest 값이며, 공개 배포 시에는 실제 코드 서명 인증서의 Subject로 교체해야 한다.

## 서명이 필요한 이유

Windows는 신뢰된 publisher의 MSIX만 일반 설치로 허용한다. unsigned package는 개발자 모드/사이드로드 정책에 따라 설치가 거부될 수 있으며, 현재 CI에서는 `0x80073D2C`로 실패한다. Portable ZIP은 이 제한을 우회하지 않고 별도 배포 경로로 유지한다.

## 공개 배포 절차 (인증서 제공 후)

1. 신뢰 가능한 Authenticode/MSIX 코드 서명 인증서를 준비한다.
2. `Package.appxmanifest`의 Publisher를 인증서 Subject와 일치시킨다.
3. `signtool sign /fd SHA256 /f <pfx> /p <password> /tr <timestamp-url> /td SHA256 <msix>`로 서명한다.
4. `signtool verify /pa /v <msix>`로 검증한다.
5. 깨끗한 Windows 11 VM에서 설치/업그레이드/제거를 확인한다.
6. ZIP과 MSIX의 SHA-256 및 release manifest를 갱신한다.
7. 인증서 만료일과 timestamp 정책을 기록한다.

## 금지

- 자체 서명 인증서를 공개 배포용으로 사용하지 않는다.
- 비밀키/인증서를 repository, ZIP, MSIX, 로그에 넣지 않는다.
- 서명 없이 MSIX 설치가 성공했다고 기록하지 않는다.
- 공개 GitHub Release를 자동 생성하지 않는다.
