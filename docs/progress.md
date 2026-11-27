# 진행 상황 (작업 이어받기용)

마지막 갱신: 2026-11-27 오후. 대화 맥락을 압축한 뒤에도 이 문서만 보고 이어서 진행할 수 있게 정리한다.

## 프로젝트 한눈에

- Windows 프로그램 사용 시간 기록 앱. C# / .NET Framework 4.8, 내장 `csc.exe`로 빌드 (`.\build.ps1` → `bin\UsageTracker.exe`)
- 하나의 exe, 두 모드: **기록 모드**(트레이 상주, WinForms 미로드, 메모리 목표 6MB 이하) / **화면 모드** `--ui`(통계 창, 닫으면 프로세스 종료)
- 저장소: https://github.com/centreject/UsageTracker (Public). 버전 규칙: SemVer `0.<단계>.<수정>`, 단계마다 태그 + GitHub 사전 릴리스(zip 첨부), CHANGELOG.md·VERSION 갱신
- 기획 문서(설계·결정 기록): https://claude.ai/artifact/41et4rPaTYw789oRgPpZNt
- 수동 테스트 목록: [manual-tests.md](manual-tests.md)
- 데이터: `%LOCALAPPDATA%\UsageTracker\` (sessions-YYYY-MM.tsv, appinfo.tsv, apps.tsv, groups.tsv, settings.ini, ui.ini, health.tsv, error.log)
- 범위 밖(사용자 결정): 카테고리 분류, 목표·알림, 이전 기간 비교, CSV 내보내기, 창 제목 저장, 여러 계정 통합

## 현재 상태

- 최신 릴리스 **v0.9.0** (2026-11-27, 사전 릴리스, zip+sha256). 업데이트 알림·기록 제외·보조 모니터 시청·게임패드·모니터별 배율·코드 정리. 릴리스 전 가속 시험 통과(0.8.5 대비 핸들 +0.7, 메모리 +0.29MB)
- **설치본**: v0.8.5 (0.9.0은 사용자가 Claude 밖에서 설치할 차례), `%LOCALAPPDATA%\Programs\UsageTracker\UsageTracker.exe`, 2026-11-27 09:39:53부터 (사용자가 Claude 밖에서 설치)
  - 자동 시작: HKCU Run + Explorer\StartupApproved\Run 등록, 작업 관리자 시작 앱에 "사용"으로 보임. 시작 메뉴 바로가기 "사용 시간 기록"(--ui)
- **v0.8.5 측정 결과 (3.4시간, 2026-11-27 사용자 결정으로 여기서 마침)**: 메모리 최대 4.7MB, CPU 0.03%, 디스크 쓰기 18.1회/시간, 오류 0, 비정상 재시작 0, 절전·복귀 5회 정상, 핸들 375→372(늘지 않음). 모두 통과
- **측정 방식 바꿈 (2026-11-27)**: 버전마다 24시간 전용 측정을 하지 않는다. 기록 모드는 평소에도 `health.tsv`에 1시간마다·시작/종료/절전/복귀 때 자원을 적으므로, 설치 뒤 평소처럼 쓰고 나중에 `.\tools\health-report.ps1`(버전별로 나눠 판정)로 본다. 측정 중이라고 설치본을 못 건드리는 제약 없음. 오래 켜 둘 때 누수는 릴리스 전 가속 시험(`tools\soak.ps1`)으로 본다 → 아래 "릴리스 전 확인 기준"
- 자체 시험: `.\tools\selftest.ps1` → 41개 시험·177개 확인 모두 통과. CI(GitHub Actions)도 빌드+자체 시험 통과

## ⚠ 2026-11-27 발견: Claude 앱 안에서 실행한 프로세스는 가상화된다

- Claude 데스크톱 앱(MSIX 패키지 Claude_pzs8sxrjxfjjc)의 셸에서 띄운 프로세스는 샌드박스를 꺼도 `%LOCALAPPDATA%`·`%APPDATA%` 쓰기가 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`로, HKCU 쓰기가 `...\SystemAppData\Helium\User.dat`로 가고, Claude 앱이 꺼지면 함께 종료된다 (job 객체)
- 그래서 Claude가 install.ps1로 설치·실행한 기록 프로그램은 자동 시작 등록이 Windows에 안 보였고(작업 관리자 시작 앱에 없음, 로그인 때 실행 안 됨), 2026-11-26 16:32 Claude 앱 종료 때 같이 꺼졌다 → **24시간 측정 무효, v0.8.3 자동 시작 수정도 검증 안 됨**
- 11-25 이후 기록 데이터는 `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\UsageTracker\`에 있다 → 사용자가 Claude 밖에서 실제 폴더로 옮김
- 2026-11-27 09:29 사용자가 Claude 밖에서 데이터 복사(robocopy) 후 기록 프로그램 직접 실행 (pid 17932, job 밖). 작업 관리자 시작 앱에 UsageTracker.exe "사용"으로 보임. 가상 사본은 `...\LocalCache\Local\UsageTracker.claude-virtual-backup-20261127`로 이름 바꿔 보관 (Claude 쪽 읽기가 실제 폴더를 보도록)
- 사용자 결정: 측정은 v0.8.5 설치 뒤에. 2026-11-27 v0.8.4·v0.8.5 릴리스, PR #1 병합 완료. **설치는 사용자가 Claude 밖 PowerShell에서** `powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -NoBuild`
- Claude 세션에서 `%LOCALAPPDATA%\UsageTracker`에 쓰면 가상 사본이 다시 생겨 읽기가 틀어진다 → 읽기만
- **규칙: 실제 기록 프로그램 설치·실행은 사용자가 Claude 밖 PowerShell/탐색기에서 한다.** Claude는 `--data=<저장소 폴더>` 시험만 한다 (저장소 폴더는 가상화되지 않음)

## 남은 일 (순서대로)

1. ~~0.9.0 릴리스~~ (2026-11-27 완료) → 사용자가 Claude 밖에서 설치 → 재부팅 1회로 로그인 자동 시작(M6) 확인. 그 뒤 평소 사용, 예약 작업 `usagetracker-24h-report`(2026-11-28 10:30)가 health-report로 0.9.0 판정 보고만
2. 0.9.0이 문제없으면 사용자에게 **1.0.0 정식 릴리스** 여부 묻기. 설치는 항상 사용자가 Claude 밖 PowerShell에서 `powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -NoBuild`
3. 사용자 손이 필요한 수동 테스트: 트레이 오른쪽 클릭 메뉴(M4, U1 "열기", L5 트레이 → 설정), L8(Windows 다크/라이트 전환), M7(탐색기 재시작), M8(원격 데스크톱), I4(시작 메뉴로 열기·"기록 시작"), U8(배율 다른 모니터)
4. ~~개선 후보~~ → 2026-11-27 모두 구현, v0.9.0으로 릴리스. 사용자 결정: 보조 모니터 시청은 영상 앱에 활성 시간, 기록 제외는 앞으로만. 실제 모니터 2개·게임패드 확인은 N2–N5, 실제 설치본 업데이트 팝업 클릭은 N6 (0.9.0 다음 버전이 나올 때 처음 가능)
5. ~~정리~~ → 2026-11-27 옛 가상 사본 삭제(실제 폴더 쪽이 더 큼을 확인한 뒤), fix/code-review 브랜치 삭제

화면 조작 시험 방법(재시험 때): 설치본 exe + `--data=<저장소>\testdata-demo`, 시험 창은 화면 왼쪽(Claude 창이 오른쪽을 덮음), 트레이 메뉴 "종료"·설정 변경 금지.
## 릴리스 전 확인 기준 (2026-11-27, 버전마다 24시간 측정 대신)

| 바뀐 곳 | 확인 |
|---|---|
| 통계 창·설정 창만 (src\ui) | 자체 시험 + 같은 시각대 화면 캡처 비교 (창을 닫으면 프로세스가 끝나 장시간 문제 없음) |
| 기록 모드 (Tracker, Env, Program, Audio, AppResolver, Native 등) | 위 + **가속 시험** `.\tools\soak.ps1` (기본 24분 = 1분 주기 약 24시간 분량, 1분마다 일시정지·절전·잠금·설정 다시 읽기·저장·시각 변경 이벤트. 설치된 릴리스를 비교용으로 동시에 돌려 PC 쪽 변화(새 창·소리 장치로 두 프로세스 모두 핸들이 느는 계단)를 빼고, 워밍업 뒤 비교용보다 더 는 양이 핸들 +15·예약 메모리 +1.5MB 이하, 오류 없음, 정상 종료) |
| 절전·잠금·로그인·자동 시작·설치 처리 | 위 + 실제로 한 번씩 (절전 1회, 재부팅 1회) |

- 가속 시험은 시험 폴더(%TEMP%)에서 돌아 설치본과 무관. Claude 안에서 돌려도 된다 (데이터 폴더가 %LOCALAPPDATA% 밖이고 자동 시작을 건드리지 않음)
- 설치 뒤에는 평소 사용 중 쌓이는 health.tsv를 `health-report.ps1`로 버전별 판정 (전용 측정 기간 없음)

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
