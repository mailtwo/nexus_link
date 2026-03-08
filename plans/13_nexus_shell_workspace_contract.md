# 13 — NEXUS Shell Workspace Contract (Alpha Redraft)

Purpose: Shell workspace, layout, pane lifecycle, taskbar, and workspace-level UI behavior contract.
Keywords: shell workspace, dock slot, dock stack, taskbar, maximized pane, toast, activity popup, system pane, pane lifecycle
Aliases: shell UI, pane layout

- 문서 상태: REDRAFT
- 대상 엔진: Godot 4.6
- 타겟 플랫폼: PC / Windows
- 문서 목적: NEXUS Shell workspace의 레이아웃, pane 타입, 상태 모델, taskbar/start menu, toast/activity popup, settings system pane, 저장/복원 요구사항을 구현 가능한 수준으로 명문화한다.

> 관련 문서
> - 터미널 명령 / system call 계약: `07`
> - 저장/복원 경계: `12`
> - 공식 프로그램 계약: `14`
> - 플레이어 여정 / 온보딩: `15`

---

## 0. 범위와 비범위

### 0.1 본 문서가 다루는 것
- NEXUS Shell workspace의 전체 레이아웃 구조
- Docked Pane / Activity Popup / Toast / System Pane 구분
- pane focus / maximize / restore / close / pin / taskbar 규칙
- Start menu 규칙
- toast / activity popup safe zone
- workspace UI 상태와 저장 요구사항
- 알파 범위 수용 기준

### 0.2 본 문서가 다루지 않는 것
- 개별 명령어 문법 및 system call 상세
- 인게임 API 반환 구조
- save 파일 포맷 자체
- 미션 / 시나리오 내용
- 개별 pane 내부의 상세 시각 디자인, 아이콘 최종 모양, 색상 hex 값

### 0.3 비범위(알파)
- 예전 `VIRTUAL_MODE` / `NATIVE_OS` 이원 모드
- OS-level 멀티윈도우 / taskbar / Alt+Tab 제어
- modal dialog 기반 입력 UI
- 전역 단축키 체계
- 동적 slot 수 증가 / 자유 분할 레이아웃

---

## 1. 핵심 UX 원칙

1. 플레이어의 주 작업 공간은 OS 창 집합이 아니라 **단일 NEXUS Shell workspace**다.
2. 알파 workspace는 제한된 수의 고정 dock slot과 pane 전환만 지원한다.
3. gameplay 관련 텍스트/명령 입력은 modal dialog로 받지 않는다.
4. pane maximize는 pane 자체를 다른 창으로 분리하는 기능이 아니라, workspace의 **표시 상태**를 바꾸는 기능이다.
5. focus 상태는 항상 시각적으로 명확해야 한다.
6. toast / activity popup은 터미널 입력 방해를 최소화해야 한다.
7. exact icon shape, chrome 미세 디자인은 추후 조정 가능하나, 상태 전이와 상호작용 규칙은 본 문서를 따른다.

---

## 2. 알파 레이아웃 개요

### 2.1 고정 slot 구조
알파 workspace는 정확히 3개의 dock slot을 가진다.

- `LEFT`
- `RIGHT_TOP`
- `RIGHT_BOTTOM`

시각 구조는 **좌측 1열 + 우측 상/하 2분할**이다.

### 2.2 알파에서 허용하는 조절
알파에서는 아래만 허용한다.

- 좌/우 column 폭 조절
- `RIGHT_TOP` / `RIGHT_BOTTOM` 높이 비율 조절
- pane의 slot 간 재배치

알파에서는 아래를 허용하지 않는다.

- slot 수 증가
- 새로운 split 생성
- 같은 slot 내 추가 분할 생성
- 자유형 다중 column 레이아웃

### 2.3 Empty Dock Slot
어떤 slot에 표시할 eligible pane이 없으면, 그 slot은 **empty dock slot** 상태를 표시한다.
이 상태는 VSCode의 빈 에디터 영역처럼, 빈 shell/placeholder 형태로 렌더링한다.

---

## 3. 용어 정의

- **WindowKind**: pane 종류 식별자. 알파에서는 각 kind당 동시에 1개 pane만 존재할 수 있다.
- **Pane**: workspace 안에 열리는 개별 UI 객체.
- **Dock Slot**: pane이 배치될 수 있는 고정 화면 영역 (`LEFT`, `RIGHT_TOP`, `RIGHT_BOTTOM`).
- **HomeSlot**: 어떤 pane이 처음 열릴 때 기본으로 배치되는 slot. 기본값일 뿐, 현재 위치를 강제하지 않는다.
- **CurrentDockSlot**: 현재 pane이 실제로 배치된 slot.
- **Resident Pane**: 현재 workspace에 열린 상태의 pane. 화면에 보이지 않더라도 resident일 수 있다.
- **DockStack**: 특정 slot에 resident 상태로 속한 pane 목록.
- **ActiveDockPane**: `DOCKED` 상태에서 특정 slot에 실제로 표시되는 pane 1개.
- **Focused Pane**: 현재 사용자 상호작용의 대상으로 간주되는 pane.
- **Pinned Pane**: taskbar에 항상 버튼이 노출되는 pane kind.
- **WorkspaceMode**: 현재 workspace 표시 상태. `DOCKED` 또는 `MAXIMIZED`.
- **MaximizedPane**: 크게 보기 context가 저장된 pane 0개 또는 1개.
- **Open Hidden**: resident 상태지만 현재 화면에 표시되고 있지 않은 pane 상태.
- **System Pane**: 일반 docked/maximized pane 규칙을 따르지 않는 특수 pane. 알파의 대표 예시는 `Settings`다.

---

## 4. Pane 분류

알파의 pane/UI 타입은 아래 4가지다.

### 4.1 Docked Pane
workspace의 dock slot에 속하는 일반 pane.

예:
- Terminal
- Web Viewer
- Code Editor
- World Map / Network pane
- Mail / Mission / List pane

특징:
- resident 가능
- slot 이동 가능
- focus 가능
- maximize 가능
- close 가능
- taskbar에 표시 가능

### 4.2 Activity Popup
진행 중 작업이나 자동화 작업을 **읽기 전용으로 관찰**하기 위한 popup.
입력창이 아니며, 장식이자 observability UI다.

예:
- `SSH_AUTH_ACTIVITY`
- `FTP_TRANSFER_ACTIVITY`
- 향후 다른 작업 모니터 popup들

특징:
- gameplay 입력을 받지 않음
- focus 대상이 아님
- workspace의 dock layout에 속하지 않음
- windowkind당 동시에 1개만 존재
- 한 popup 안에 여러 작업 항목을 함께 표시할 수 있음

### 4.3 Toast
짧은 시스템/작업 알림.

특징:
- 비인터랙티브에 가깝지만 클릭으로 닫기 가능
- focus 대상이 아님
- dock layout에 속하지 않음

### 4.4 System Pane
일반 docked/maximized pane 규칙 밖에서 동작하는 특수 pane.

알파 범위:
- `Settings`

특징:
- `MaximizedPane` 모델에 포함되지 않음
- 열리면 전용 전체 pane 형태로 표시
- close 시 진입 직전 workspace 상태를 복원
- restore/minimize 버튼 없음

### 4.5 Dialog
알파에서는 일반 Dialog 타입을 정의하지 않는다.
Modal dialog 기반 gameplay 입력 UI는 알파 범위 밖이다.

---

## 5. WindowKind 단일 인스턴스 규칙

### 5.1 전역 규칙
알파에서는 각 `WindowKind`마다 동시에 존재 가능한 pane 인스턴스 수는 정확히 1개다.

예:
- Terminal pane 2개 동시 존재 불가
- World Map pane 2개 동시 존재 불가
- Code Editor pane 2개 동시 존재 불가

### 5.2 taskbar 표현
Taskbar는 `WindowKind` 기준으로 버튼을 노출한다.
kind당 pane이 1개뿐이므로, 사용자가 여러 인스턴스 중 무엇을 고를지 선택할 상황은 발생하지 않는다.

---

## 6. HomeSlot / 기본 배치 규칙

### 6.1 일반 원칙
`HomeSlot`은 pane이 **처음 열릴 때의 기본 slot 위치**만 의미한다.
이미 resident인 pane에는 현재 위치(`CurrentDockSlot`)를 우선한다.

### 6.2 알파 기본 HomeSlot 매핑
알파 기본값은 아래와 같다.

- `TERMINAL` -> `LEFT`
- `WEB_VIEWER` -> `LEFT`
- `CODE_EDITOR` -> `LEFT`
- `WORLD_MAP_TRACE` -> `RIGHT_TOP`
- `MAIL` -> `RIGHT_BOTTOM`
- `MISSION_PANEL` -> `RIGHT_BOTTOM`
- 기타 정보/목록 pane -> 기본적으로 `RIGHT_BOTTOM`

### 6.3 이동 가능성
모든 docked pane은 알파에서도 slot 간 이동 가능하다.
즉 `HomeSlot`은 default일 뿐이며, pane은 이후 `LEFT`, `RIGHT_TOP`, `RIGHT_BOTTOM` 중 어디로든 재배치될 수 있다.

### 6.4 이미 열려 있는 pane 재호출
이미 resident인 pane을 다시 열려고 하면 새 인스턴스를 만들지 않는다.
대신 아래를 수행한다.

- pane이 visible이면 focus 이동
- pane이 open hidden이면 해당 pane을 다시 표시 상태로 전환
- pane이 `MaximizedPane`이면 그 context로 복귀 가능

---

## 7. Focus와 시각 피드백

### 7.1 Focus 규칙
한 시점에 focus된 pane은 최대 1개다.

### 7.2 시각 피드백
focus된 pane은 아래 중 최소 1개 이상으로 명확히 표시해야 한다.

- 타이틀 색 강조
- pane border 밝기 상승
- 기타 명확한 active indicator

권장 기본안:
- inactive pane title: 회색톤
- active pane title: 강조색
- active pane border: inactive보다 약간 밝음

### 7.3 Activity Popup / Toast
Activity Popup과 Toast는 focus 대상이 아니다.

---

## 8. Start Menu

### 8.1 구조
NEXUS 시작 메뉴를 열면 위쪽으로 아래 4개 메뉴가 나타난다.

- `Save`
- `Load`
- `Settings`
- `Panels`

### 8.2 Save / Load 하위 메뉴
`Save` 또는 `Load`에 마우스를 올리면 오른쪽에 save slot 목록 및 각 slot 정보가 하위 메뉴로 나타난다.

### 8.3 Panels 하위 메뉴
`Panels`에 마우스를 올리면 현재 열 수 있는 docked pane 목록이 나타난다.
선택 시 동작은 아래와 같다.

- 해당 pane이 resident가 아니면 새로 연다
- 이미 resident이면 새 인스턴스를 만들지 않고 focus/표시 전환만 수행한다

### 8.4 Settings
`Settings`는 시작 메뉴에서 실행 가능한 `System Pane`이다.
클릭 즉시 maximized system pane 형태로 열린다.

---

## 9. Taskbar

### 9.1 목적
Taskbar는 아래를 담당한다.

- 현재 열려 있는 pane kind 목록 표시
- pinned pane kind 목록 표시
- focus 전환 / reopen 진입점 제공
- pin/unpin 토글 진입점 제공

### 9.2 taskbar 버튼 상태 모델
문서 구현상 아래 상태를 구분한다.

- `focused`
- `visible_unfocused`
- `open_hidden`
- `pinned_closed`

### 9.3 taskbar 시각 표현
UI 표시상 `visible_unfocused`와 `open_hidden`은 **동일한 시각 상태로 표현한다.**
즉 구현 기작은 다를 수 있으나, taskbar 비주얼은 구분하지 않는다.

권장 표현:
- `focused`: 강조색/채움 상태
- `visible_unfocused` + `open_hidden`: 회색 border + 흰색 텍스트
- `pinned_closed`: 회색 border + 회색 텍스트

### 9.4 클릭 동작
- `focused` 클릭: 상태 변화 없음. 단, 클릭 피드백은 제공 가능
- `visible_unfocused` 클릭: 해당 pane에 focus 부여
- `open_hidden` 클릭:
  - 해당 pane이 현재 `MaximizedPane`이면 `MAXIMIZED` 상태로 복귀하며 그 pane을 표시
  - 아니면 해당 pane을 해당 slot의 `ActiveDockPane`으로 만들고 `DOCKED` 상태에서 표시/focus
- `pinned_closed` 클릭: pane open

### 9.5 우클릭 동작
Taskbar 우클릭 메뉴는 알파에서 `Pin / Unpin`만 제공한다.
`Close`는 우클릭 메뉴에 포함하지 않는다.

### 9.6 pin 순서 규칙
taskbar의 pinned pane 순서는 **삽입 순서(insertion order)** 를 따른다.

- 이미 pinned인 pane에 다시 pin을 수행해도 순서를 바꾸지 않는다.
- unpin 시 해당 pane만 목록에서 제거한다.
- 저장/복원 시 duplicate pin은 첫 등장만 유지한다.

### 9.7 taskbar 버튼 정렬 규칙
taskbar 버튼은 아래 순서로 노출한다.

1. pinned pane들을 pinned 순서대로 먼저 표시
2. 그 다음 resident이지만 pinned가 아닌 pane들을 `LEFT -> RIGHT_TOP -> RIGHT_BOTTOM` 순서로 추가
3. 각 slot 내부에서는 해당 `DockStack` 순서를 유지

즉 resident pane이더라도 pinned pane보다 앞에 오지 않는다.

### 9.8 Pin/Unpin 피드백
pin 상태가 바뀌면 해당 taskbar 버튼 이름 영역의 오른쪽 위에 잠금/해제 아이콘을 짧게 표시한다.

- 아이콘 고정 표시: 0.5초
- 이후 fade out: 0.5초

### 9.9 direct-shell bootstrap focus
개발용 direct-shell bootstrap으로 workspace가 바로 열릴 때, 초기 focus 기본값은 `TERMINAL`이다.
이 focus는 저장 상태가 아니라 runtime 기본값이다.

---

## 10. Toast

### 10.1 수명
Toast는 아래에서 위로 쌓인다.

- 표시 시간: 5초
- fade out: 1초

### 10.2 닫기
Toast는 클릭으로 즉시 닫을 수 있다.
어떤 toast가 닫히면, 그 위의 toast들은 아래로 내려와 빈 공간을 메운다.

### 10.3 safe zone
Toast는 가능하면 터미널 입력을 가리지 않도록 배치한다.

기본 원칙:
1. 터미널이 visible한 경우, 터미널이 없는 column 방향의 하단을 우선 사용
2. 터미널이 없거나 특별히 가릴 영역이 없으면 우하단을 기본값으로 사용

알파에서는 safe zone 기준을 **터미널 우선**으로 둔다.
향후 다른 입력 pane을 더 정교하게 고려하는 규칙은 추후 확장 가능하다.

---

## 11. Activity Popup

### 11.1 목적
Activity Popup은 실행 중 작업이 실제로 돌아가고 있음을 보여주는 관찰용 UI다.
입력창이 아니며, gameplay 조작을 요구하지 않는다.

### 11.2 네이밍 규칙
Activity Popup은 windowkind 이름 끝에 `_ACTIVITY`를 붙인다.

알파 명시 예:
- `SSH_AUTH_ACTIVITY`
- `FTP_TRANSFER_ACTIVITY`

### 11.3 인스턴스 규칙
windowkind당 동시에 1개만 존재할 수 있다.
다만 하나의 popup 안에서 여러 개의 시도/작업 항목을 함께 표시할 수 있다.

예:
- 여러 SSH 시도 로그를 하나의 `SSH_AUTH_ACTIVITY` 안에 표시
- 여러 파일 전송 항목을 하나의 `FTP_TRANSFER_ACTIVITY` 안에 표시

### 11.4 수명
작업이 끝난 뒤 3초간 유지한 후 자동으로 닫는다.

### 11.5 개수 제한
동시에 보이는 activity popup의 총 개수는 알파에서 하드 제한을 두지 않는다.
단, windowkind 중복 생성은 허용하지 않는다.

### 11.6 safe zone
Activity Popup은 가능한 한 터미널 입력을 가리지 않도록, 터미널이 없는 column 영역의 하단 절반 정도를 safe zone으로 사용한다.
기본 fallback은 우측 하단 영역이다.

---

## 12. Docked Pane 상태 모델

### 12.1 DockStack
각 dock slot은 `DockStack`(pane list)을 가진다.
`DockStack`에 포함된 pane은 해당 slot에 resident 상태로 열린 pane이다.

### 12.2 ActiveDockPane
각 dock slot은 `ActiveDockPane`(pane 1개)를 가진다.
`DOCKED` 상태에서는 각 slot의 `ActiveDockPane`만 화면에 표시된다.

### 12.3 Resident / Visible / Hidden
pane 상태는 최소한 아래를 구분한다.

- resident + visible
- resident + open hidden
- closed

### 12.4 Empty slot
현재 표시 가능한 eligible pane이 없으면 empty dock slot을 표시한다.

---

## 13. WorkspaceMode와 MaximizedPane

### 13.1 WorkspaceMode
Workspace는 현재 표시 상태로 아래 둘 중 하나를 가진다.

- `DOCKED`
- `MAXIMIZED`

### 13.2 MaximizedPane
Workspace는 별도로 `MaximizedPane`을 0개 또는 1개 저장할 수 있다.
`MaximizedPane`은 현재 크게 보기 context가 저장된 pane을 뜻한다.

중요:
- `WorkspaceMode = DOCKED`여도 `MaximizedPane != null`일 수 있다.
- 즉 현재는 docked 화면을 보고 있지만, 나중에 taskbar를 통해 다시 크게 볼 pane context가 저장될 수 있다.

### 13.3 MAXIMIZED 표시 규칙
`MAXIMIZED` 상태에서는 `MaximizedPane`에 등록된 pane 1개만 전체 화면 형태로 표시한다.

### 13.4 DOCKED 표시 규칙
`DOCKED` 상태에서는 각 slot의 `ActiveDockPane`를 표시한다.
단, `MaximizedPane`이 존재하는 경우 그 pane은 docked rendering 대상에서 제외한다.

### 13.5 maximize 동작
pane의 maximize 버튼을 누르면 아래를 수행한다.

1. 해당 pane을 `MaximizedPane`으로 등록
2. `WorkspaceMode = MAXIMIZED`
3. 해당 pane 표시

maximize는 pane의 open/close 상태를 바꾸는 것이 아니라, pane의 표시 context를 저장하고 workspace의 현재 표시 상태를 바꾸는 동작이다.

### 13.6 taskbar와 maximized 전환
`MAXIMIZED` 상태에서 taskbar를 통해 pane을 선택했을 때:

- 선택한 pane이 현재 `MaximizedPane`이면, 그대로 `MAXIMIZED` 상태 유지
- 선택한 pane이 현재 `MaximizedPane`이 아니면,
  - `WorkspaceMode = DOCKED`
  - 선택한 pane이 속한 slot의 `ActiveDockPane`으로 focus
  - 기존 `MaximizedPane`은 제거하지 않고 유지

즉 사용자는
- maximized pane context를 보존한 채
- 일시적으로 docked pane들을 보고
- 다시 taskbar로 maximized pane context로 복귀할 수 있다.

### 13.7 restore 동작
현재 표시 중인 `MaximizedPane`의 restore 버튼을 누르면 아래를 수행한다.

1. `WorkspaceMode = DOCKED`
2. `MaximizedPane = null`

restore는 단순히 현재 화면만 바꾸는 것이 아니라, 저장된 maximized context 자체를 해제한다.

### 13.8 maximize 교체
다른 pane의 maximize 버튼을 누르면 기존 `MaximizedPane`은 새 pane으로 교체된다.

---

## 14. DOCKED 상태에서 MaximizedPane 제외 규칙

### 14.1 제외 규칙
`DOCKED` 상태에서 `MaximizedPane`이 존재하는 경우, 해당 pane은 docked rendering 대상에서 제외한다.

이 규칙의 목적은 다음과 같다.
- 사용자가 maximized context를 유지한 pane을 taskbar를 통해 잠시 벗어났다가 다시 돌아올 수 있게 한다
- 동시에 그 pane이 docked 상태에서 다시 일반 pane처럼 보여 혼동을 주는 것을 막는다

### 14.2 fallback 표시
어떤 slot의 `ActiveDockPane`가 현재 `MaximizedPane`과 같다면, 해당 slot은 `DockStack` 내의 다른 eligible pane으로 fallback 표시를 시도한다.

### 14.3 fallback 실패
fallback 가능한 pane이 없으면, 그 slot은 empty dock slot을 표시한다.

---

## 15. Pane open / focus / close / move 규칙

### 15.1 open
pane open 시:
- pane이 closed라면 resident로 만든다
- 기본적으로 `HomeSlot`에 배치한다
- 이미 resident라면 새 인스턴스를 만들지 않는다

### 15.2 focus
focus 요청 시:
- visible pane이면 focus만 이동
- open hidden pane이면 보이는 상태로 전환 후 focus
- current `MaximizedPane`이면 필요 시 `MAXIMIZED` 상태로 복귀하여 표시 가능

### 15.3 close
Docked Pane은 close 가능하다. 터미널도 예외 없이 close 가능하다.
close 시:
- pane은 resident set에서 제거된다
- taskbar에 pinned되어 있다면 `pinned_closed` 상태로 남는다
- `ActiveDockPane` 또는 `DockStack`는 필요시 fallback / empty slot 규칙을 적용한다

### 15.4 move
pane은 알파에서도 3개 slot 사이로 이동 가능하다.
이동은 split 생성이 아니라 **slot 재배치**다.

---

## 16. Settings System Pane

### 16.1 정체성
`Settings`는 일반 Docked Pane이 아니라 `System Pane`이다.

### 16.2 표시 방식
`Settings`는 열리면 즉시 maximized system pane 형태로 표시된다.
그러나 이것은 `MaximizedPane`과는 별개 규칙이다.

### 16.3 MaximizedPane과의 관계
`Settings`는 `MaximizedPane`에 등록되지 않는다.

### 16.4 상태 복원
`Settings`를 열기 직전의 workspace 상태를 임시 저장한다.
`Settings`를 닫으면 그 직전 상태를 그대로 복원한다.

### 16.5 버튼 정책
`Settings`는 restore/minimize 버튼이 없다.
close 버튼만 제공한다.

---

## 17. Pane chrome / 버튼 정책

### 17.1 최소화 버튼
알파에서는 최소화 버튼을 사용하지 않는다.

### 17.2 maximize / restore 버튼
Docked Pane의 최대화 버튼은 현재 pane을 크게 보기 위한 버튼이다.
`MAXIMIZED` 상태에서 동일 위치의 버튼은 restore/return 의미의 아이콘으로 바뀐다.

### 17.3 remove / close 버튼
pane 제거 버튼은 아이콘 기반으로 표시하되, 정확한 도형은 추후 polish에서 조정 가능하다.

---

## 18. 1단계 최소 런타임 상태 모델

### 18.1 구현 소유 구조
1단계 구현에서는 NEXUS Shell workspace의 최소 canonical state를 **별도 runtime state 계층**으로 분리한다.

권장 코드 명칭:
- `WorkspacePaneKind`
- `DockSlot`
- `WorkspaceMode`
- `WorkspaceDockSlotState`
- `WorkspaceStateSnapshot`
- `WorkspaceStateMachine`
- `ShellWorkspaceRuntime`

이 계층은 pane/window의 **현재 resident 배치와 표시 context**만 소유한다.
실제 렌더링, 저장 파일 포맷, pane availability sanitize는 별도 단계에서 붙인다.

### 18.2 1단계 canonical state 범위
1단계 최소 runtime state는 아래만 canonical로 가진다.

- `WorkspaceMode`
- `MaximizedPane`
- `LeftRatio`
- `RightTopRatio`
- slot별 `DockStack`
- slot별 `ActiveDockPane`
- `PinnedSet`

`CurrentDockSlotByKind`는 별도 저장 필드가 아니라 `DockStack`들로부터 유도한다.

### 18.3 1단계 bootstrap state
shell 첫 bootstrap state는 아래로 고정한다.

- `WorkspaceMode = DOCKED`
- `MaximizedPane = null`
- `LeftRatio = 0.42`
- `RightTopRatio = 0.55`
- `LEFT.stack = [TERMINAL]`
- `LEFT.active = TERMINAL`
- `RIGHT_TOP.stack = []`
- `RIGHT_TOP.active = null`
- `RIGHT_BOTTOM.stack = []`
- `RIGHT_BOTTOM.active = null`
- `PinnedSet = { TERMINAL }`

즉 1단계에서는 shell 첫 진입 시 **Terminal만 resident/pinned** 상태로 시작한다.

### 18.4 1단계 비범위
아래 항목은 1단계 최소 runtime state의 canonical 범위에 포함하지 않는다.

- `FocusedPane`
- Settings 진입 직전 임시 복원 버퍼
- taskbar 시각 상태 캐시
- toast runtime store
- activity popup runtime store
- pane availability sanitize
- save/load 및 profile persistence 연결

### 18.5 2단계 hydrate/sanitize 코어 메모
2단계 구현에서는 1단계 canonical state 위에 **availability-aware hydrate/sanitize 계층**을 추가한다.

권장 코드 명칭:
- `WorkspaceStoredState`
- `WorkspaceStoredDockSlotState`
- `WorkspacePaneStateTable`
- `WorkspaceHydrationResult`
- `WorkspaceStateHydrator`

이 계층은 stored state를 current availability 기준으로 sanitize하여, 실제 runtime이 바로 적용할 수 있는 effective state를 만든다.

추가 구현 메모:
- `ReplaceState`는 raw stored state가 아니라 **sanitize 완료된 effective `WorkspaceStateSnapshot`** 만 받는다.
- pane availability는 hydrate 단계 입력으로 주입되며, 현재 save에서 unavailable pane은 resident layout에서 제거된다.
- 이 단계의 목적은 파일 I/O가 아니라, stored preference를 runtime-safe state로 바꾸는 엔진 코어를 분리하는 것이다.

---

## 19. 알파 범위의 단축키

알파에서는 전역 단축키 체계를 구현 범위 밖으로 둔다.
향후 구현 예정(reserved future)으로 기록만 남긴다.

---

## 20. Acceptance Criteria

### 20.1 레이아웃
- workspace는 정확히 3개 slot(`LEFT`, `RIGHT_TOP`, `RIGHT_BOTTOM`)만 가진다.
- 좌/우 폭, 우측 상하 비율 조절이 가능하다.
- 알파에서 slot 추가나 새로운 split 생성은 불가능하다.

### 20.2 WindowKind
- 각 windowkind당 동시에 1개 pane만 존재한다.
- taskbar는 windowkind 기준으로만 버튼을 표시한다.

### 20.3 Focus
- focus된 pane은 타이틀/테두리 등으로 명확히 표시된다.
- toast / activity popup은 focus 대상이 아니다.

### 20.4 Start Menu
- 시작 메뉴에 `Save / Load / Settings / Panels`가 표시된다.
- Save/Load는 하위 slot 메뉴를 오른쪽에 표시한다.
- Panels는 open/focus 진입점으로 동작한다.

### 20.5 Taskbar
- `focused`, `visible_unfocused`, `open_hidden`, `pinned_closed` 상태를 내부적으로 구분한다.
- `visible_unfocused`와 `open_hidden`은 시각적으로 동일하게 렌더링된다.
- 우클릭 메뉴는 pin/unpin만 제공한다.

### 20.6 Toast
- 5초 표시 + 1초 fade out이 동작한다.
- 아래에서 위로 쌓인다.
- 클릭 닫기 시 위쪽 toast들이 아래로 내려온다.
- 터미널 safe zone 우선 규칙을 따른다.

### 20.7 Activity Popup
- windowkind당 1개만 존재한다.
- 같은 popup 안에 여러 작업 항목을 함께 표시할 수 있다.
- 작업 종료 후 3초 뒤 자동으로 닫힌다.
- safe zone 규칙을 따른다.

### 20.8 Settings
- `Settings`는 System Pane으로 열린다.
- `MaximizedPane` 규칙에 포함되지 않는다.
- 닫으면 진입 직전 workspace 상태를 정확히 복원한다.

### 20.9 MAXIMIZED / DOCKED 전이
- pane maximize 시 `MaximizedPane`이 저장되고 `WorkspaceMode = MAXIMIZED`가 된다.
- `MAXIMIZED` 상태에서 다른 일반 pane을 taskbar로 선택하면 `DOCKED`로 전환되지만 `MaximizedPane` 저장은 유지된다.
- 이후 taskbar로 `MaximizedPane`을 다시 선택하면 maximized context로 복귀할 수 있다.
- restore 버튼은 `MaximizedPane` 저장을 null 처리한다.

### 20.10 DOCKED에서 MaximizedPane 제외
- `DOCKED` 상태에서 `MaximizedPane`은 docked rendering 대상에서 제외된다.
- active pane이 제외 대상이면 fallback pane을 찾는다.
- fallback이 없으면 empty dock slot을 표시한다.

---

## 21. 추후 확장 예정(Reserved Future)

- slot 수 증가 / 사용자 정의 layout
- 전역 단축키
- richer pane chrome / icon polish
- input-pane-aware safe zone generalization
- more activity popup kinds
- modal dialog 필요성 재평가
 

