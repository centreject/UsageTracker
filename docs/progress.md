# 진행 상황 (작업 이어받기용)

마지막 갱신: 2026-11-26 10:2x. 대화 맥락을 압축한 뒤에도 이 문서만 보고 이어서 진행할 수 있게 정리한다.

## 프로젝트 한눈에

- Windows 프로그램 사용 시간 기록 앱. C# / .NET Framework 4.8, 내장 `csc.exe`로 빌드 (`.\build.ps1` → `bin\UsageTracker.exe`)
- 하나의 exe, 두 모드: **기록 모드**(트레이 상주, WinForms 미로드, 메모리 목표 6MB 이하) / **화면 모드** `--ui`(통계 창, 닫으면 프로세스 종료)
- 저장소: https://github.com/centreject/UsageTracker (Public). 버전 규칙: SemVer `0.<단계>.<수정>`, 단계마다 태그 + GitHub 사전 릴리스(zip 첨부), CHANGELOG.md·VERSION 갱신
- 기획 문서(설계·결정 기록): https://claude.ai/artifact/41et4rPaTYw789oRgPpZNt
- 수동 테스트 목록: [manual-tests.md](manual-tests.md)
- 데이터: `%LOCALAPPDATA%\UsageTracker\` (sessions-YYYY-MM.tsv, appinfo.tsv, apps.tsv, groups.tsv, settings.ini, ui.ini, health.tsv, error.log)
- 범위 밖(사용자 결정): 카테고리 분류, 목표·알림, 이전 기간 비교, CSV 내보내기, 창 제목 저장, 여러 계정 통합

## 현재 상태

- 최신 릴리스 **v0.8.3** (2026-11-26). 저장소 main은 그 뒤 커밋 1개 더 있음 (미출시: UWP 틀 "Application Frame Host" 기록 수정, 수동 테스트 결과 문서)
- **설치본**: v0.8.3, `%LOCALAPPDATA%\Programs\UsageTracker\UsageTracker.exe`, 2026-11-26 09:53부터 실행 중
  - 자동 시작: HKCU Run + Explorer\StartupApproved\Run(02…) 등록됨. 시작 메뉴 바로가기 "사용 시간 기록"(--ui)
- **24시간 측정 진행 중**: 기록 모드가 스스로 `health.tsv`에 1시간마다·시작/종료/절전/복귀 때 기록 → `.\tools\health-report.ps1 -Since '2026-11-26 09:53'`로 판정
  - 예약 작업 `usagetracker-24h-report`가 **2026-11-27 10:05**에 판정 보고 (보고만, 수정·릴리스 안 함)
  - 측정 끝날 때까지 실제 기록 모드를 건드리지 말 것 (재설치·종료 금지). 시험은 반드시 `--data=<시험 폴더>`로
- 자체 시험: `.\tools\selftest.ps1` → 30개 시험·108개 확인 모두 통과. CI(GitHub Actions)도 빌드+자체 시험 통과

## 남은 일 (순서대로)

1. ~~수동 테스트 중 화면 조작 항목~~ → 2026-11-26 10:20–10:31 진행 완료 (결과는 manual-tests.md). 남은 건 트레이 오른쪽 클릭 메뉴(M4·U1·L5 일부)뿐 — 탐색기가 "클릭만" 권한이라 Claude가 못 함
   - 주의: Claude 데스크톱 창이 화면 오른쪽(x 989–1524)을 덮고 있어 그 영역 클릭은 Claude 창으로 감 → 시험 창은 왼쪽에 두기
   - 재시험이 필요하면 아래 방법 그대로
   - 방법: 설치본 exe로 시험 폴더 사용 → 권한 대상 앱 이름이 "사용 시간 기록"과 일치
     `.\tools\make-demo-data.ps1 -Days 20` 후
     `Start-Process "$env:LOCALAPPDATA\Programs\UsageTracker\UsageTracker.exe" -ArgumentList "`"--data=$PWD\testdata-demo`""` (기록 모드)
     `... -ArgumentList "--ui `"--data=$PWD\testdata-demo`""` (통계 창)
   - 끝나면 창 제목으로 시험 인스턴스만 닫기: 기록 모드 창 제목 = `UsageTracker.Recorder|<데이터 폴더 소문자>`
   - 트레이 아이콘은 실제·시험 두 개가 같은 모양이니 트레이 메뉴에서 "종료"·설정 변경을 누르지 말 것 (실제 쪽일 수 있음)
2. **2026-11-27 10:05 판정 확인** → 모두 통과면 사용자에게 1.0.0 정식 릴리스 여부 묻기. M6(로그인 자동 시작)도 판정에 포함
3. 측정 후 v0.8.4로 묶어 설치 (사용자 승인 "판정 후 설치", 2026-11-26): CHANGELOG [미출시] 전부. 예약 작업이 판정 보고 뒤 릴리스·설치까지 한다. 예약 작업이 못 했으면 직접 진행
   - 이어서 **PR #1(fix/code-review, 코드 검토 버그 수정)을 병합해 v0.8.5**로 릴리스·설치 (사용자 결정 2026-11-26: v0.8.4에는 넣지 않고 따로). 2026-11-26 검토: 막을 문제 없음, 141개 확인 통과. CHANGELOG·VERSION 충돌은 손으로 풀고 .cs 충돌이면 멈춤
4. 개선 후보 (사용자 결정 대기): 보조 모니터 영상 자리 비움 원칙(배경음악도 사용 중으로 볼지 먼저 결정), 트레이 툴팁에 오늘 사용 시간, 모니터별 DPI(PerMonitorV2), 통계 창 코드(MainForm.cs ~1100줄) 분리, 게임패드 입력, 기록 제외 옵션

## 사용자가 할 일

- 시험 중 연 **작업 관리자 창**이 관리자 권한이라 Claude가 못 닫음 → 직접 닫기
- 수동 테스트 중 Claude가 못 하는 것: M6(재로그인 필요, 오늘 한 번 로그아웃·로그인해 주면 내일 판정에서 확인), M7, M8, L8

## 작업 규칙·주의 (지금까지 겪은 것)

- PowerShell 5.1: 한글이 든 .ps1은 UTF-8 **BOM**으로 저장. 커밋 메시지는 BOM 없이 파일로 써서 `git commit -F`
- PowerShell에서 함수 이름 `R` 금지 (Invoke-History 별칭). `FindWindow(..., $null)`은 ""로 넘어가니 `[NullString]::Value`
- git·gh는 PATH 갱신 필요: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- 커밋 메시지 끝: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- 화면 확인: `.\tools\snap.ps1 -Title '사용 시간 기록' -Out <png>` (DPI 인식 캡처), 통계 창 시험 옵션 `--view=0..3 --select=<앱ID> --theme=dark|light --favonly --search=`
- 시험 스크립트: `tools\test-run.ps1`, `tools\test-events.ps1`(일시정지·절전·잠금·시각 변경 메시지), `tools\selftest.ps1`, 진단 `--probe=<파일>`
- 로그인 사용자 GitHub 계정: centreject (gh 로그인은 외부 Chrome으로, 터미널에서 Enter 먼저 누르고 브라우저에서 승인)
