# DesktopDeck

DesktopDeck는 Windows 10/11용 바탕화면 파일 정리와 Google Calendar 연동을 지원하는 WPF/.NET 8 프로그램입니다.

## 기능

- Collection 그룹으로 바탕화면 아이콘 정리 및 탭 관리. 참조형 Collection에서 항목만 제거할 경우 원본 파일은 보존합니다.
- 실제 폴더를 보는 Folder Portal, 드래그 앤 드롭, 파일 탐색기 스타일 복사·붙여넣기
- 여러 모니터, 바탕화면 페이지 전환, Peek/Quick-hide, 배경 카멜레온 블러
- Google Calendar 다중 캘린더 조회/일정 추가 및 로컬 전용 일정
- 휴지통 삭제 사전 용량 검사: 크기 초과 또는 확인 불가인 경우 영구 삭제 위험을 추가로 경고
- 프로그램 안전 모드, 상태 복구, 서명 검증형 업데이트

## 설치와 실행

[GitHub Releases](https://github.com/deadpixel134/DesktopDeck/releases)에 **서명된 릴리스가 게시된 경우에만** Windows ZIP을 받습니다. 현재 저장소의 소스는 빌드/테스트가 필요한 개발판입니다.

1. Windows 10/11 PC에서 .NET 8 SDK를 설치합니다.
2. 소스를 내려받아 PowerShell 7에서 `pwsh -File ./build.ps1 -Runtime win-x64` 실행합니다. `build.cmd`도 사용할 수 있습니다.
3. 설치 가능한 ZIP이 있으면 안전한 새 폴더에 압축을 풀고 `pwsh -File ./install.ps1`을 실행합니다.
4. 기존 사용자 레이아웃·일정은 `%APPDATA%\DesktopDeck`에서 별도 백업 후 업데이트합니다.

## 사용 설명

바탕화면을 드래그해 그룹을 만들고 파일을 그룹 안으로 끌어 놓습니다. Collection은 기존 파일에 대한 참조이며, Folder Portal은 실제 폴더와 연결되어 있으므로 삭제나 이동은 원본 파일에 영향을 줄 수 있습니다.

캘린더는 설정/트레이에서 활성화합니다. Google 로그인을 연결한 경우 표시할 캘린더를 여러 개 선택하고 신규 일정을 작성할 대상 캘린더를 따로 선택할 수 있습니다. 구글에 업로드하지 않을 일정은 로컬 저장 옵션을 사용합니다. 캘린더의 바탕화면 표시가 실패하면 트레이에서 일반 창 모드를 선택합니다.

### 삭제 안전성

Collection에서 참조만 제거하는 것은 물리적 삭제가 아닙니다. 반면 실제 파일을 삭제하는 경우 용량·휴지통 한도를 사전 확인하고, Windows가 영구 삭제할 가능성이 있을 때 다시 확인합니다. 취소하면 원본을 건드리지 않습니다. 탐색기 네이티브 우클릭 메뉴는 Windows 기본 동작을 따릅니다.

### 단축키

- `Alt+←/→`: 바탕화면 페이지 전환
- `Ctrl+Alt+D`: 빠른 숨김
- `Ctrl+F`: 검색
- `Ctrl+Tab`: 그룹 탭 전환

## 개발 및 안전성 검사

```powershell
dotnet build src/DesktopDeck/DesktopDeck.csproj -c Release
python tools/run_v43_safety_regressions.py
python tools/test_v45_recycle_bin_preflight.py
```

Google OAuth 데스크톱 앱용 JSON은 소스 저장소에 **절대로 올리지 마세요**. 배포 빌드용 JSON은 GitHub Actions Secret `DESKTOPDECK_GOOGLE_DESKTOP_OAUTH_JSON`으로 공급합니다. 릴리스 개인 서명 키도 Secret `DESKTOPDECK_UPDATE_SIGNING_KEY_PEM`에 별도로 저장하고 공개하지 않습니다. 클라이언트 ID는 공개될 수 있지만 사용자 토큰은 절대로 공개되면 안 됩니다.

GitHub Releases 기반 자동 업데이트는 별도의 **서명된 업데이트 매니페스트/배포 자산**이 게시되어야 동작합니다. 실제 Windows 빌드와 업데이트 동작은 별도 테스트가 필요합니다.

## 주의 사항

데스크톱 셸 통합과 폴더 파일 작업은 사용자 데이터를 다룰 수 있습니다. 삭제나 복구 전 테스트 폴더 및 백업으로 검증하세요. 과거 개발 기록, 임시 테스트 보고서, 개인 설정, 로그, 인증정보, 빌드 결과물은 이 공개 저장소에 포함하지 않습니다.

별도 라이선스 파일이 없다면 재사용·재배포 라이선스는 허가된 것으로 해석하지 마세요.
