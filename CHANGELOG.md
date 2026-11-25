# 변경 이력

이 프로젝트는 [유의적 버전(SemVer)](https://semver.org/lang/ko/)을 따른다.
1.0.0 전까지는 `0.<단계>.<수정>` 형식으로 개발 단계를 표시한다.

## [0.1.0] - 2026-11-25

### 추가
- 1단계 실측 프로토타입 (`prototype/Spike.cs`)
  - 활성 창 감지: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 이벤트 방식
  - 트레이 아이콘: `Shell_NotifyIcon` 직접 호출 (WinForms 미사용)
  - 1분 주기 작업: 자리 비움, 소리 재생(WASAPI 세션), 전체 화면, 보이는 창 목록
  - 주기마다 `EmptyWorkingSet`으로 메모리 정리

### 측정 결과
- 작업 관리자 "메모리"(private working set): 평소 0.08MB, 최대 0.87MB (목표 6MB 이하)
- CPU: 60초 간격 약 3분 실행 시 평균 0.05% (목표 0.1% 미만)
- 소리 재생 감지 확인. 전체 화면 감지와 24시간 실행 테스트는 아직 하지 않았다.
