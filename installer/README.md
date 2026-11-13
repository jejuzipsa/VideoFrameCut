# FrameCut 포터블 설치 프로그램

설치 파일: FrameCut-Portable-Setup-win-x64.exe

## 설치 및 첫 실행

1. 설치 EXE 실행 → 파일을 풀 폴더 선택 (기본: 사용자 AppData 아래 FrameCut).
2. 관리자 권한, 시스템 PATH, 파일 연결, 바로가기, 제거 레지스트리 등록 없이 선택한 폴더에 프로그램을 압축 해제합니다.
3. 마지막에 'FrameCut 실행'을 선택하면 선택한 폴더의 FrameCut.exe를 실행합니다.
4. 첫 실행 때 ffmpeg.exe, ffprobe.exe가 프로그램 옆 또는 PATH에 있으면 다운로드하지 않습니다. 하나라도 없으면 Gyan FFmpeg Essentials ZIP과 제공처의 SHA-256 검증 파일을 HTTPS로 받아 검증한 후 두 파일을 프로그램 폴더에 설치합니다.
5. 진행률 표시와 취소/재시도를 지원합니다. 실패하면 기존 파일은 지우지 않으며 설치 버튼으로 다시 시도할 수 있습니다.

## 보안과 라이선스

- FFmpeg 공급처: https://www.gyan.dev/ffmpeg/builds/
- ZIP: https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip
- 검증: https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256
- 검증값 오류·누락된 실행파일·비정상 파일은 설치하지 않습니다.
- 설치 EXE는 아직 코드 서명이 없습니다. Chrome/Windows Defender 경고를 무조건 무시하지 말고 검사를 진행하세요.
- LibVLC/LibVLCSharp 및 FFmpeg 라이선스와 재배포 조건을 배포 전에 확인해야 합니다.

## 빌드

GitHub Actions → Build FrameCut portable installer EXE → 실행 후 Artifacts에서 설치 파일 다운로드.
GitHub 단일 파일 제한(100MB)을 넘지 않으면 installer/output/에도 자동 커밋합니다. 크면 Actions artifact만 생성합니다.

개발 PC에는 .NET 8 SDK와 Inno Setup 6이 필요합니다.
dotnet publish src/VideoFrameCut.App/VideoFrameCut.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false --output publish/win-x64
ISCC.exe installer/FrameCut.iss

LibVLC DLL과 플러그인은 설치 파일에 포함됩니다. FFmpeg는 최초 실행 시 필요할 때 별도 다운로드합니다.