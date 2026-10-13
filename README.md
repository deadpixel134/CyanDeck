# CyanDeck

CyanDeck은 Windows 10/11 바탕화면 정리 및 Google Calendar 연동을 위한 WPF/.NET 8 프로그램입니다.

## 독립 제품 정보

| 항목 | 경로 및 이름 |
| --- | --- |
| 실행 파일 | CyanDeck.exe |
| 소스 프로젝트 | src/CyanDeck/CyanDeck.csproj |
| 설치 경로 | %LOCALAPPDATA%\\Programs\\CyanDeck |
| 설정 및 로컬 일정 | %APPDATA%\\CyanDeck |
| 업데이트 캐시 | %LOCALAPPDATA%\\CyanDeck |
| 바로가기 및 시작 항목 | CyanDeck |
| 기존 아이콘 | AppIcon.ico (변경하지 않음) |

이 버전은 **DesktopDeck 데이터와 호환하거나 자동 마이그레이션하지 않는 새 제품**입니다. 기존 DesktopDeck 설치 및 데이터 폴더를 자동으로 읽거나 제거하지 않습니다.

## 주요 기능

- Collection 그룹, 탭, Folder Portal 및 Explorer 스타일 파일 작업
- 멀티 모니터, 바탕화면 페이지, Peek/빠른 숨김, 카멜레온 배경
- Google Calendar 다중 캘린더 표시, 업로드 대상 선택, 로컬 전용 일정
- 파일 삭제 보호, 진단·상태 복구, 서명 검증 업데이트

## 공식 홈페이지 및 Google OAuth 안내

CyanDeck 공식 안내 사이트는 GitHub Pages와 DuckDNS로 운영합니다.

- 홈페이지: https://cyandeck.duckdns.org/
- 개인정보처리방침: https://cyandeck.duckdns.org/privacy.html
- 서비스 이용약관: https://cyandeck.duckdns.org/terms.html
- 웹사이트 소스: https://github.com/deadpixel134/desktopdeck-oauth-site

사이트의 이름 및 도메인이 변경되어 Google Auth Platform의 Branding
설정도 새 URL과 일치해야 합니다. Google 사용자 데이터 접근 범위와
로컬 전용 일정에 대한 안내는 개인정보처리방침에서 확인할 수 있습니다.

## 빌드 및 배포

Windows에서 .NET 8 SDK와 PowerShell을 사용합니다.

    pwsh -File ./build.ps1 -Runtime win-x64

ARM64 환경에는 win-arm64를 선택합니다. 출력은 publish/{runtime}/CyanDeck.exe 입니다.

CI 개발 빌드는 Google 인증 설정을 포함하지 않습니다. 배포용 빌드는 배포자 Google Desktop OAuth JSON과 RSA 업데이트 서명 키를 GitHub Secrets로 주입해 서명 ZIP 및 매니페스트를 검증한 후에만 릴리스합니다. 사용자 토큰 및 개인 서명 키를 GitHub 소스에 올리지 마세요.

공식 GitHub 저장소: https://github.com/deadpixel134/CyanDeck 입니다. 업데이트 채널도 `github-release://deadpixel134/CyanDeck`을 사용합니다. 이전 DesktopDeck v7.49.5 릴리스는 CyanDeck 업데이트 대상으로 사용하지 않으며, 공개된 CyanDeck v8 이상 릴리스만 자동 업데이트에 사용합니다. v8.0.0 서명 패키지는 정식 공개되었으며, 최신 릴리스에서 x64 및 ARM64용 ZIP과 검증 가능한 업데이트 매니페스트를 제공합니다. 배포 패키지의 설치·제거 자동 검사는 통과했지만 실제 데스크톱 GUI 및 Google 계정 로그인 통합 검증은 별도로 수행해야 합니다.

## CyanDeck v8.0.0 다운로드와 설치

- 정식 릴리스: https://github.com/deadpixel134/CyanDeck/releases/tag/v8.0.0
- 일반 PC(Intel/AMD): `CyanDeck-8.0.0-win-x64.zip`
- Windows ARM PC: `CyanDeck-8.0.0-win-arm64.zip`

배포 ZIP의 압축을 풀고 `CyanDeck.exe`를 실행하거나, 설치를 원하면
같은 폴더의 `install.ps1`을 PowerShell에서 실행합니다. 설치 관리자가
사용자 데이터 폴더를 삭제하거나 DesktopDeck 데이터를 이동하지 않습니다.



Collection의 참조 제거와 원본 파일 삭제는 다릅니다. Folder Portal은 실제 파일 시스템을 다루므로 삭제 및 복구 전에 주의하세요. Windows Explorer, 다중 모니터, Google OAuth, 설치/업데이트/롤백 실기기 검증은 별도로 수행해야 합니다.
