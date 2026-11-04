# Android 성능 측정 준비

- 기준: Android 0.12.0
- 실제 성능 수치: **UNVERIFIED**
- 원칙: 측정하지 않은 값을 추정하지 않음

## 1. 측정 환경 기록

```text
기기 모델:
Android 버전:
SoC/RAM:
GeckoView 버전: 152.0.20260713164047
AMARE 버전: 0.12.0
APK ABI: arm64-v8a
네트워크:
화면 밀도/해상도:
배터리/충전 상태:
```

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

- 실제 기기 없음
- 성능 수치 없음
- APK 용량 최적화는 별도로 수행/측정
- 성능 개선 수치 또는 목표 달성을 주장하지 않음
