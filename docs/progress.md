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

## ⚠ 2026-11-27 발견: Claude 앱 안에서 실행한 프로세스는 가상화된다

- Claude 데스크톱 앱(MSIX 패키지 Claude_pzs8sxrjxfjjc)의 셸에서 띄운 프로세스는 샌드박스를 꺼도 `%LOCALAPPDATA%`·`%APPDATA%` 쓰기가 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`로, HKCU 쓰기가 `...\SystemAppData\Helium\User.dat`로 가고, Claude 앱이 꺼지면 함께 종료된다 (job 객체)
- 그래서 Claude가 install.ps1로 설치·실행한 기록 프로그램은 자동 시작 등록이 Windows에 안 보였고(작업 관리자 시작 앱에 없음, 로그인 때 실행 안 됨), 2026-11-26 16:32 Claude 앱 종료 때 같이 꺼졌다 → **24시간 측정 무효, v0.8.3 자동 시작 수정도 검증 안 됨**
- 11-25 이후 기록 데이터는 `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\UsageTracker\`에 있다 → 사용자가 Claude 밖에서 실제 폴더로 옮김
- 2026-11-27 09:29 사용자가 Claude 밖에서 데이터 복사(robocopy) 후 기록 프로그램 직접 실행 (pid 17932, job 밖). 작업 관리자 시작 앱에 UsageTracker.exe "사용"으로 보임. 가상 사본은 `...\LocalCache\Local\UsageTracker.claude-virtual-backup-20261127`로 이름 바꿔 보관 (Claude 쪽 읽기가 실제 폴더를 보도록)
- 사용자 결정: 측정은 v0.8.5 설치 뒤에. 2026-11-27 v0.8.4·v0.8.5 릴리스, PR #1 병합 완료. **설치는 사용자가 Claude 밖 PowerShell에서** `powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -NoBuild`
- **새 24시간 측정: v0.8.5 첫 start부터, 예약 작업이 2026-11-28 10:30에 판정 보고만**
- Claude 세션에서 `%LOCALAPPDATA%\UsageTracker`에 쓰면 가상 사본이 다시 생겨 읽기가 틀어진다 → 읽기만
- **규칙: 실제 기록 프로그램 설치·실행은 사용자가 Claude 밖 PowerShell/탐색기에서 한다.** Claude는 `--data=<저장소 폴더>` 시험만 한다 (저장소 폴더는 가상화되지 않음)

## 남은 일 (순서대로)

1. **2026-11-28 10:30 판정** (예약 작업, 보고만): v0.8.5 24시간 측정 (2026-11-27 09:39:53 시작, 사용자가 Claude 밖에서 설치). 로그인 자동 시작(M6)도 이번에 처음 제대로 검증됨
2. 모두 통과면 사용자에게 **1.0.0 정식 릴리스** 여부 묻기. 설치는 항상 사용자가 Claude 밖 PowerShell에서 `powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -NoBuild`
3. 사용자 손이 필요한 수동 테스트: 트레이 오른쪽 클릭 메뉴(M4, U1 "열기", L5 트레이 → 설정), L8(Windows 다크/라이트 전환), M7(탐색기 재시작), M8(원격 데스크톱), I4(시작 메뉴로 열기·"기록 시작"), U8(배율 다른 모니터)
4. ~~개선 후보~~ → 2026-11-27 모두 구현해 main에 커밋 (미출시, CHANGELOG [미출시]). 사용자 결정: 보조 모니터 시청은 영상 앱에 활성 시간, 기록 제외는 앞으로만. **v0.8.5 측정이 끝난 뒤 다음 버전(0.9.0)으로 릴리스, 설치는 사용자가 Claude 밖에서**. 실제 모니터 2개·게임패드 확인은 N2–N5
5. ~~정리~~ → 2026-11-27 옛 가상 사본 삭제(실제 폴더 쪽이 더 큼을 확인한 뒤), fix/code-review 브랜치 삭제

화면 조작 시험 방법(재시험 때): 설치본 exe + `--data=<저장소>\testdata-demo`, 시험 창은 화면 왼쪽(Claude 창이 오른쪽을 덮음), 트레이 메뉴 "종료"·설정 변경 금지.
## 릴리스 절차 (자동 업데이트 때문에 바뀜, 2026-11-27)

- VERSION·CHANGELOG 갱신 → `.\tools\package.ps1` → 커밋·주석 태그·push → `gh release create v<ver> dist\UsageTracker-v<ver>.zip dist\UsageTracker-v<ver>.zip.sha256 --prerelease ...` (1.0.0부터는 --prerelease 빼기: 1.0 이후 앱은 정식 릴리스만 알림)
- **.sha256 파일이 없으면 앱이 자동 업데이트하지 않고 릴리스 페이지만 연다**
- 업데이트 확인 상태: 데이터 폴더 update.ini (lastCheck, skip). 시험: `--ui --update-check --manual --yes --data=<시험 폴더>` (--yes는 시험 폴더에서만 팝업 없이 진행)

## 작업 규칙·주의 (지금까지 겪은 것)

- PowerShell 5.1: 한글이 든 .ps1은 UTF-8 **BOM**으로 저장. 커밋 메시지는 BOM 없이 파일로 써서 `git commit -F`
- PowerShell에서 함수 이름 `R` 금지 (Invoke-History 별칭). `FindWindow(..., $null)`은 ""로 넘어가니 `[NullString]::Value`
- git·gh는 PATH 갱신 필요: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- 커밋 메시지 끝: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- 화면 확인: `.\tools\snap.ps1 -Title '사용 시간 기록' -Out <png>` (DPI 인식 캡처), 통계 창 시험 옵션 `--view=0..3 --select=<앱ID> --theme=dark|light --favonly --search=`
- 리팩토링 검증(2026-11-27에 쓴 방법): 바꾸기 전 exe를 따로 복사해 두고, 같은 시험 데이터 사본으로 `--ui --view=N` 창을 PrintWindow로 캡처해 해시 비교(같은 시각대에 찍어야 함) + 시험 폴더 기록 모드에 WM_COMMAND·WM_COPYDATA·WM_CLOSE를 보내 옛·새 결과 비교
- 코드 지도: 기록 파일 형식 `SessionFormat`, 기록 모드 연락 `RecorderLink`, key=value 파일 `IniFile`(AtomicFile.cs), 자체 시험은 `SelfTest.cs`(실행·도우미) + `SelfTest.Tracker/Data/Apps.cs`
- 시험 스크립트: `tools\test-run.ps1`, `tools\test-events.ps1`(일시정지·절전·잠금·시각 변경 메시지), `tools\selftest.ps1`, 진단 `--probe=<파일>`
- 로그인 사용자 GitHub 계정: centreject (gh 로그인은 외부 Chrome으로, 터미널에서 Enter 먼저 누르고 브라우저에서 승인)
