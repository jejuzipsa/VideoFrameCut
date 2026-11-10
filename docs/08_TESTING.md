# 08. 빌드·사용법 및 검증

## Windows GUI 초기 실행

- GitHub Actions > 최신 성공한 **FrameCut build and core tests** 실행 > Artifacts > `FrameCut-win-x64-preview` 다운로드 후 압축 해제.
- `VideoFrameCut.App.exe` 실행. .NET 런타임을 포함하는 Windows x64 테스트 빌드이며, SmartScreen 경고나 보안 경고는 Windows 정책에 따라 표시될 수 있습니다.
- FFmpeg 배포 파일의 `ffmpeg.exe`, `ffprobe.exe`를 **앱 실행 파일과 같은 폴더**에 놓거나 두 명령을 시스템 PATH에 등록해야 영상 분석·출력이 동작합니다.
- 소스코드/FFmpeg 라이선스를 확인하고, 신뢰할 수 있는 공식 배포처에서 FFmpeg를 준비하세요. 현재 GitHub 빌드 아티팩트에는 FFmpeg가 동봉되어 있지 않습니다.
- Windows 미디어 프리뷰는 재생 편의를 위한 것입니다. 정확한 원본 컷 위치를 확인하려면 **정확한 프레임 보기**를 누르세요.
- **구간 분할**, **구간 삭제**, **시간으로 나누기**, **출력 체크**, **선택한 구간 MP4 출력** 순서로 사용할 수 있습니다.
- 파일이 크면 최초 프레임 인덱싱과 정확한 프레임 미리보기에 시간이 걸립니다. 특히 현재 정확한 미리보기는 영상 시작부터 지정 프레임까지 디코딩합니다.

## CLI (기술 검증 및 일괄 작업용)

.NET 8 SDK, FFmpeg, FFprobe가 필요합니다.

```powershell
dotnet run -c Release --project src/VideoFrameCut.Cli -- info "D:\videos\input.mp4"
dotnet run -c Release --project src/VideoFrameCut.Cli -- split "D:\videos\input.mp4" --seconds 25 --out "D:\output" --cpu
dotnet run -c Release --project src/VideoFrameCut.Cli -- split "D:\videos\input.mp4" --seconds 10 --out "D:\output" --segments 2-7 --no-audio
dotnet run -c Release --project src/VideoFrameCut.Cli -- extract "D:\videos\input.mp4" --start-frame 300 --end-frame 600 --out "D:\output" --cpu
```

CLI 옵션: `--cpu`는 libx264 강제, 생략하면 NVENC 시도 후 오류 시 CPU 대체, `--no-audio`는 무음 출력. 원본 FPS를 변환하는 옵션은 제공하지 않습니다.

## 자동 테스트

GitHub Actions에서 매 push마다 다음을 확인합니다.

1. .NET 편집 엔진 테스트 7종 (CFR, VFR, 삭제 범위, Undo/Redo).
2. Windows WPF 및 CLI 빌드.
3. 검증용 이미지 생성 → FFV1 원본 인코딩 → 선택 출력 → H.264 결과 재디코딩 → 16비트 패턴으로 **모든 프레임의 고유 번호** 검사.
4. B-frame을 포함한 H.264 원본에서 **키프레임 밖**을 추출하고 오디오 스트림 유지 검사.
5. 가변 FPS 원본에서 프레임 중복/누락 검사와 출력 프레임 PTS 간격 비교.
6. Windows x64 초기 실행 패키지를 Actions Artifacts로 보관.

```bash
python3 scripts/test_frame_export.py
```

이 검증은 synthetic video 기반입니다. **실제 대용량 영상, HEVC/HDR, 다양한 오디오 코덱, GPU NVENC, Windows 미디어 프리뷰와 H3 입력 호환성은 아직 실측되지 않았습니다.**

## 알려진 제약 / 다음 검증

- 정확한 프레임 미리보기는 긴 소스의 뒤쪽에서 느립니다.
- 현재 타임라인은 슬라이더와 구간 리스트 중심이며 정교한 썸네일/구간 블록 도형은 미구현입니다.
- 소스 영상마다 타임스탬프가 불완전할 수 있습니다. 단조 증가하지 않는 VFR PTS는 안전하게 거부합니다.
- VFR MP4의 마지막 프레임 표시시간/타임스케일은 원본 플레이어별 차이가 있을 수 있으므로 추가 호환성 점검이 필요합니다.
- **FFmpeg 8.0.1 (Ubuntu 최신 환경)**의 synthetic VFR fixture 생성 과정에서 불필요한 중복 프레임이 재현되었습니다. 현재 통합 회귀 테스트는 FFmpeg 6 계열(ubuntu-24.04)을 기준으로 고정했고, FFmpeg 8 + 실제 Windows 미디어의 VFR 처리는 미검증입니다. 출력 엔진은 기대 디코딩 프레임 수와 일치하지 않으면 실패 처리하도록 설계했습니다.
- 인코딩의 **디코딩 프레임 수 일치**는 원본과 동일한 시각 정보의 완전한 증명이 아닙니다. 별도 고유 프레임 패턴 기반 검증을 추가했지만 모든 실사용 영상으로 일반화할 수 없습니다.
- FFmpeg 재배포 라이선스 및 설치 체계 확정 전이므로 **정식 릴리스 아님**.
