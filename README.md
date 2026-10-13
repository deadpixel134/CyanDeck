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

## 빌드 및 배포

Windows에서 .NET 8 SDK와 PowerShell을 사용합니다.

    pwsh -File ./build.ps1 -Runtime win-x64

ARM64 환경에는 win-arm64를 선택합니다. 출력은 publish/{runtime}/CyanDeck.exe 입니다.

CI 개발 빌드는 Google 인증 설정을 포함하지 않습니다. 배포용 빌드는 배포자 Google Desktop OAuth JSON과 RSA 업데이트 서명 키를 GitHub Secrets로 주입해 서명 ZIP 및 매니페스트를 검증한 후에만 릴리스합니다. 사용자 토큰 및 개인 서명 키를 GitHub 소스에 올리지 마세요.

현재 GitHub 저장소 주소는 https://github.com/deadpixel134/DesktopDeck 입니다. 이 주소는 업데이트 파일 호스팅 경로일 뿐, 프로그램 이름과 저장 경로는 모두 CyanDeck입니다. 저장소 자체의 이름 변경은 별도 GitHub 관리 작업입니다.

## 안전

Collection의 참조 제거와 원본 파일 삭제는 다릅니다. Folder Portal은 실제 파일 시스템을 다루므로 삭제 및 복구 전에 주의하세요. Windows Explorer, 다중 모니터, Google OAuth, 설치/업데이트/롤백 실기기 검증은 별도로 수행해야 합니다.
