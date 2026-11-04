# Android 성능 측정 준비

- 기준: Android 0.12.0
- 실제 성능 수치: **부분 측정** (cold start 1회), 지속 부하/메모리는 **UNVERIFIED**
- 원칙: 측정하지 않은 값을 추정하지 않음

## 1. 측정 환경 기록

```text
기기 모델: Lenovo TB138FC
Android 버전: 14 / SDK 34
SoC/RAM: 7,855,092 kB
GeckoView 버전: 152.0.20260713164047
AMARE 버전: 0.12.0
APK ABI: arm64-v8a
네트워크: 원격 ADB/Tailscale, HTTPS DuckDuckGo 로드
화면 밀도/해상도: 320 dpi / 2560x1536
배터리/충전 상태: 미기록
```

2026-11-05 측정: `am start -W` cold launch `TotalTime: 1031 ms`.
단일 측정값이며 반복 측정/백그라운드 복귀/탭 수별 메모리는 아직 UNVERIFIED입니다.

## 2. 측정 항목

- Cold start
- Warm start
- GeckoRuntime 초기화
- 첫 페이지 로딩
- 탭 생성 시간
- 탭 전환 시간
- 1 / 5 / 10 / 20탭 메모리
- 백그라운드 복귀
- 세션 복구 시간
- 다운로드 처리
- 웹 알림 처리
- 장시간 사용 시 메모리 증가

## 3. 도구

```bash
# cold/warm start
adb shell am force-stop org.amare.browser
adb shell am start -W org.amare.browser/.MainActivity

# memory
adb shell dumpsys meminfo org.amare.browser

# process/runtime logs
adb logcat -c
adb logcat -d -v time
```

Android Studio Profiler:

- CPU
- Memory
- Network
- Energy

## 4. 절차

1. 기기 재부팅 후 5분 안정화
2. Debug APK가 아닌 Release APK 사용
3. 네트워크 조건 고정
4. cold start 5회, warm start 5회
5. 1/5/10/20 탭에서 각각 메모리 기록
6. 화면 회전, 백그라운드 5분, 복귀 측정
7. 세션 복구 시간 기록
8. 다운로드/알림 1회씩 측정
9. 결과와 로그를 기기/빌드 정보와 함께 보관

## 5. 현재 상태

- 실기기: Lenovo TB138FC (Android 14/SDK 34, arm64-v8a)
- cold start 1회: 1,031 ms
- 반복 cold/warm start, 탭 수별 메모리, 장시간/백그라운드 성능: UNVERIFIED
- APK 용량 최적화(Arm64)는 별도로 수행/측정
- 성능 개선 수치 또는 목표 달성을 주장하지 않음
