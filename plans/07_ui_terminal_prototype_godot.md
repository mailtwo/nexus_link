# UI 기획 문서: 터미널 기반 프로토타입 (Godot / PC)

목표: **가상 리눅스 터미널 1개 화면**만으로 “서버 1대 침투 시나리오”를 빠르게 검증하는 MVP를 만든다.  
플랫폼: **PC 전용(Windows)**.  
제약: **실시간 Trace/추적 대응 루프 UI는 알파 버전 구현 범위**, **실제 OS/네트워크 접근 없음(전부 가상)**.

---

## 1) MVP UX 목표

- 화면은 **리눅스 터미널처럼** 보인다(모노스페이스, 프롬프트, 스크롤백).
- 유저는 명령어를 입력해 **가상 파일 시스템(VFS)**과 **가상 네트워크**를 다룬다.
- 코딩(프로그램 수정/작성)을 위해 **내장 텍스트 에디터 모드**가 필요하다.
- CLI에 익숙하지 않은 유저를 위해 **마우스 입력(클릭)**이 보조적으로 동작한다.
  - 예: `ls` 결과의 파일명을 클릭하면 `cat <file>` 또는 `edit <path>`를 입력창에 자동 채움.

---

## 2) Godot 구현 스택(추천)

### 2.1 UI 노드(기본 제공으로 충분)
- 출력(스크롤백):
  - `ScrollContainer` + `RichTextLabel`
- 입력(커맨드 라인):
  - `LineEdit` (단일 라인 입력)
- 코드/텍스트 편집기(에디터 모드):
  - `CodeEdit` (권장) 또는 `TextEdit`
  - 터미널 뷰와 에디터 뷰를 전환하는 방식(편집 중 터미널 숨김)

근거:
- `RichTextLabel`은 BBCode/서식/메타 데이터(클릭) 처리에 적합
- `LineEdit`은 터미널 프롬프트 입력에 최적
- `CodeEdit`은 라인 넘버/인덴트/코드 편집 기능을 기본 제공

> 구현 난이도/속도 관점에서 Unity TMP보다 “에디터 모드”가 더 수월할 가능성이 높다.

---

## 3) 화면 구성(씬/노드 트리)

### 3.1 메인 씬: `TerminalScene.tscn`
권장 노드 트리(예시):

```
TerminalScene (Control)
├── Background (ColorRect)                    # 검정 배경
├── VBox (VBoxContainer)                      # 상하 레이아웃
│   ├── OutputScroll (ScrollContainer)        # 스크롤백
│   │   └── Output (RichTextLabel)            # 로그 출력
│   └── InputRow (HBoxContainer)              # 프롬프트 + 입력
│       ├── Prompt (Label)                    # "player@term:~$ "
│       └── Input (LineEdit)                  # 커맨드 입력
└── EditorOverlay (Control) [hidden]          # 편집기 전용 뷰
    └── Editor (CodeEdit)                     # 멀티라인 편집기
```

편집기 모드 전환 규칙(프로토타입):
- `EditorOverlay.visible = true`일 때 `VBox`를 숨겨서 “터미널 위 오버레이”가 아니라 “터미널 대신 에디터”처럼 보이게 한다.
- 편집 중에는 `InputRow`를 비활성화하고, 터미널 출력/입력 영역은 보이지 않게 유지한다.
- 에디터 영역은 터미널 본문 영역과 동일한 크기(동일한 여백)로 맞춘다.

### 3.2 프롬프트 문자열 규칙(연출)
- 로컬 쉘:
  - `player@term:~/ $ `
- 원격(가상 서버 접속 후):
  - `player@10.0.1.20:/var/www $ `
- Root로 승격:
  - `root@10.0.1.20:/ # `

프롬프트는 `ShellState`(현재 호스트, cwd, user)에 의해 갱신.

---

## 4) 터미널 동작 규칙

### 4.1 출력(스크롤백)
- 출력은 내부적으로 “라인 리스트”로 관리한다.
- 출력 렌더 방식:
  - `RichTextLabel.append_text()` 또는 `text += ...` 중 택1
  - 스크롤백 라인 상한(예: 1000 lines) 초과 시 앞부분 삭제
- 항상 최신 출력이 보이게:
  - 출력 추가 후 `ScrollContainer.scroll_vertical = max_value` 처리

### 4.2 입력(커맨드 라인)
- Enter: 커맨드 실행
- ↑/↓: 히스토리 탐색
- Tab: 자동완성(명령어/경로)
- Ctrl+L: 화면 클리어(로그 버퍼 유지 여부는 선택)
- Focus 규칙:
  - 터미널 클릭 시 `Input.grab_focus()`로 항상 입력이 가능하도록

### 4.3 마우스(클릭 보조 UX)
`RichTextLabel`의 “메타 데이터”를 활용해 클릭 가능한 텍스트를 만든다.

예시 출력 아이디어:
- `ls` 출력에서 파일명에 `meta`를 걸어 클릭 시:
  - 단일 클릭: Input에 `cat <file>` 채우기
  - 더블 클릭: `edit <path>` 실행(선택)
- 호스트/IP에도 meta를 달아 클릭 시 `ping <ip>` 채우기

> 클릭은 “입력을 대체”가 아니라 “입력을 돕는” 용도(터미널 감성 유지).

### 4.4 명령 응답 `code` 토큰 규약(v0.2)
- `ExecuteTerminalCommand`/관련 응답 payload의 `code`는 enum text가 아니라 **표준 토큰**을 사용한다.
- 성공은 항상 `OK`.
- 실패는 항상 `ERR_*` 형식.
- 최소 포함 토큰:
  - `ERR_UNKNOWN_COMMAND`
  - `ERR_INVALID_ARGS`
  - `ERR_NOT_FOUND`
  - `ERR_TOOL_MISSING`
  - `ERR_PERMISSION_DENIED`
  - `ERR_NOT_TEXT_FILE`
  - `ERR_ALREADY_EXISTS`
  - `ERR_NOT_DIRECTORY`
  - `ERR_NOT_EMPTY`
  - `ERR_IS_DIRECTORY`
  - `ERR_PORT_CLOSED`
  - `ERR_NET_DENIED`
  - `ERR_AUTH_FAILED`
  - `ERR_RATE_LIMITED`
  - `ERR_TOO_LARGE`
  - `ERR_INTERNAL_ERROR`
- MiniScript intrinsic의 공통 에러 코드 표면은 `03_game_api_modules.md`를 따른다.  
  See DOCS_INDEX.md → 03.

---

## 5) 에디터 모드(유사 vim / 코드 편집)

### 5.1 진입/종료
- 진입: `edit <path>` 커맨드
- 종료: `Esc` (dirty 상태에서는 종료 확인 다이얼로그 표시)
- 저장: `Ctrl+S` (`SaveEditorContent` 브리지 호출)
  - editable: 저장 성공/실패 메시지를 터미널 로그에 출력하고 에디터 유지
  - read-only: 에러 메시지를 출력하고 에디터 유지

### 5.3 구현 팁
- `EditorOverlay.visible = true`일 때:
  - `Editor.grab_focus()`
  - 터미널 `VBox`를 숨기고 Input을 비활성화(입력 충돌 방지)
- 오버레이 닫을 때:
  - 변경 사항 저장 여부 처리
  - `Input.grab_focus()` 복귀
- 에디터 상단 상태 바는 사용하지 않는다.
- 도움말 바는 표시한다(현재 구현 기준):
  - `Ctrl+S`: 저장
  - `Esc`: 종료 시도
  - dirty 상태에서 종료 시 `정말 나가시겠습니까?` 확인 문구를 노출하고, 취소하면 편집을 유지한다.
- 터미널↔에디터 전환 시 이질감이 없도록 배경색/폰트/폰트 크기/기본 글자색을 동일하게 유지.
- `ExecuteTerminalCommand` 응답에 에디터 전환 메타를 포함한다:
  - `openEditor`, `editorPath`, `editorContent`, `editorReadOnly`, `editorDisplayMode`, `editorPathExists`

---

## 6) MVP 명령어 세트(최소, 상태 표기)

상태 범례:
- 표기가 없으면 현재 런타임에 시스템콜 핸들러가 등록되어 동작한다(구현됨).
- `[미구현]`: 본 문서의 목표 명령이지만 현재 핸들러가 없어 동작하지 않는다.
- `[임시-제거예정]`: 프로토타입 편의 기능이며 알파 버전에서 제거 대상이다.
- `[옵션: DEBUG]`: `DebugOption` 활성화 빌드에서만 노출된다.

### 6.1 파일/디렉토리
- `pwd`: 현재 작업 경로(`cwd`)를 출력한다.
- `ls [path]`: 디렉토리 엔트리를 출력한다. `path` 생략 시 현재 경로를 기준으로 조회한다.
- `cd <path>`: 작업 경로를 변경한다. 대상 경로는 디렉토리여야 한다.
- `cat <file>`: 텍스트/편집 가능 파일 내용을 출력한다. 디렉토리나 비텍스트 대상은 에러를 반환한다.
- `mkdir <dir>`: 디렉토리를 생성한다. 이미 같은 디렉토리가 있으면 성공으로 처리한다.
- `rm [(-r|-R|--recursive)] <path>`: 기본 모드(`rm <path>`)는 파일 삭제만 지원하며 디렉토리 대상은 에러를 반환한다. `-r` 계열 옵션을 주면 디렉토리 하위를 재귀 삭제할 수 있고, 안전성을 위해 `cwd` 자체나 `cwd`의 상위 경로는 삭제할 수 없다.
- `rmdir <dir>`: 빈 디렉토리를 삭제한다. 대상이 비어 있지 않으면 `ERR_NOT_EMPTY`를 반환하며, 안전성을 위해 `cwd` 자체나 `cwd`의 상위 경로는 삭제할 수 없다.
- `cp [(-r|-R|--recursive)] <src> <dst>`: 파일/디렉토리를 복사한다. 디렉토리 복사는 `-r`(또는 `-R`/`--recursive`) 옵션이 필요하며, 대상 파일이 있으면 기본 덮어쓰기한다.
- `mv [(-r|-R|--recursive)] <src> <dst>`: 파일/디렉토리를 이동(이름 변경 포함)한다. 디렉토리 이동은 옵션 없이 허용되며, `-r` 계열 옵션은 호환용으로 받아들이고 동작은 동일하다.

### 6.2 유틸
- `help`: 시스템콜 사용법 요약 페이지를 출력한다.
- `echo [text]`: 입력 문자열을 한 줄로 그대로 출력한다. 인자를 생략하면 빈 줄 1개를 출력하며, `-n` 같은 옵션은 별도 처리하지 않고 일반 텍스트로 취급한다.
- `clear`: 터미널 화면을 비우는 유틸 명령이다. 인자는 허용하지 않으며, 성공 시 `clearTerminal=true` 응답 메타를 통해 UI가 화면을 정리한다.

### 6.3 네트워크(가상)
- `ping [(-c) <count>] <host|ip>`: 실제 ICMP 대신 접근성 기반으로 probe를 시뮬레이션한다.
  - 터미널 프로그램 async 실행 파이프라인으로 동작하며, probe 라인은 실행 중 실시간 스트리밍된다.
  - 실행 중에는 miniscript와 동일한 공통 정책을 적용한다: 입력 제출 차단, placeholder `Program is running. Press Ctrl+C to stop.` 표시, `Ctrl+C` 중단 허용.
  - `-c` 생략 시 기본 `5`회 probe를 보낸다(`ping <host|ip>`는 `ping -c 5 <host|ip>`와 동일).
  - `count` 허용 범위는 `1..10`이다.
  - `-c 1`은 단일 probe 결과를 한 줄로 출력한다: `success` 또는 `fail(<reason>)`.
  - `-c > 1`은 probe마다 Linux 유사 포맷(`64 bytes ... icmp_seq=... ttl=64`, 시간 제외)으로 출력하고 마지막에 `packet loss` 통계를 출력한다.
  - probe 간격은 Linux 기본 동작에 맞춰 1초이며, 각 probe 시점마다 대상 서버 상태를 재평가한다.
- `connect [(-p|--port) <port>] <host|ip> <user> <password>`: 원격 서버에 SSH 접속하고 터미널 컨텍스트를 전환한다. 기본 포트는 `22`다.
  - 예시: `connect 10.0.1.20 guest guest`, `connect -p 2222 10.0.1.20 guest guest`
- `disconnect`: 현재 원격 SSH 세션을 종료하고 이전 컨텍스트(로컬 또는 이전 hop)로 복귀한다.
- `known`: 플레이어가 파악한 public 호스트/IP 목록을 표 형식으로 출력한다.
- `scan [netId]`: 인접 서버를 스캔해 인터페이스별 이웃을 출력한다. 실행 권한(`execute`)이 필요하다.
- `ftp [(-p|--port) <port>] <get|put> <pathA> [<pathB>]`: SSH 연결 상태에서 파일을 전송한다. `get`은 원격→로컬, `put`은 로컬→원격이며 기본 포트는 `21`이다.

### 6.4 코딩/프로그램
- `edit <path>`: 에디터 오버레이를 열어 파일을 편집한다. 파일이 없고 부모 경로가 유효하면 빈 버퍼로 연다.
- `[옵션: DEBUG]` `DEBUG_miniscript <script>`: 개발/검증 전용 시스템콜이다. `DebugOption`이 켜진 런타임에서만 활성화된다.
- `miniscript`, `inspect` 등 **프로그램 실행 계약(시스템콜 아님)** 은 `14_official_programs.md`를 따른다.  
  See DOCS_INDEX.md → 14.
- MiniScript 전역 `import(name, alias?)` intrinsic의 계약/탐색/오류코드는 `03_game_api_modules.md`가 SSOT다.  
  See DOCS_INDEX.md → 03.

### 6.5 프로토타입 임시 명령
- 이 명령들은 현재 프로토타입 편의 기능이며, 알파 버전에서 제거 대상이다.
- `EnablePrototypeSaveLoadSystemCalls`가 켜진 런타임에서만 노출된다.
- `[임시-제거예정]` `save [0-9]`: 현재 진행 상태를 슬롯(0~9)에 저장한다.
- `[임시-제거예정]` `load [0-9]`: 슬롯(0~9) 저장 상태를 불러오고 터미널 컨텍스트를 워크스테이션 기준으로 재정렬한다.
- 저장/불러오기 정책의 SSOT는 `12_save_load_persistence_spec_v0_1.md`를 따른다.  
  See DOCS_INDEX.md → 12.

---

## 7) 내부 구조(개발 구현 관점)

### 7.1 핵심 스크립트(클래스) 구성
- `TerminalView.gd / .cs`
  - 출력 버퍼 관리, RichText meta 생성, 클릭 이벤트 처리
- `CommandInputController.gd / .cs`
  - 입력 라인 이벤트(enter/tab/history), 단축키 처리
- `Shell.gd / .cs`
  - 현재 호스트/유저/cwd 관리, 명령 디스패치
- `VirtualFileSystem.gd / .cs`
  - 파일/권한/경로 처리
- `VirtualNetwork.gd / .cs`
  - 호스트 그래프, scan/banner/ping
- `EditorOverlay.gd / .cs`
  - 파일 열기/편집/저장/종료
- (선택) `ProgramRunner.gd / .cs`
  - 프로그램 실행, (추후) CPU/RAM 예산 연결

### 7.2 데이터 흐름
`LineEdit(text_submitted)` → `Shell.execute(command)` → (VFS/Net/Runner) → `TerminalView.print(output)`

### 7.3 입력 이벤트 매핑(권장)
- `LineEdit.text_submitted` : 엔터 실행
- `_unhandled_key_input(event)` :
  - ↑/↓, Tab, Ctrl+L 등 “전역 단축키”
- `RichTextLabel.meta_clicked(meta)` :
  - 클릭한 항목(파일명/IP 등) 처리

---

## 8) 프로토타입 성공 기준(Definition of Done)
- 단일 터미널 화면에서 `ls/cd/cat/edit/ping/connect/disconnect`가 동작한다.
- 출력 스크롤백이 자연스럽고, 클릭 보조 UX가 최소 2가지 이상 동작한다.
- 에디터에서 파일 수정→저장→결과 확인이 가능하다.
- 전체 경험이 “리눅스 터미널 같다”는 인상을 준다.

---

## 참고(공식 문서)
- RichTextLabel: https://docs.godotengine.org/en/stable/classes/class_richtextlabel.html
- LineEdit: https://docs.godotengine.org/en/stable/classes/class_lineedit.html
- TextEdit: https://docs.godotengine.org/en/stable/classes/class_textedit.html
- CodeEdit: https://docs.godotengine.org/en/stable/classes/class_codeedit.html
