# FrameCut 패치노트 및 개발 기록

> 저장소: [jejuzipsa/VideoFrameCut](https://github.com/jejuzipsa/VideoFrameCut)  
> 기준 브랜치: `main` · 날짜/시간 표기: **한국 표준시(KST, UTC+09:00)**  
> 최초 작성: **2026-11-15** · 기록 기준 커밋: [`758e6bcd`](https://github.com/jejuzipsa/VideoFrameCut/commit/758e6bcd524013d743e265c7d96257e48d7307ed)  
> **기록 원칙:** 이전 패치를 되돌리거나 대체해도 해당 패치의 기록을 삭제하지 않는다. 롤백은 날짜가 있는 **새 기록**으로 추가한다.

---

## 1. 프로젝트 설명

### 1.1 개요

**FrameCut(VideoFrameCut)**은 Windows 11 x64용 영상 분할·선택 추출·병합·압축 프로그램이다. LosslessCut처럼 원하는 지점에서 구간을 나누고, 필요 없는 구간을 비파괴적으로 제외한 다음, 남긴 구간만 일정 시간 간격으로 분할하거나 MP4로 추출한다. 여러 원본 구간을 순서대로 병합하고, 필요한 경우 압축·회전·좌우 반전도 수행한다.

핵심 목표는 **키프레임에 맞춘 고속 무재인코딩보다 원본의 표시 프레임을 정확하게 선택하는 것**이다.

### 1.2 확정된 제품 원칙

1. **원본 불변:** 원본 영상은 읽기 전용으로 취급한다. 편집에서의 삭제는 목록에서 제외하는 작업이며 원본 파일을 삭제하거나 덮어쓰지 않는다.
2. **정확한 구간:** 구간은 원본 표시 프레임의 반열린 범위 `[start, end)`로 보관한다. 분할 경계에서 프레임 중복·누락이 생기지 않도록 한다.
3. **FPS와 시간 유지:** 개별 컷 출력은 원본 FPS 및 프레임 타이밍 보존을 기본으로 한다. H3 입력용 **24fps 사전 변환을 강제하지 않는다**. VFR은 평균 FPS를 곱하는 방식 대신 실제 표시 PTS에 근거한다.
4. **재인코딩 허용:** 정확한 프레임 선택을 위해 FFmpeg 재인코딩을 허용한다. NVIDIA NVENC를 우선 시도하되 실패하면 CPU 경로를 사용한다.
5. **미리보기와 실제 컷 구분:** LibVLC 재생은 빠른 미리보기용이다. 정확한 첫/마지막 프레임 확인과 최종 추출은 FFmpeg 및 원본 프레임 인덱스를 따른다.
6. **선택 출력·개별 실패 처리:** 작업 대상으로 선택한 구간만 출력하며, 작업별 오류·취소·출력 파일 충돌을 명확히 처리한다.
7. **검증:** 출력이 열리는지, 예상 프레임 수와 실제 디코딩 프레임 수가 같은지 확인하고 작업 내역을 기록한다. 단, **프레임 수 일치만으로 원본 프레임 내용까지 동일함을 보증하지는 않는다**.
8. **다른 목적의 출력 정책 구분:** 여러 원본을 이어 붙이는 병합은 첫 원본을 기준으로 공통 해상도/FPS로 정규화할 수 있으며, 압축 모드 ③은 최대 30fps로 변환할 수 있다. 이는 **개별 정확 컷의 원본 FPS 유지 원칙과 별개**다.

### 1.3 기술 구성 및 저장 위치

| 영역 | 구성 |
| --- | --- |
| GUI | C# / .NET 8 / WPF, 기본 다크 테마 |
| 핵심 편집 로직 | `src/VideoFrameCut.Core/` — 프레임 범위, 분할·합치기, Undo/Redo |
| 영상 분석·재생 | `src/VideoFrameCut.Media/` — FFprobe, FFmpeg 보조 기능, LibVLC 연동 |
| 내보내기 | `src/VideoFrameCut.Export/` — 컷, 병합, HEVC 압축/변환 |
| GUI 구현 | `src/VideoFrameCut.App/` |
| 명령줄 도구 | `src/VideoFrameCut.Cli/` |
| 자동 테스트 | `tests/`, `scripts/test_frame_export.py` |
| 자동 빌드·배포 | `.github/workflows/build.yml`, `build-installer.yml`, `installer/` |
| 오류 로그 | `%LOCALAPPDATA%\FrameCut\logs\` (로컬 저장) |

참고 문서: [요구사항](docs/01_REQUIREMENTS.md) · [아키텍처](docs/02_ARCHITECTURE.md) · [프레임 모델](docs/03_FRAME_MODEL.md) · [UI 설계](docs/04_UI_DESIGN.md) · [출력 검증](docs/05_EXPORT_AND_VERIFICATION.md) · [로드맵](docs/06_ROADMAP.md) · [테스트](docs/08_TESTING.md)

---

## 2. 개발 가이드라인

### 2.1 코드·기능 수정 규칙

1. 기존 기능을 보존하면서 변경한다. 핵심 프레임 모델, 원본 보호, 파일명 충돌 방지, Undo/Redo 동작을 임의로 바꾸지 않는다.
2. **계획 → 코드 수정 → 해당 테스트/빌드 → 패치노트 작성 → 커밋** 순서를 기본으로 한다. 사용자 PC에서만 확인할 수 있는 사항은 검증 완료라고 쓰지 않는다.
3. 각 기능의 책임을 명확히 분리한다. WPF UI 이벤트 코드에 미디어 처리·FFmpeg 옵션·도메인 규칙을 계속 누적하지 말고 점진적으로 분리한다.
4. FFmpeg 실행 실패, NVENC→CPU 대체, 작업 취소, 오류 코드 및 원인을 구분하여 기록한다. 오류를 숨긴 채 성공으로 처리하지 않는다.
5. 정확 컷, 병합, 압축/변환의 FPS·오디오·HDR 정책을 혼동하지 않는다. 정책 변경은 코드·UI 설명·테스트를 함께 갱신한다.
6. 실제 원본 영상과 출력 파일은 덮어쓰지 않는다. 임시 파일은 정상 완료/취소/오류 시 정리하고, 사용자 데이터 삭제는 명시적 승인 없이 수행하지 않는다.
7. FFmpeg/LibVLC 등 외부 라이브러리를 배포할 때는 버전, 라이선스, 필수 네이티브 파일과 설치/교체 경로를 확인한다.
8. 성능 개선보다 프레임 선택의 정확성·원본 보호·출력 검증을 우선한다.

### 2.2 테스트·배포 규칙

- 기본 검증: Core 테스트, Windows WPF/CLI 빌드, GUI 다크 테마 스모크 테스트, FFmpeg 실제 샘플 출력 검증.
- 영상 변경 검증: 원본 프레임 범위, 출력 디코딩 프레임 수, 원본 불변, FPS/PTS, 오디오 유무, 인코더 대체, 파일 충돌, 취소 후 임시파일 정리.
- 압축/변환 변경 검증: 세 프리셋, 90°/180°/270° 회전, 좌우 반전, MOV 표시 회전 태그, HEVC `hvc1`, HLG/PQ 10비트 메타데이터, 영상 방향과 해상도.
- 실사용 별도 점검: Windows 11·RTX NVENC, 4K 및 장시간 영상, AV1/Opus 탐색, VFR 원본 출력, HDR 재생 환경, 4K/DPI 레이아웃. 자동 CI 성공을 실기기 검증으로 대체하지 않는다.
- 배포 파일의 생성/검증 결과와 커밋을 연결한다. 설치 EXE는 용량이 크므로 Git 이력 누적을 최소화하도록 배포 정책을 계속 검토한다.

### 2.3 **패치노트 작성·롤백 보존 규칙 (필수)**

1. 앞으로 **기능 추가, 버그 수정, UI 변경, 테스트·빌드·배포 변경, 기능 제거, 설정/정책 변경, 롤백**마다 해당 날짜의 항목을 추가한다. 사소한 연속 커밋은 하나의 작업 항목으로 묶을 수 있다.
2. **최신 기록을 맨 위**에 두고 과거 기록을 유지한다. 날짜와 시간을 아는 경우 `YYYY-MM-DD HH:mm KST`로 표기한다.
3. 한 항목에 **구분**(`추가`, `수정`, `개선`, `제거`, `테스트`, `배포`, `롤백`), **변경 내용**, **영향 범위**, **검증 결과/미검증**, **관련 커밋·PR**을 남긴다. 실제 릴리스 태그가 없는 변경에 임의의 버전 번호를 부여하지 않는다.
4. **롤백해도 과거 항목을 수정하거나 지우지 않는다.** 새 날짜에 `[롤백]` 항목을 만들고, 롤백 대상 커밋·원인·되돌린 범위·현재 상태·롤백 커밋·검증 결과를 기록한다. 기능을 재도입할 때도 또 하나의 새 기록을 남긴다.
5. 과거 기록에 사실 오류를 발견한 경우 원 기록을 몰래 바꾸지 않는다. 별도의 `[기록 정정]`을 추가해 원 기록과 정정 이유를 연결한다.
6. 성공하지 못한 패치도 추적이 필요하면 `[실패/미적용]`으로 기록한다. 실제 결과가 확인되지 않은 작업은 `미검증`으로 표시한다.
7. README에는 사용법과 대표 기능, **이 파일에는 누적 변경 이력**을 둔다. 로드맵의 완료 여부와 이력은 서로 대조한다.
8. 비밀번호·토큰·개인 경로·개인 영상 정보가 로그나 커밋 설명에 포함되지 않도록 확인한다.

**새 패치 작성 양식 (복사하여 최신 날짜 아래 추가)**

```markdown
### YYYY-MM-DD HH:mm KST — [추가/수정/개선/제거/테스트/배포/롤백] 제목
- 변경: 무엇을 왜 바꾸었는지
- 영향: 관련 기능/파일/기존 동작과 호환 여부
- 검증: 통과한 테스트와 아직 미검증인 사항
- 참조: 커밋 SHA / PR 번호 / 이슈 번호
- 롤백 관련(해당 시): 원래 패치·롤백 원인·현재 상태
```

---

## 3. 날짜별 패치 내역

> **소급 기록 안내:** 아래 2026-11-11~15 항목은 기존 GitHub 커밋 메시지, 최신 README 및 소스·테스트를 **2026-11-15에 소급 정리한 요약**이다. 개별 항목의 기록일은 실제 커밋 날짜(KST)이며, 모든 세부 작업을 하나씩 나열한 정식 기존 릴리스 노트는 아니다. 검증 상태는 별도 표기가 없는 한 **코드 구현 또는 CI 테스트 범위**를 의미한다. 과거 커밋 자체는 변경하지 않았다.


### 2026-11-15 — [현황/미완료] 기준 시점의 추적 항목
- **기능:** 확대 타임라인·썸네일, 컷 경계 전후 프레임 비교, 편집 프로젝트 저장/불러오기, H3 연동.
- **정확성:** 실사용 VFR 출력 통합 검증, 원본↔결과 프레임 내용 대응 심화 검증, 오디오 경계·다중 스트림·HDR 특수 사례 점검.
- **실기기:** Windows 11 RTX NVENC 및 실제 AV1/Opus/4K/HDR/고 DPI 사용성 시험.
- **유지보수:** `MainWindow.xaml.cs`에 집중된 UI·작업 로직의 단계적 분리, 초기 설계 중심 로드맵을 실제 구현 상태에 맞게 갱신.
- **배포:** 설치 EXE Git 누적 용량 최적화, 정식 릴리스 및 FFmpeg/LibVLC 라이선스 공지 점검.

> 이 문서는 **이력 보존을 위한 누적 기록**이다. 위 미완료 항목이 해결되면 해당 내용만 조용히 지우지 말고 날짜별 패치 이력에 해결 사실·관련 커밋·검증 결과를 먼저 추가한다.

### 2026-11-15 — [수정/테스트/배포] MOV 회전 메타데이터 및 FFmpeg 호환성
- MOV의 표시 회전(Display Matrix)을 고려하여 가로·세로 해상도 계산, 변환 결과 치수 검증을 수정.
- 3840×2160 원본이 표시상 세로 영상인 사례와 180° 변환 테스트를 추가.
- FFmpeg 6/7 호환성을 위해 명시적인 `-autorotate` 옵션 대신 기본 자동 회전을 사용하도록 수정.
- 최신 소스 기준 Windows 빌드·테스트 및 포터블 설치 EXE 생성 **GitHub Actions 성공**.
- 참조: [`3e424ffe`](https://github.com/jejuzipsa/VideoFrameCut/commit/3e424ffe8f73619433691dfe1129ee1d6bef1f9f), [`9f48ebef`](https://github.com/jejuzipsa/VideoFrameCut/commit/9f48ebefa90211bd104e7deace891761645e7777), [`482a965c`](https://github.com/jejuzipsa/VideoFrameCut/commit/482a965cd2d5d71fe733b79bd7642ad47c1605e0), [설치 파일 커밋 `758e6bcd`](https://github.com/jejuzipsa/VideoFrameCut/commit/758e6bcd524013d743e265c7d96257e48d7307ed).

### 2026-11-15 — [추가/수정/테스트] 상세 처리 오류창·로컬 진단 로그
- 영상 열기, 분할, 병합, 압축, FFmpeg 및 인코더 오류에 **고유 오류 코드·전체 오류 복사·로그 폴더 열기**를 추가.
- 로컬 날짜별 로그, 크기 기반 순환과 오래된 로그 정리 정책을 구현.
- NVENC 실패 후 CPU 성공 시에도 GPU 실패 원인을 별도의 `FALLBACK` 기록으로 유지하도록 수정.
- Windows 오류창/로그 및 실패 경로에 대한 자동 검증 추가.
- 참조: [`113a59f4`](https://github.com/jejuzipsa/VideoFrameCut/commit/113a59f413ca54c23f1aca353fa008ca9c38e2f4), [`7f7ed0fd`](https://github.com/jejuzipsa/VideoFrameCut/commit/7f7ed0fd4d4d587ffe7c71c1ef0929e05d1f7b7f), [`3d519e93`](https://github.com/jejuzipsa/VideoFrameCut/commit/3d519e93dbbd5b9cac63f33edbb52dca9177d3c0), [`75d6479d`](https://github.com/jejuzipsa/VideoFrameCut/commit/75d6479d5d06d1ff0f07abf1a260135e5c6214a5).

### 2026-11-15 — [수정] HEVC MP4의 `hvc1` 태그 사용
- HEVC 출력 샘플 태그를 `hev1` 대신 `hvc1`로 지정하고 결과 태그를 검증.
- Windows/Apple 계열 미리보기 호환성 개선 목적. **10비트 HDR 디코더가 없는 환경까지 해결된 것은 아님**.
- 참조: [`38860025`](https://github.com/jejuzipsa/VideoFrameCut/commit/38860025).

### 2026-11-15 — [추가/개선/테스트] HEVC 압축·회전·반전
- 원본 해상도·FPS 유지형 고화질, 최대 1080p 균형형, 최대 720p·30fps 용량형 **3개 압축 프리셋** 도입.
- 90°/180° 회전, 좌우 반전, 정지화면 변환 미리보기, MP4 저장 UI 연결.
- 회전 버튼 재클릭 시 선택 해제, 도움말 `?` 툴팁 및 다크 테마 UI 보정.
- HDR HLG/PQ 10비트·BT.2020 보존 검증과 NVENC 불일치 시 CPU x265 대체 경로 보강.
- 실제 FFmpeg 기반 픽셀 배치, 음성, HDR 메타데이터 및 세 프리셋 회귀 테스트 추가. **Windows 실제 NVENC·4K 장시간 변환은 추가 검증 대상**.
- 참조: [`dafbae74`](https://github.com/jejuzipsa/VideoFrameCut/commit/dafbae74), [`6f91d7a5`](https://github.com/jejuzipsa/VideoFrameCut/commit/6f91d7a5), [`07b541cd`](https://github.com/jejuzipsa/VideoFrameCut/commit/07b541cd), [`1376f0e3`](https://github.com/jejuzipsa/VideoFrameCut/commit/1376f0e3), [`964b4330`](https://github.com/jejuzipsa/VideoFrameCut/commit/964b4330), [`cb5d276f`](https://github.com/jejuzipsa/VideoFrameCut/commit/cb5d276f).

### 2026-11-15 — [수정/테스트] 자동 시간 분할 범위 분리
- 자동 시간 분할을 전체 영상 일괄 적용이 아닌 **'분할' 체크 구간 우선**, 체크가 없다면 현재 선택한 행에만 적용하도록 변경.
- '출력' 체크는 실제 MP4 출력 선택에만 쓰도록 분리. 선택하지 않은 구간의 ID·범위·출력 상태 보존.
- 대상 구간별 독립 분할과 Undo/Redo 회귀 테스트 보강.
- 참조: [`f06c238c`](https://github.com/jejuzipsa/VideoFrameCut/commit/f06c238c), [`fdabe6b4`](https://github.com/jejuzipsa/VideoFrameCut/commit/fdabe6b4).

### 2026-11-15 — [추가/수정] 미리보기 음량 및 레이아웃
- VLC 미리보기 우하단에 음소거 버튼과 0~100% 음량 조절 추가, 마지막 음량 기억.
- 출력 영상의 실제 음량은 변경하지 않음.
- 다크 모드 그룹 제목과 본문 겹침·잘림 문제 수정 및 WPF 검증.
- 참조: [`39eb5077`](https://github.com/jejuzipsa/VideoFrameCut/commit/39eb5077), [`e7fd4706`](https://github.com/jejuzipsa/VideoFrameCut/commit/e7fd4706).

### 2026-11-14 — [수정] Windows 파일 불러오기 경로·유니코드 대응
- 탐색기 드래그 앤 드롭, 따옴표가 포함된 전체 경로 붙여넣기, 여러 줄 경로, 유니코드 파일명 처리 보강.
- 파일 선택 창에서 이전 경로가 남는 문제와 누락된 입력을 조용히 무시하는 문제를 수정.
- 참조: [`2d8def66`](https://github.com/jejuzipsa/VideoFrameCut/commit/2d8def66), [`901ba650`](https://github.com/jejuzipsa/VideoFrameCut/commit/901ba650), [`44cc7612`](https://github.com/jejuzipsa/VideoFrameCut/commit/44cc7612), [`7f1cb1ce`](https://github.com/jejuzipsa/VideoFrameCut/commit/7f1cb1ce).

### 2026-11-14 — [추가/테스트] 여러 원본 영상 병합과 전환 효과
- 여러 원본의 선택 구간을 병합 목록에 추가하고 순서 변경·제거할 수 있는 UI 추가.
- 컷 연결, 크로스페이드, 검은 화면 페이드 아웃·인 3개 전환 방식 지원.
- 병합 결과는 공통 해상도/FPS·오디오로 정규화하며, 개별 원본 정확 컷의 정책과 구별.
- 혼합 해상도/FPS 및 무음 구간을 포함한 실제 FFmpeg 병합 테스트, 다크 UI 보정.
- 참조: [`1d7f90c4`](https://github.com/jejuzipsa/VideoFrameCut/commit/1d7f90c4), [`beb3023a`](https://github.com/jejuzipsa/VideoFrameCut/commit/beb3023a), [`d0799344`](https://github.com/jejuzipsa/VideoFrameCut/commit/d0799344), [`134a9e9f`](https://github.com/jejuzipsa/VideoFrameCut/commit/134a9e9f).

### 2026-11-14 — [추가/배포/제거] FFmpeg 자동 설치와 포터블 패키징
- 첫 실행 시 FFmpeg/FFprobe 설치 여부 확인, HTTPS 다운로드·SHA-256 검증·안전한 파일 교체, 취소·재시도 UI 추가.
- Inno Setup 기반 포터블 압축 해제 설치 EXE와 Windows CI 생성 자동화.
- **이력 보존:** 초기에는 포터블 프로그램 파일 자체를 Git에 커밋하는 경로를 시험했으나, 이후 구식 포터블 빌드 및 자동 게시 워크플로를 제거하고 설치 EXE 중심으로 변경했다. 기존 시도 기록은 유지한다.
- 현재도 설치 EXE를 `installer/output/`에 Git 커밋하는 방식이 있어 저장소 용량 관리는 후속 검토 항목.
- 참조: [`46032dbf`](https://github.com/jejuzipsa/VideoFrameCut/commit/46032dbf), [`72a09855`](https://github.com/jejuzipsa/VideoFrameCut/commit/72a09855), [`6cbeea86`](https://github.com/jejuzipsa/VideoFrameCut/commit/6cbeea86), [`96b693a4`](https://github.com/jejuzipsa/VideoFrameCut/commit/96b693a4).

### 2026-11-14 — [추가/대체] AV1/Opus 미리보기 경로 변경
- **초기 시도:** AV1/Opus 원본 미리보기가 검게 나오는 문제에 대응해 H.264/AAC 프록시 생성 기능 도입.
- **이후 대체:** LibVLCSharp WPF 내장 플레이어를 적용해 프록시 없이 원본 AV1/Opus 등을 재생하도록 변경. 과거 프록시 서비스 파일 일부는 남아 있으나 현재 UI 기본 재생 경로는 LibVLC다.
- 기존 영상 편집/재생 컨트롤을 유지하고 네이티브 VLC DLL 및 코덱 플러그인 배포 검증 추가.
- **기록 유지:** 프록시 방식은 실제 도입 후 다른 구현으로 대체된 이력이므로 삭제하지 않는다.
- 참조: [`36ea9229`](https://github.com/jejuzipsa/VideoFrameCut/commit/36ea9229), [`ec84bdad`](https://github.com/jejuzipsa/VideoFrameCut/commit/ec84bdad), [`c34db879`](https://github.com/jejuzipsa/VideoFrameCut/commit/c34db879), [`fb9ba0d4`](https://github.com/jejuzipsa/VideoFrameCut/commit/fb9ba0d4).

### 2026-11-14 — [추가] 아이콘 및 Windows 패키징 보완
- 필름·가위 아이콘을 EXE/창에 적용. WPF에서 표시 가능한 PNG 창 아이콘과 ICO 실행 파일 리소스를 구분.
- 불필요한 아키텍처 VLC 네이티브 바이너리를 배제하고 x64 디코더 플러그인 검사.
- 참조: [`75bdbc06`](https://github.com/jejuzipsa/VideoFrameCut/commit/75bdbc06), [`0755b1dd`](https://github.com/jejuzipsa/VideoFrameCut/commit/0755b1dd), [`dd32454b`](https://github.com/jejuzipsa/VideoFrameCut/commit/dd32454b).

### 2026-11-13 — [추가/수정] 여러 영상 목록과 재생 구간 제어
- 여러 원본 영상 불러오기, 소스 목록·구간 목록 분리, 선택 구간 시작~끝 범위 재생 및 자동 정지.
- 영상 아래 7개 재생 버튼, ±3초/±1프레임 탐색, 슬라이더 클릭 위치 이동 및 재생 상태 수정.
- 선택된 인접 분할 구간을 다시 합치는 기능 추가. 삭제로 떨어진 원본 범위를 임의로 병합하지 않도록 보호.
- 참조: [`3ae6ed91`](https://github.com/jejuzipsa/VideoFrameCut/commit/3ae6ed91), [`b85e4760`](https://github.com/jejuzipsa/VideoFrameCut/commit/b85e4760), [`f0fab867`](https://github.com/jejuzipsa/VideoFrameCut/commit/f0fab867), [`1acc403b`](https://github.com/jejuzipsa/VideoFrameCut/commit/1acc403b).

### 2026-11-13 — [개선/테스트] 전체 다크 테마
- DataGrid·헤더·입력 컨트롤에 일관된 고대비 WPF 다크 테마를 적용.
- Windows DWM 다크 제목 표시 및 테마 리소스 스모크 테스트 보강.
- 참조: [`36a5fa22`](https://github.com/jejuzipsa/VideoFrameCut/commit/36a5fa22), [`b97cc2db`](https://github.com/jejuzipsa/VideoFrameCut/commit/b97cc2db), [`ab96f1d7`](https://github.com/jejuzipsa/VideoFrameCut/commit/ab96f1d7).

### 2026-11-11 — [초기 구현/테스트] 프레임 기반 편집 엔진 및 Windows 앱
- 저장소·요구사항·구조 문서와 LosslessCut/반디컷 비교 자료 작성.
- 원본 표시 프레임 인덱스, 비파괴 구간 분할/삭제/출력 선택, Undo/Redo, 시간 분할 로직 구현.
- FFprobe 분석, FFmpeg 프레임 범위 출력 및 프레임 수 검증, CLI, 초기 WPF 다크 GUI 연결.
- 번호가 있는 합성 프레임, H.264 키프레임 밖 컷, CFR 출력 회귀 테스트 및 Windows CI 구성.
- VFR 출력/PTS 관련 여러 보정과 테스트를 진행했지만 **실제 VFR 통합 출력 검증은 통과 처리하지 않고 보류**. FFmpeg 버전별 샘플 생성·출력 동작 차이가 남아 있다.
- 참조: [초기 커밋 `61551b52`](https://github.com/jejuzipsa/VideoFrameCut/commit/61551b52), [Core `6b2749e7`](https://github.com/jejuzipsa/VideoFrameCut/commit/6b2749e7), [Media/CLI `4d9f46cf`](https://github.com/jejuzipsa/VideoFrameCut/commit/4d9f46cf), [WPF `63277ec2`](https://github.com/jejuzipsa/VideoFrameCut/commit/63277ec2), [VFR 보류 `e10dcddb`](https://github.com/jejuzipsa/VideoFrameCut/commit/e10dcddb).

---
