# UI 기획 문서: 터미널 기반 프로토타입 (Unity)

목표: **가상 리눅스 터미널 1개 화면**만으로 “서버 1대 침투 시나리오”를 빠르게 검증하는 MVP를 만든다.  
제약: **Trace/추적 시스템 없음**, **고정된 서버 1대**, **실제 OS/네트워크 접근 없음(전부 가상)**.

---

## 1) MVP UX 목표

- 화면은 **리눅스 터미널처럼** 보인다(모노스페이스, 프롬프트, 스크롤백).
- 유저는 명령어를 입력해 **가상 파일 시스템(VFS)**과 **가상 네트워크**를 다룬다.
- 코딩(프로그램 수정/작성)을 위해 **내장 텍스트 에디터 모드**가 필요하다.
- CLI가 익숙하지 않은 유저를 위해 **마우스 입력(클릭)**이 보조적으로 동작한다.
  - 예: `ls` 결과 파일명을 클릭하면 `cat <file>` 또는 `edit <file>` 자동 입력.

---

## 2) Unity 구현 스택(추천)

### 2.1 UI 프레임워크: UGUI + TextMeshPro (TMP)
MVP 속도와 구현 난이도 관점에서 가장 현실적인 조합.

- 출력: `ScrollRect` + `TMP_Text`
- 입력: `TMP_InputField` (단일 라인)
- 에디터: `TMP_InputField` (멀티라인) 오버레이

참고(기능 근거):
- ScrollRect: 스크롤 가능한 컨테이너 제공
- TMP RichText/Link: 출력 텍스트에 링크를 심어 클릭 이벤트 처리 가능

> 주의: 입력 필드에서는 리치 텍스트를 끄고, 출력에서만 색/링크를 사용한다(IME 안정성).

---

## 3) 화면 구성(레이아웃)

### 3.1 터미널 기본 화면(단일 씬/패널)
- **상단/본문**: 출력 로그 영역 (스크롤 가능)
- **하단**: 입력 라인 (프롬프트 + 입력 필드)
- (선택) 우측/하단 작은 영역: 상태(현재 디렉토리, 연결된 호스트 등)

권장 프롬프트 예:
- 로컬 쉘: `player@term:~/ $ `
- 원격(가상 서버 접속 후): `player@target:/var/www $ `

### 3.2 에디터 모드(오버레이)
`edit <file>` 명령으로 “터미널 위에 덮는” 오버레이 UI를 연다.

- 멀티라인 `TMP_InputField` (파일 내용)
- 상단 상태바: 파일 경로, 모드(INSERT/NORMAL), RAM 예약치(선택)
- 하단 힌트: 저장/종료 단축키

에디터 MVP 단축키(권장)
- 저장: `Ctrl+S`
- 종료: `Esc` 후 확인(또는 `Ctrl+Q`)
- (선택) vim 흉내: `i`로 INSERT, `Esc`로 NORMAL, `:w`, `:q` 최소 지원

---

## 4) 터미널 동작 규칙

### 4.1 출력(스크롤백)
- 출력은 “라인 리스트”로 관리하고, 화면 렌더는 버퍼를 합쳐 `TMP_Text.text`로 세팅한다.
- 성능을 위해 스크롤백 라인 수 상한을 둔다(예: 1000 lines).
- 출력 포맷은 최대한 리눅스 느낌을 유지하되, 게임적 힌트(클릭 가능 링크)도 포함한다.

### 4.2 입력(명령 라인)
- Enter: 커맨드 실행
- ↑/↓: 히스토리 탐색
- Tab: 자동완성(파일/명령어)
- Ctrl+L: 화면 클리어(로그는 유지해도 됨)

### 4.3 마우스(클릭 보조 UX)
출력에 링크를 삽입해 클릭 이벤트로 처리한다.

예:
- `ls` 결과에서 파일명에 링크를 걸어 클릭하면:
  - 단일 클릭: 입력창에 `cat <file>` 채워넣기
  - 더블 클릭: `edit <file>` 실행(선택)

- IP/호스트명 클릭:
  - 입력창에 `ping <ip>` 또는 `ssh <host>` 채워넣기(실제 ssh 아님)

> 클릭은 “입력을 대체”가 아니라 “입력을 돕는” 방향이 좋다(터미널 감성 유지).

---

## 5) MVP 명령어 세트(최소)

### 5.1 파일/디렉토리
- `pwd`
- `ls [path]`
- `cd <path>`
- `cat <file>`
- `mkdir <dir>`
- `touch <file>`
- `rm <path>`
- `cp <src> <dst>`
- `mv <src> <dst>`

### 5.2 유틸
- `echo <text>`
- `clear`
- `help`
- `history`

### 5.3 네트워크(가상)
- `ping <host|ip>`  
  - 실제 ICMP가 아니라 “가상 연결성/지연”을 출력하는 시뮬레이션.

### 5.4 코딩/프로그램
- `edit <file>`: 에디터 오버레이 열기
- `run <file|program>`: MiniScript 프로그램 실행(가상 API만 사용)

---

## 6) 내부 구조(개발 구현 관점)

### 6.1 핵심 컴포넌트
- `TerminalView`
  - 출력 버퍼 관리, 렌더링, 링크 클릭 처리
- `CommandInputController`
  - 입력 라인, 히스토리/완성/단축키 처리
- `Shell`
  - 현재 디렉토리, 환경 변수(선택), 명령 디스패치
- `VirtualFileSystem (VFS)`
  - 파일/권한/경로 처리
- `VirtualNetwork`
  - 고정 서버 1대(호스트/포트/서비스) 시뮬레이션
- `EditorOverlay`
  - 파일 열기/편집/저장/종료
- (선택) `ProgramRunner`
  - MiniScript 실행, CPU/RAM 예산(추후 확장)

### 6.2 데이터 흐름(간단)
`InputField` → `Shell.Execute(command)` → (VFS/Net/Runner) → `TerminalView.Print(output)`

---

## 7) 입력 시스템/IME 리스크(간단 메모)

- TMP_InputField + 새 Input System 조합에서 이슈가 보고된 사례가 있으므로,
  MVP 단계에서는 **입력 검증 씬**을 먼저 만들어 한글/영문/단축키가 원하는 대로 되는지 확인한다.
- 입력 필드에는 리치 텍스트를 사용하지 않는다(출력에만 사용).

---

## 8) MVP 범위에서 “하지 않는 것”
- 실제 bash 호환/파이프(`|`), 리다이렉션(`>`), 퍼미션 완전 재현 등
- 실제 SSH/실제 네트워크 연결
- Trace/IDS/로그 탐지(후속 단계)

---

## 9) 프로토타입 성공 기준(Definition of Done)
- 단일 터미널 화면에서 `ls/cd/cat/edit/save/run/ping`이 모두 동작한다.
- 출력 스크롤백이 자연스럽고, 클릭 보조 UX가 최소 2가지 이상 동작한다.
- 에디터 모드에서 파일을 수정→저장→`run`으로 결과를 확인할 수 있다.
- 전체 경험이 “리눅스 터미널 같다”는 인상을 준다.

---

## 참고(구현 근거용 링크)
- TextMeshPro RichText: https://docs.unity3d.com/Packages/com.unity.textmeshpro@4.0/manual/RichText.html
- TextMeshPro Link: https://docs.unity3d.com/Packages/com.unity.textmeshpro@4.0/manual/RichTextLink.html
- ScrollRect: https://docs.unity3d.com/Packages/com.unity.ugui@1.0/manual/script-ScrollRect.html
- TMP_InputField API: https://docs.unity3d.com/Packages/com.unity.textmeshpro@2.0/api/TMPro.TMP_InputField.html
