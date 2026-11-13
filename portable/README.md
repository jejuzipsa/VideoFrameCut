# FrameCut Windows x64 - Git 포터블 실행 파일

Chrome 브라우저를 통하지 않고 기존에 클론한 Git 저장소에서 받아 실행하는 폴더입니다. GitHub Actions가 이 위치에 실제 EXE 및 VLC 라이브러리 파일을 푸시합니다.

## 다운로드 없이 갱신

프로젝트가 클론된 로컬 디렉터리에서 PowerShell 또는 CMD 실행:

    git pull origin main

이후 portable/FrameCut-win-x64/FrameCut.exe를 실행합니다. libvlc 폴더와 DLL, plugins는 지우거나 이동하지 마세요.

FFmpeg/FFprobe가 없으면 FrameCut 첫 실행 때 자동 다운로드와 SHA-256 검증을 거쳐 프로그램 폴더에 설치합니다. 설치 실패 시 별도 복사도 가능합니다.

## 다음 빌드

GitHub Actions의 "Publish FrameCut portable folder into Git" 워크플로에서 Run workflow를 누르면 현재 main 기준으로 Windows x64 빌드를 다시 만들고 이 폴더를 업데이트합니다.

## 안전과 저장소 용량

- Git에 실행 바이너리가 올라가므로 초기 clone 및 pull 크기가 커집니다.
- GitHub에서 생성된 파일임을 확인하는 것은 보안 검사와 별개입니다. 사용 전 Windows Defender로 검사하세요.
- Chrome이 명시적으로 멀웨어를 감지했다고 표시했다면 탐지 원인 확인 전 실행하지 마세요.
- 현재 코드 서명이 없으며 VLC 서드파티 라이브러리의 LGPL 라이선스 고지/교체 가능성 등을 준수해야 합니다.
