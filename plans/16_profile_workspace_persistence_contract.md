# 16 — Profile / Workspace Persistence Contract (Alpha Redraft)

Purpose: Profile options and workspace UI persistence contract outside gameplay save slots.
Keywords: profile persistence, workspace persistence, TOML profile, options state, workspace UI state, hydrate, sanitize, pane state
Aliases: profile/workspace save, UI persistence

- 문서 상태: REDRAFT
- 문서 계층: Tier 1 SSOT
- 대상 엔진: Godot 4.6 / C#
- 타겟 플랫폼: PC / Windows
- 문서 목적: **save slot과 분리된 사용자 전역 profile/options 및 NEXUS Shell workspace UI 상태의 저장/복원 계약**을 정의한다.

> 관련 문서
> - save slot persistence: `12`
> - workspace 상태 의미 계약: `13`
> - 공식 프로그램 계약 (`nexus_shell` 포함): `14`
> - 플레이어 여정 / shell 도입 흐름: `15`

---

## 0. 범위와 핵심 결정

### 0.1 본 문서가 다루는 것
- save slot과 분리된 profile/workspace 저장 파일의 포맷
- `OptionsState` 저장 범위와 기본 구조
- `WorkspaceUiState` 저장 범위와 기본 구조
- TOML 직렬화 구조
- pane-local state 위임 규칙
- load 시 sanitize / hydrate / fallback 규칙
- 현재 save에서 사용 불가능한 pane을 처리하는 규칙
- reset / 버전 / 호환성 정책

### 0.2 본 문서가 다루지 않는 것
- 게임 진행 상태 저장 포맷
- 서버/프로세스/로그/세션/시나리오 플래그 저장 포맷
- 개별 pane의 세부 기능 규칙 자체
- 개별 명령어/system call 계약
- 개별 프로그램의 실행 계약

### 0.3 핵심 결정 요약
1. 게임 진행 상태(save slot)와 사용자 취향(profile/workspace)은 완전히 분리한다.
2. profile/workspace persistence는 **사용자 전역 상태**이며 save slot과 독립적으로 저장된다.
3. 저장 포맷은 사람이 읽고 편집 가능한 **TOML**을 사용한다.
4. 저장 파일은 **엔진이 소유하는 config 파일**이며, 사람이 수정 가능하더라도 문서상 canonical owner는 엔진이다.
5. `OptionsState`와 `WorkspaceUiState`는 같은 TOML 파일에 공존할 수 있으나, 논리적으로는 별도 상태 영역이다.
6. `WorkspaceUiState`는 사용자 취향을 저장하지만, 현재 save에서의 gameplay entitlement(보유/해금/접근 가능 여부)을 우회해서는 안 된다.
7. 저장된 pane 취향은 유지할 수 있으나, load 시에는 현재 save의 pane availability를 기준으로 sanitize 후 적용한다.
8. `FocusedPane`은 저장하지 않는다. workspace 시작 상태의 focus는 `none`이며, 이후 사용자 상호작용으로 결정된다.
9. pane-local state는 `workspace.pane_state.<WindowKind>` TOML table에 저장하며, 내부 키의 의미는 pane-specific restore/save 훅에 위임한다.
10. `pane_state` table은 optional이며, 비어 있거나 아예 없어도 유효해야 한다.

---

## 1. 12 / 13 / 16 문서 경계

### 1.1 12가 소유하는 것
`12`는 아래만 소유한다.
- 월드 진행 상태
- 서버 mutable state
- 프로세스/로그/플래그
- 로컬 워크스테이션 VFS 상태
- shell 사용 가능 여부를 유도할 수 있는 world facts

### 1.2 13이 소유하는 것
`13`는 아래 의미 규칙을 소유한다.
- `WorkspaceMode`
- `MaximizedPane`
- dock slot 구조
- `DockStack`
- `ActiveDockPane`
- `PinnedSet`
- `HomeSlot`
- pane 타입 분류
- taskbar state 의미

### 1.3 16이 소유하는 것
본 문서는 아래를 소유한다.
- `OptionsState` 저장 구조
- `WorkspaceUiState` 저장 구조
- TOML 테이블/키 구조
- load 시 sanitize / hydrate 절차
- pane-local state 위임 저장 위치
- invalid/unavailable pane fallback 규칙
- reset / versioning / compatibility 정책

---

## 2. 설계 원칙

### 2.1 사용자 취향 vs 게임 권한
Profile/workspace persistence는 **사용자 취향(user preference)** 을 저장한다.
하지만 **gameplay entitlement** 를 저장하지 않는다.

예시:
- 브라우저 pane을 pin하고 싶다는 취향은 저장할 수 있다.
- 그러나 현재 save에서 브라우저 프로그램을 아직 보유하지 않았다면, 그 pane을 실제로 열어서는 안 된다.

### 2.2 저장 상태와 실제 적용 상태의 분리
본 문서는 아래 두 상태를 구분한다.

- **Stored State**: TOML 파일에 저장된 사용자 취향
- **Effective State**: 현재 save의 availability / capability를 반영해 실제로 적용된 workspace 상태

Stored State는 current save에서 일시적으로 적용 불가능할 수 있다.
이 경우 엔진은 sanitize 규칙을 통해 Effective State를 계산한다.

### 2.3 사람이 읽을 수 있는 저장 포맷
profile/workspace persistence는 save slot과 달리 tamper-evident 바이너리를 요구하지 않는다.
대신 사람이 읽고 편집할 수 있는 TOML을 사용한다.

### 2.4 pane-local state 위임
개별 pane이 저장하고 싶은 UI 상태(탭, 필터, zoom 등)는 본 문서가 직접 필드 의미를 소유하지 않는다.
대신 `workspace.pane_state.<WindowKind>` 위치만 본 문서가 정의하고, 내부 키는 pane-specific save/restore 훅에 위임한다.

---

## 3. 저장 파일 / 위치 / 포맷

### 3.1 기본 저장 파일
알파 기본 저장 파일은 아래 1개를 권장한다.

- `user://profile.toml`

동일 파일 안에 아래 두 논리 영역을 저장한다.
- `OptionsState`
- `WorkspaceUiState`

### 3.2 추후 분리 가능성
향후 필요 시 아래처럼 파일을 물리적으로 분리할 수 있다.
- `user://options.toml`
- `user://workspace.toml`

그러나 알파에서는 단일 `profile.toml` 권장을 기본으로 한다.

### 3.3 포맷 규칙
- 포맷은 TOML 1.0 호환을 목표로 한다.
- 엔진이 다시 저장(write-back)할 때도 안정적으로 유지 가능한 구조를 사용한다.
- 중첩 깊이는 과도하게 깊어지지 않도록 제한한다.
- 알 수 없는 키는 가능한 한 보존하지 않아도 된다. canonical owner는 엔진이다.

---

## 4. OptionsState 계약

### 4.1 목적
`OptionsState`는 save slot과 무관한 전역 사용자 설정을 저장한다.

### 4.2 알파에서의 구체 키
알파 시점에는 게임 옵션의 구체 키 목록이 확정되지 않았다.
따라서 본 문서는 **구조와 카테고리**만 정의한다.

### 4.3 reserved 카테고리
알파 기준 reserved 카테고리는 아래와 같다.
- `audio`
- `display`
- `input`
- `accessibility`
- `ui`

### 4.4 저장 규칙
- 각 카테고리는 TOML table로 저장한다.
- 카테고리 내부의 구체 key/value는 추후 확정 가능하다.
- 미지원 키는 load 시 무시 가능해야 한다.
- 카테고리 table이 비어 있어도 유효하다.

### 4.5 예시 구조
```toml
[options.audio]
# reserved

[options.display]
# reserved

[options.input]
# reserved

[options.accessibility]
# reserved

[options.ui]
# reserved
```

---

## 5. WorkspaceUiState 계약

### 5.1 목적
`WorkspaceUiState`는 NEXUS Shell workspace의 전역 UI 취향과 배치 상태를 저장한다.

### 5.2 저장 대상
알파 기준 canonical 저장 대상은 아래와 같다.

#### A. workspace 전역 레이아웃
- 좌/우 split 비율
- 우측 상/하 split 비율
- `WorkspaceMode`
- `MaximizedPane`

#### B. pane 배치 상태
- `DockStackBySlot`
- `ActiveDockPaneBySlot`
- `PinnedSet`

#### C. pane-local state
- `workspace.pane_state.<WindowKind>`
- pane별 UI 세부 상태(opaque table)

### 5.3 저장 제외 대상
아래는 profile/workspace persistence에서도 저장하지 않는다.
- `FocusedPane`
- Toast 상태/히스토리
- Activity Popup 상태/히스토리
- Settings 열림 여부
- 현재 입력 중인 command line 내용
- 터미널 스크롤백
- 명령 히스토리
- 실행 중 프로그램 상태
- SSH 세션 / 연결 상태
- 에디터 임시 버퍼 / unsaved buffer
- gameplay object의 live selection 상태
- 브라우저 현재 URL / 세션 컨텍스트

### 5.4 `FocusedPane` 규칙
- `FocusedPane`은 저장하지 않는다.
- hydrate 직후 시작 focus는 `none`으로 간주한다.
- 이후 사용자 클릭/입력에 의해 focus를 획득한다.

---

## 6. TOML 구조 (canonical layout)

### 6.1 최상위 구조
권장 TOML 구조는 아래와 같다.

```toml
[meta]
version = 1

[options]
# categories only

[workspace]
mode = "DOCKED"
maximized_pane = ""

[workspace.split]
left_ratio = 0.42
right_top_ratio = 0.55

[workspace.pins]
kinds = ["TERMINAL", "WORLD_MAP_TRACE", "MAIL"]

[workspace.slots.LEFT]
stack = ["TERMINAL", "WEB_VIEWER", "CODE_EDITOR"]
active = "TERMINAL"

[workspace.slots.RIGHT_TOP]
stack = ["WORLD_MAP_TRACE"]
active = "WORLD_MAP_TRACE"

[workspace.slots.RIGHT_BOTTOM]
stack = ["MAIL"]
active = "MAIL"

[workspace.pane_state.WORLD_MAP_TRACE]
schema = 1
# pane-local keys
```

### 6.2 `meta`
- `version: int`
  - profile/workspace TOML schema version

### 6.3 `workspace.mode`
- 값: `"DOCKED" | "MAXIMIZED"`
- invalid 값은 load 시 `"DOCKED"` fallback

### 6.4 `workspace.maximized_pane`
- 값: `WindowKind string` 또는 빈 문자열
- 빈 문자열은 `none` 의미
- invalid/unavailable pane이면 sanitize 후 `none`

### 6.5 `workspace.split`
- `left_ratio: float`
  - 좌/우 column 비율
- `right_top_ratio: float`
  - 우측 영역에서 상단 pane 비율

유효 범위는 엔진에서 clamp한다.

### 6.6 `workspace.pins.kinds`
- pinned pane kind 배열
- 순서는 taskbar 정렬 규칙에 사용할 수 있다
- duplicate는 제거하며 첫 등장 순서를 유지한다
- 이미 pinned인 pane에 다시 pin을 수행해도 기존 순서를 바꾸지 않는다

### 6.7 `workspace.slots.<SLOT>`
각 slot은 아래 2개 필드를 가진다.
- `stack: array<string>`
- `active: string`

알파에서 유효한 slot 이름은 아래 3개다.
- `LEFT`
- `RIGHT_TOP`
- `RIGHT_BOTTOM`

### 6.8 `workspace.pane_state.<WindowKind>`
- pane-local opaque table
- optional table
- table이 없어도 유효
- 빈 table이어도 유효
- 내부 키는 pane-specific save/restore 훅이 소유
- `schema` 필드를 둘 수 있으며 권장한다

예:
```toml
[workspace.pane_state.WORLD_MAP_TRACE]
schema = 1
active_tab = "map"
filter_hot = true
filter_forensic = true
filter_lock_on = true

[workspace.pane_state.MAIL]
schema = 1
# empty state allowed
```

---

## 7. canonical vs derived state

### 7.1 canonical persisted state
아래는 TOML에 canonical로 저장한다.
- `workspace.mode`
- `workspace.maximized_pane`
- split ratios
- `PinnedSet`
- `DockStackBySlot`
- `ActiveDockPaneBySlot`
- `pane_state`

### 7.2 derived runtime state
아래는 저장하지 않고 hydrate 시 재구성한다.
- `FocusedPane`
- `CurrentDockSlotByKind`
- pane availability cache
- empty slot placeholder state

### 7.3 `CurrentDockSlotByKind`
`CurrentDockSlotByKind`는 `DockStackBySlot`로부터 유도 가능한 runtime map이다.
따라서 TOML에는 저장하지 않는다.

---

## 8. pane-local state 위임 계약

### 8.1 목적
각 pane은 자신의 로컬 UI 상태를 저장/복원할 수 있어야 한다.
그러나 그 내부 필드 의미를 본 문서가 직접 소유하지는 않는다.

### 8.2 저장 위치
pane-local state는 아래 위치에 저장한다.
- `workspace.pane_state.<WindowKind>`

### 8.3 저장 규칙
- pane-specific save 훅은 TOML table 또는 key/value map을 반환할 수 있다.
- 상태가 없으면:
  - table 자체를 생성하지 않아도 된다
  - 또는 빈 table을 반환해도 된다
- pane-specific state는 gameplay entitlement를 우회하는 정보를 저장해서는 안 된다.

### 8.4 복원 규칙
- load 시 엔진은 각 available pane에 대해 해당 table을 pane-specific restore 훅에 전달한다.
- table이 없으면 기본 UI 상태로 초기화한다.
- table이 존재하지만 복원 실패 시:
  - 해당 pane의 로컬 상태만 default fallback
  - 전체 workspace load는 실패시키지 않는다

### 8.5 권장 저장 항목
권장:
- 현재 탭
- 필터 토글
- zoom
- 보기 모드
- 정렬 방식
- 펼침/접힘 상태

비권장:
- 현재 선택 중인 gameplay object id
- live session context
- 임시 버퍼
- 연결 상태
- 실행 중 작업 상태

---

## 9. pane availability / entitlement sanitize 규칙

### 9.1 availability 판정 원칙
어떤 pane이 현재 save에서 실제로 사용 가능한지는 **현재 게임 진행 상태**가 결정한다.
예:
- 특정 프로그램을 아직 보유하지 않으면 pane unavailable
- shell workspace가 아직 열 수 없는 상태면 shell pane unavailable

단, 개발용 `DebugOption=true` 런타임에서는 **현재 renderer slice에서 이미 구현된 pane만 예외적으로 available 처리**할 수 있다.
이 예외는 개발 편의 목적의 debug-only override이며, 미구현 pane이나 release 기준 entitlement 규칙을 대체하지 않는다.

### 9.2 저장된 취향의 유지
Stored State에는 unavailable pane 관련 취향이 남아 있어도 된다.
예:
- pinned preference
- last known dock stack membership
- pane-local state

### 9.3 적용 시 sanitize
hydrate 시 엔진은 current save 기준으로 availability를 계산한 뒤 Effective State를 만든다.

sanitize 규칙:
1. unavailable pane은 visible/resident layout에서 제외한다.
2. 같은 pane이 여러 slot/stack에 중복 저장돼 있으면, `LEFT -> RIGHT_TOP -> RIGHT_BOTTOM` 순서와 각 stack 내부 순서를 기준으로 **첫 등장만 유지**하고 나머지는 제거한다.
3. 어떤 slot의 `active`가 unavailable이거나 sanitize 후 stack에 존재하지 않으면, 해당 slot의 **첫 eligible pane**으로 fallback 시도한다.
4. fallback 가능한 pane이 없으면 empty slot을 표시한다.
5. `workspace.maximized_pane`이 unavailable이면:
   - `workspace.mode = DOCKED`
   - `maximized_pane = none`
6. `PinnedSet`의 unavailable pane은 **stored preference 및 effective pinned preference로는 유지 가능**하나, 현재 taskbar에는 표시하지 않는다.
7. taskbar에서 pinned가 아닌 resident pane은 `LEFT -> RIGHT_TOP -> RIGHT_BOTTOM` 순서와 각 slot의 `DockStack` 순서대로 뒤에 붙는다.
8. pane-local state table은 unavailable pane에 대해 load 시 무시할 수 있다. 저장된 table 자체는 삭제하지 않아도 된다.

### 9.4 mid-session capability gain
게임 도중 어떤 pane이 newly available이 되어도, 엔진은 그 pane을 자동으로 열 필요가 없다.
권장 규칙:
- availability만 갱신
- pinned preference가 있으면 taskbar 노출 가능
- resident/open 복원은 다음 hydrate 시점 또는 명시적 open 시점에만 수행

---

## 10. hydrate / save 절차 계약

### 10.1 저장 절차
1. 현재 options state를 수집
2. 현재 workspace canonical state를 수집
3. 각 available pane의 pane-local state를 위임 수집
4. TOML 구조를 생성
5. `FocusedPane` 및 기타 비저장 상태는 기록하지 않음
6. 파일에 원자적 쓰기(권장: temp -> replace)

### 10.2 로드 절차
1. TOML 파일 존재 여부 확인
2. 없으면 default profile/workspace state 사용
3. TOML parse
4. version 검증
5. options load
6. workspace canonical state load
7. current save 기준 availability 계산
8. sanitize 후 Effective WorkspaceState 계산
9. available pane에 한해 pane-local restore 위임
10. hydrate 완료 후 `FocusedPane = none`

### 10.3 save slot 로드와의 관계
save slot 로드 후 workspace hydrate는 별도 단계로 수행한다.
권장 순서:
1. save slot load (`12`)
2. shell capability / pane availability 계산
3. profile/workspace TOML hydrate (`16`)
4. effective workspace 적용

---

## 11. default / fallback 규칙

### 11.1 파일 없음
profile TOML이 없으면 아래 기본값을 사용한다.
- options: 전부 기본값
- workspace mode: `DOCKED`
- `maximized_pane = none`
- split ratios: 엔진 default
- pins: 엔진 default
- slots: `HomeSlot` 기반 기본 배치
- pane_state: 없음
- `FocusedPane = none`

### 11.2 invalid key / invalid table
- unknown top-level key: 무시 가능
- invalid enum/string: default fallback
- invalid slot name: 무시
- duplicate pane across multiple stacks: sanitize 시 첫 등장만 유지하고 나머지 제거
- `active`가 stack에 없는 경우: stack의 첫 eligible pane으로 fallback, 없으면 empty slot

### 11.3 invalid `maximized_pane`
다음 경우는 `maximized_pane = none`, `mode = DOCKED` fallback이다.
- unknown kind
- unavailable pane
- stack 어디에도 존재하지 않는 pane
- pane 생성 실패

---

## 12. reset 정책

### 12.1 Reset Options
- options만 기본값으로 초기화
- workspace layout은 유지

### 12.2 Reset Workspace Layout
- workspace canonical state만 초기화
- options는 유지
- pane-local state도 함께 제거 가능하며, 알파 기본은 제거 권장

### 12.3 Full Reset
- options + workspace 둘 다 초기화
- save slot 데이터에는 영향 없음

---

## 13. 버전 / 호환성 정책

### 13.1 `meta.version`
- breaking change 시 증가
- 로더는 너무 오래된 버전이면 default fallback 가능

### 13.2 pane-local state schema
- pane-local state table은 자체 `schema`를 가질 수 있다.
- pane-specific restore 훅이 schema upgrade를 담당할 수 있다.
- pane-local state schema mismatch는 전체 load 실패 사유가 아니다.

### 13.3 forward tolerance
- unknown pane-local keys는 pane-specific restore에서 무시 가능해야 한다.
- 알파에서는 가능한 한 tolerant load를 기본으로 한다.

---

## 14. 수용 기준

### 14.1 기본 파일/복원
- `profile.toml`이 없을 때 엔진 default workspace로 정상 시작한다.
- TOML이 존재하면 split, pins, slot stack, active pane, mode, maximized pane이 복원된다.

### 14.2 Focus 비저장
- 저장 시 `FocusedPane`은 기록되지 않는다.
- load 직후 focus는 `none` 상태다.

### 14.3 sanitize
- unavailable pane은 현재 workspace에 표시되지 않는다.
- `active` pane unavailable 시 fallback 또는 empty slot이 동작한다.
- invalid `maximized_pane`이면 `DOCKED` fallback이 적용된다.
- unavailable pinned pane은 taskbar에 표시되지 않는다.

### 14.4 pane-local state
- pane-local state table이 없어도 load가 성공한다.
- 빈 table이어도 load가 성공한다.
- pane-local restore 실패 시 해당 pane만 default state로 복원되고 전체 workspace load는 유지된다.

### 14.5 reset
- options reset, workspace reset, full reset이 서로 독립적으로 동작한다.
- reset은 save slot 데이터에 영향을 주지 않는다.

---

## 15. 구현 체크리스트

- [ ] `user://profile.toml` 저장/로드 IO 추가
- [ ] TOML parse / serialize 계층 추가
- [ ] `OptionsState` 구조 추가
- [ ] `WorkspaceUiState` 구조 추가
- [ ] `DockStackBySlot` / `ActiveDockPaneBySlot` hydrate 구현
- [ ] `PinnedSet` hydrate 구현
- [ ] split ratio hydrate + clamp 구현
- [ ] `MaximizedPane` sanitize 구현
- [ ] `FocusedPane` 비저장 규칙 반영
- [ ] pane availability sanitize 구현
- [ ] pane-local save/restore delegation API 추가
- [ ] reset options / reset layout / full reset 구현

