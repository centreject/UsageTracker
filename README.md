# UsageTracker

Windows 데스크탑용 프로그램 사용 시간 기록 앱. 백그라운드에 상주하면서 프로그램별 사용 시간을 기록하고, 일·주·월·년 단위로 보여 준다. CPU와 메모리 점유를 최소화하는 것이 최우선 목표다.

## 상태

개발 중 — 4단계(화면 모드) 완료. 트레이 아이콘을 더블클릭하면 통계 창이 열린다. 이름 바꾸기·숨기기·합치기·설정·다크 모드는 5단계에서 추가한다. 변경 내용은 [CHANGELOG.md](CHANGELOG.md), 사람이 확인할 항목은 [docs/manual-tests.md](docs/manual-tests.md) 참고.

## 시험

```powershell
.\tools\selftest.ps1      # 집계 로직 자체 시험 (가짜 시계·창·입력)
.\tools\test-events.ps1   # 실제 실행 + 일시정지·절전·잠금·시각 변경 메시지
```

## 주요 기능 (계획)

- 파일명이 아닌 알아볼 수 있는 이름으로 표시 (예: `chrome.exe` → Google Chrome), 사용자가 이름 변경 가능
- 활성 시간(맨 앞 창)과 실행 시간(켜져 있던 시간)을 일·주·월·년 단위로 조회, 정렬
- 하루 타임라인, 시간대별 통계, 숨기기, 여러 exe 합치기
- 네트워크 통신 없음, 창 제목 미저장

## 빌드

Windows에 내장된 .NET Framework 4.8 C# 컴파일러만 있으면 된다.

```powershell
.\build.ps1
```

결과물은 `bin\UsageTracker.exe`와 `bin\UsageTracker.exe.config`다. 실행하면 트레이에 상주하며 `%LOCALAPPDATA%\UsageTracker\`에 기록한다.

시험할 때는 실제 데이터와 자동 시작을 건드리지 않도록 데이터 폴더를 따로 지정한다.

```powershell
.\tools\test-run.ps1 -Seconds 60 -Extra '--tick=5 --flush=10 --idle=0'
```

| 옵션 | 뜻 |
| --- | --- |
| `--data=폴더` | 데이터 폴더 지정. 지정하면 자동 시작을 등록하지 않는다 |
| `--tick=초` | 주기 작업 간격 (기본 60) |
| `--flush=초` | 저장 간격 (기본 300) |
| `--idle=초` | 자리 비움 기준 (기본: 설정값 5분, 0 = 끔) |

`prototype/`은 1단계 실측용 코드로, 기록용으로 남겨 두었다.

## 버전 관리

[유의적 버전](https://semver.org/lang/ko/)을 따르며, 현재 버전은 [VERSION](VERSION) 파일에 있다. 각 단계를 마칠 때 `v0.<단계>.0` 태그와 GitHub 릴리스를 만든다.
