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
5. VFR 분할·PTS 기반 스냅의 순수 로직 테스트. VFR **실제 FFmpeg 출력 통합 시험은 아직 완료되지 않았습니다.**
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
- **FFmpeg 6.1.1 및 8.0.1**에서 인위적 VFR 샘플을 생성할 때 프레임 복제가 재현되어 생성된 원본이 테스트 조건을 만족하지 못했습니다. 따라서 VFR *출력 통합 테스트*는 보류했고, 기본 CFR 및 H.264 키프레임 밖 출력 회귀 시험만 필수 CI에 남겼습니다. 실제 VFR 입력/출력 호환성은 미검증입니다. 출력 엔진은 기대 디코딩 프레임 수 불일치 시 실패 처리하도록 설계했습니다.
- 인코딩의 **디코딩 프레임 수 일치**는 원본과 동일한 시각 정보의 완전한 증명이 아닙니다. 별도 고유 프레임 패턴 기반 검증을 추가했지만 모든 실사용 영상으로 일반화할 수 없습니다.
- FFmpeg 재배포 라이선스 및 설치 체계 확정 전이므로 **정식 릴리스 아님**.

## LibVLC 내장 플레이어 (2026-11)

- NuGet 패키지: `LibVLCSharp.WPF 3.10.2`, `VideoLAN.LibVLC.Windows 3.0.24`.
- 기존 WPF `MediaElement` 대신 `VideoView`에서 원본 비디오를 디코딩합니다. 네이티브 라이브러리는 Windows x64 빌드 패키지에 포함됩니다.
- LibVLC의 네이티브 HWND 위에 정확한 프레임/오류 화면을 표시하기 위해 WPF 오버레이를 `VideoView.Content` 안에 넣습니다.
- Windows Actions는 네이티브 엔진 생성과 패키지의 `libvlc.dll`/`libvlccore.dll`/plugins 존재 여부를 검사합니다.
- 프록시 생성 서비스는 이전 코드로 남아 있어도 UI에서는 호출하지 않습니다. 원본은 재인코딩하지 않고 재생합니다.
- 정확한 1프레임 확인과 최종 추출에는 FFmpeg를 계속 사용합니다.
- 실제 Windows PC에서 AV1/Opus, 4K, GPU 가속, 시간 탐색 및 재생/정지를 추가 검증해야 합니다.

## FFmpeg 다중 원본 병합/전환 테스트 (2026-11-14)

- `tests/VideoFrameCut.Merge.Tests`는 실제 다른 해상도/프레임 속도(160×90@24fps, 128×96@30fps) 및 무음 영상을 섞어 3개 구간을 병합합니다.
- `scripts/test_frame_export.py`는 기본 컷 연결, 크로스페이드(FFmpeg xfade=fade), 페이드 아웃·인(fadeblack) 각각에 대해 생성된 MP4의 H.264/AAC·크기·프레임 수를 검사합니다.
- 원본 선택은 디코딩된 프레임 번호 `[start,end)` 기준이고, 병합 규격만 첫 원본의 해상도와 정수 FPS로 통일합니다.
- 페이드 시간은 0.1~3초(기본 0.5초), 짧은 구간에는 전환 시간을 더 짧게 지정해야 합니다.
- Windows에서는 WPF 병합 작업 목록/다크 ComboBox 로딩과 설치 EXE 빌드도 확인합니다.
