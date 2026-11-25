# UsageTracker

Windows 데스크탑용 프로그램 사용 시간 기록 앱. 백그라운드에 상주하면서 프로그램별 사용 시간을 기록하고, 일·주·월·년 단위로 보여 준다. CPU와 메모리 점유를 최소화하는 것이 최우선 목표다.

## 상태

개발 중 — 1단계(실측 프로토타입) 완료. 변경 내용은 [CHANGELOG.md](CHANGELOG.md) 참고.

## 주요 기능 (계획)

- 파일명이 아닌 알아볼 수 있는 이름으로 표시 (예: `chrome.exe` → Google Chrome), 사용자가 이름 변경 가능
- 활성 시간(맨 앞 창)과 실행 시간(켜져 있던 시간)을 일·주·월·년 단위로 조회, 정렬
- 하루 타임라인, 시간대별 통계, 숨기기, 여러 exe 합치기
- 네트워크 통신 없음, 창 제목 미저장

## 빌드

Windows에 내장된 .NET Framework 4.8 C# 컴파일러만 있으면 된다.

```powershell
cd prototype
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /codepage:65001 /platform:x64 /out:bin\Spike.exe Spike.cs
Copy-Item Spike.exe.config bin\
```

## 버전 관리

[유의적 버전](https://semver.org/lang/ko/)을 따르며, 현재 버전은 [VERSION](VERSION) 파일에 있다. 각 단계를 마칠 때 `v0.<단계>.0` 태그와 GitHub 릴리스를 만든다.
