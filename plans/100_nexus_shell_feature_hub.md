# 100 — NEXUS Shell 기능 허브

Purpose: Tier 2 feature hub for NEXUS Shell as the main player workspace across onboarding, commands, programs, and persistence.
Keywords: nexus shell, feature hub, shell unlock, workspace hub, taskbar, pane system, command parity, workspace restore
Aliases: shell feature hub, nexus hub

- 문서 버전: v0.1-draft
- 문서 역할: **Tier 2 Feature Hub**
- 상태: ACTIVE (draft)
- 관련 Tier 1 문서:
  - `13` — NEXUS Shell workspace / pane / taskbar / toast / activity popup / system pane 계약
  - `07` — 터미널 명령 / system call / command UX 계약
  - `14` — `nexus_shell` 및 관련 공식 프로그램 계약
  - `12` — shell/workspace 복원 경계 및 persistence 정책
  - `15` — shell 도입 시점, 온보딩 흐름, 1차 라이선스까지의 플레이어 여정

---

## 0. 문서 목적

이 문서는 **NEXUS Shell 기능 전체를 하나의 플레이어 경험 단위로 설명하는 허브 문서**다.

이 문서가 다루는 것은 다음과 같다.
- 왜 NEXUS Shell이 필요한가
- 터미널 전용 시작 상태에서 왜/어떻게 shell workspace로 확장되는가
- shell이 플레이어 경험에서 어떤 역할을 맡는가
- shell 관련 규칙이 어느 Tier 1 문서에 소유되는가
- 현재 확정된 것과 아직 미결인 것이 무엇인가

이 문서는 **저수준 규칙의 SSOT가 아니다.**
구체적인 명령 syntax, pane 상태 규칙, persistence boundary, 공식 프로그램 계약은 해당 Tier 1 문서를 따른다.

---

## 1. 한 줄 정의

**NEXUS Shell은 터미널 중심 플레이를 유지한 채, 관찰·탐색·상태 가시화·시스템 접근성을 제공하는 플레이어의 메인 workspace 레이어다.**

---

## 2. 왜 필요한가

터미널만으로도 플레이는 가능해야 하지만, 아래 이유로 shell workspace가 필요하다.

1. 플레이어가 자주 참고해야 하는 정보를 지속적으로 보여줄 공간이 필요하다.
   - 월드맵 / 네트워크 추적
   - 이메일
   - known servers / credentials / library
   - 작업 상태 표시(activity)

2. 플레이어가 “내 워크스테이션이 점점 갖춰지고 있다”는 감각을 느낄 수 있어야 한다.

3. 해킹 퍼즐의 핵심 입력은 터미널/스크립트에 남겨두되, 진행 상태와 주변 정보를 더 읽기 쉽게 제공할 필요가 있다.

4. 초반에는 터미널 중심 몰입을 주고, 이후에는 shell을 통해 점진적으로 사용성/가시성/전문가 감각을 확장하는 구조가 필요하다.

---

## 3. 설계 원칙

### 3.1 입력 원칙
- **게임플레이에 영향을 주는 명령/텍스트 입력은 Terminal / Script / Intrinsic에 남긴다.**
- Shell UI는 원칙적으로 상태 표시, 탐색, launcher, 가시화, workspace 관리 레이어다.
- 예외적으로 시스템 설정(Options/Settings)은 shell 내부의 `System Pane`에서 조정할 수 있다.

### 3.2 코어 재미와의 관계
- 이 게임의 코어 재미는 **서버 침투 퍼즐 공략**이다.
- NEXUS Shell은 그 퍼즐을 대체하는 UI가 아니다.
- NEXUS Shell은 아래를 보조한다.
  - 정보 가시화
  - 상태 모니터링
  - 빠른 pane 전환
  - 작업 흐름 유지
  - 플레이어의 워크스테이션 성장감

### 3.3 플레이어 감각
- 터미널만 강요하면 불친절하고 반복적일 수 있다.
- 반대로 GUI가 플레이를 대신하면 해킹 게임 정체성이 흐려진다.
- NEXUS Shell은 이 사이에서 **“입력은 터미널, 정보는 shell”**이라는 분업을 형성한다.

---

## 4. 플레이어 경험에서의 역할

NEXUS Shell은 플레이어에게 아래 4가지 역할을 제공한다.

### 4.1 Workspace
- Terminal, Web Viewer, Code Editor, World Map, Mail, Lists 등 작업 대상을 한 workspace 안에서 관리한다.

### 4.2 Monitoring
- SSH 시도, FTP 전송, password breaker 진행 같은 장시간 작업을 Activity UI로 보여준다.

### 4.3 Navigation
- Start menu / taskbar / panel focus를 통해 pane 접근 비용을 낮춘다.

### 4.4 Progression Surface
- 플레이어가 기능 해금과 workflow 확장을 체감하는 표면이 된다.

---

## 5. 범위

### 5.1 현재 문서가 직접 설명하는 범위
- NEXUS Shell의 목적
- shell 관련 UX 원칙
- shell을 구성하는 큰 요소
- 관련 문서 라우팅
- 현재 확정 상태와 미결 상태

### 5.2 이 문서가 직접 소유하지 않는 범위
- pane 상태 모델 / slot / maximize / taskbar 규칙
- toast / activity popup safe zone 및 수명 정책
- command parity와 명령 syntax
- `nexus_shell` 실행 계약
- save/load 포맷 및 transient 경계
- shell unlock의 정확한 시점과 튜토리얼 스크립팅

위 항목은 각 Tier 1 문서를 따른다.

---

## 6. 현재 확정된 방향 (summary)

### 6.1 workspace 성격
- 기존의 OS-level multi-window 실험보다, **game-internal shell workspace**가 핵심 방향이다.
- 플레이어는 shell 안에서 pane을 열고, 보고, 전환하고, 일부를 크게 본다.

### 6.2 입력 경계
- SSH/FTP/브레이커/스크립트 실행 등 핵심 게임플레이 입력은 터미널/스크립트에서 수행한다.
- Activity Popup은 입력용이 아니라 **상태 가시화/연출용**이다.

### 6.3 UI taxonomy
- `Docked Pane`
- `Activity Popup`
- `Toast`
- `System Pane` (Settings)

### 6.4 pane 문법
- 알파에서는 dock slot 수를 제한한다.
- pane는 `WindowKind` 단위로 1개씩만 열 수 있다.
- taskbar는 `WindowKind` 단위로 노출된다.

### 6.5 shell 사용감
- 플레이어는 자주 보는 pane들 사이를 taskbar로 빠르게 오갈 수 있다.
- 특정 pane은 크게(maximized) 볼 수 있다.
- maximized context와 docked context는 분리되어 유지될 수 있다.

---

## 7. 관련 Tier 1 문서와 소유권

### 7.1 `13`
**소유 내용:**
- shell workspace layout
- dock slot / dock stack / active pane
- maximize / restore / taskbar state model
- toast / activity popup / system pane 규칙
- focus 표시 / empty slot / pin/unpin / taskbar feedback

### 7.2 `07`
**소유 내용:**
- shell에 진입하거나 pane을 호출하는 command/system call
- 터미널 command parity
- 터미널 입력/출력 UX

### 7.3 `14`
**소유 내용:**
- `run nexus_shell`이 공식 프로그램일 경우 그 계약
- `nexus_shell` 실행 가능 시점/형태/에러 semantics
- 관련 공식 프로그램(예: inspect, viewer, shell launcher 등)의 계약

### 7.4 `12`
**소유 내용:**
- shell/workspace에서 저장되는 것과 저장되지 않는 것
- layout restore boundary
- activity popup / toast / maximized context / pane visibility의 persistence 경계

### 7.5 `15`
**소유 내용:**
- shell이 언제 등장하는가
- terminal-only 시작에서 shell unlock까지의 흐름
- 1차 라이선스까지 shell이 어떻게 플레이어 경험에 통합되는가

---

## 8. 권장 읽기 순서

### 8.1 shell 자체를 이해할 때
1. 이 문서 (`100`)
2. `13`
3. `07`
4. `14`
5. `12`
6. `15`

### 8.2 shell 구현을 시작할 때
1. `13`
2. `07`
3. `14`
4. `12`

### 8.3 shell unlock / 온보딩을 설계할 때
1. `15`
2. 이 문서 (`100`)
3. `13`
4. `14`
5. `07`

---

## 9. 알파 범위에서 shell이 반드시 보여줘야 하는 것

알파(특히 1차 라이선스 승급까지)에서 NEXUS Shell은 최소한 아래를 보여줘야 한다.

1. **Terminal 중심 작업 흐름**
2. **월드맵/네트워크 관련 참고 pane**
3. **메일/리스트류 pane 접근**
4. **taskbar 기반 pane 전환**
5. **activity popup 기반 상태 가시화**
6. **settings system pane 진입**

즉, 알파 shell의 목표는 “기능이 많은 데스크톱”이 아니라,
**플레이어가 터미널 중심 침투 플레이를 더 편하게, 더 선명하게, 더 해커답게 느끼게 하는 workspace**를 보여주는 것이다.

---

## 10. 현재 미결 사항 (non-blocking)

이 항목들은 아직 미결일 수 있으나, 이 허브 문서를 만드는 데는 치명적인 blocker가 아니다.

### 10.1 shell unlock 정확한 시점
- README 직후인지
- 첫 계약 중간인지
- 1차 라이선스 직전/직후인지
- 정확한 소유 문서: `15`

### 10.2 `nexus_shell`의 프로그램 형태
- 실제로 `run nexus_shell` 형태인지
- 어떤 공식 프로그램 계약으로 소유할지
- 정확한 소유 문서: `14`

### 10.3 shell command parity 범위
- 어떤 pane들이 command로 열리는지
- focus/open/toggle semantics를 어디까지 맞출지
- 정확한 소유 문서: `07`

### 10.4 persistence 상세
- 어떤 pane visibility/state가 save 대상인지
- maximized context 저장 여부
- activity popup/ toast restore 여부
- 정확한 소유 문서: `12`

### 10.5 panel catalog 고정 범위
- 알파에 실제로 어떤 pane들이 포함되는지
- 베타 이후 reserved pane은 무엇인지
- 정확한 소유 문서: `13` + `15`

---

## 11. 구현자/Codex용 주의사항

1. 이 문서에 저수준 규칙을 새로 정의하지 않는다.
2. shell 관련 concrete rule이 필요하면 먼저 owner Tier 1 문서를 찾는다.
3. shell이 여러 문서를 가로지른다고 해서 같은 규칙을 복제하지 않는다.
4. 이 문서는 feature overview / routing / open-question tracking 용도로 유지한다.

---

## 12. 다음으로 연결되는 작업

현재 상태에서 다음 작업은 아래 순서가 자연스럽다.

1. `13`를 shell workspace contract 방향으로 재정의/정리
2. `15`에 shell unlock / shell 통합 흐름 반영
3. `14`에 `nexus_shell` 프로그램 계약 반영
4. `07`에 shell 관련 command parity/entry point 반영
5. `12`에 shell restore boundary 반영

---

## 13. 한 줄 결론

**NEXUS Shell은 터미널을 대체하는 GUI가 아니라, 터미널 중심 해킹 플레이를 유지하면서 워크스테이션 감각과 정보 가시성을 제공하는 기능 허브다.**
 

