# 108 — 개발자 도구 허브

Purpose: Tier 2 feature hub for developer tooling direction and debug-only startpoint override planning.
Keywords: developer tools, feature hub, debug boot, startpoint override, direct-to-shell, development entry, boot mode, debug flow
Aliases: dev tools hub, debug boot hub

- 문서 버전: v0.1-draft
- 문서 역할: **Tier 2 Feature Hub**
- 상태: ACTIVE (draft)
- 관련 Tier 1 문서:
  - `15` — 정식 플레이 흐름, pre-shell 온보딩, shell 도입 타이밍
  - `13` — shell bootstrap 상태와 workspace 의미 규칙
  - `16` — profile/workspace 복원 기반 시작점의 경계
  - `12` — gameplay save/load 기반 시작점 복원의 경계

---

## 0. 문서 목적

이 문서는 **개발자 도구와 개발 편의 기능을 하나의 허브 관점에서 정리하는 Tier 2 문서**다.

현재 이 허브가 실제로 다루는 범위는 매우 좁다.
지금 단계에서 이 문서는 **개발 중 direct-to-shell 시작점 우회 기능**만을 다룬다.

이 문서는 concrete rule의 SSOT가 아니다.
구체적인 플레이 흐름은 `15`, shell bootstrap 상태는 `13`, profile/workspace 복원은 `16`, gameplay save/load 복원은 `12`를 따른다.

---

## 1. 왜 필요한가

정식 게임 흐름은 유지되어야 한다.
플레이어는 처음에는 pre-shell 상태에서 시작하고, 이후 `run nexus_shell`을 통해 workspace를 확장해야 한다.

하지만 개발 중에는 아래 문제가 생긴다.

1. shell UI를 자주 테스트하려면 매번 pre-shell 구간을 반복해야 한다.
2. 이후 타이틀 메뉴가 추가되면, shell 진입 전 단계가 더 길어질 수 있다.
3. 온보딩 흐름을 유지하고 싶은 요구와 shell 반복 테스트 속도 요구가 동시에 존재한다.

따라서 개발 중에는 **정식 흐름을 깨지 않으면서도, 특정 시작점으로 빠르게 진입할 수 있는 개발용 시작점 우회**가 필요하다.

---

## 2. 현재 확정 방향

### 2.1 정식 플레이 흐름 유지
- 배포/정식 플레이 흐름은 계속 `15`를 따른다.
- 이 허브의 목적은 정식 시작 구조를 바꾸는 것이 아니다.

### 2.2 개발 중에만 허용
- 시작점 우회는 개발 편의 기능이다.
- 정식 플레이 경험과 동일선상에 두지 않는다.

### 2.3 현재 지원 목표 시작점
- 현재 우선 지원 목표는 정확히 1개다.
- **NEXUS Shell 첫 진입 직후 상태에서 바로 시작하는 개발용 direct-to-shell 시작점**

이 문서는 향후 더 많은 시작점 확장을 막지 않는다.
다만 지금 단계에서는 direct-to-shell 하나만 현재 범위로 본다.

---

## 3. direct-to-shell 시작점의 의미

이 시작점은 아래와 같은 의미를 가진다.

1. terminal-only 시작을 대체하는 정식 플레이 흐름이 아니다.
2. save/load 복원 상태도 아니다.
3. shell bootstrap 직후의 canonical workspace 상태를 개발용으로 바로 여는 우회 진입점이다.

즉 개발자는 pre-shell 온보딩을 매번 다시 거치지 않고도,
**shell을 막 실행한 직후의 기본 작업 환경**에서 바로 테스트를 시작할 수 있어야 한다.

이때 shell bootstrap의 concrete state는 이 문서가 정하지 않는다.
Canonical rule은 `13`를 따른다.

---

## 4. 문서 소유권 경계

### 4.1 `15`
소유 내용:
- 플레이어가 실제로 어디서부터 시작하는가
- pre-shell 온보딩과 shell 도입의 정식 경험

### 4.2 `13`
소유 내용:
- shell bootstrap 상태
- direct-to-shell 진입 시 사용되는 canonical workspace 의미 규칙

### 4.3 `16`
소유 내용:
- profile/workspace 복원 기반 시작점
- 저장된 UI 상태를 hydrate/sanitize하여 복원하는 경계

### 4.4 `12`
소유 내용:
- gameplay save/load를 통한 시작점 복원
- world/progression/runtime state 복원 경계

### 4.5 이 문서가 소유하지 않는 것
이 문서는 아래를 소유하지 않는다.

- `BootMode` enum 이름
- inspector/export 필드 이름
- 우선순위(precedence) 규칙
- release 빌드 차단 로직
- save slot / profile file 구조

이 항목들은 concrete contract가 필요한 시점에 적절한 Tier 1 문서에서 정의해야 한다.

---

## 5. 현재 비범위

현재 이 허브의 범위 밖인 항목은 아래와 같다.

- 여러 개발 시작점의 상세 taxonomy
- title menu bypass의 세부 동작 규칙
- 시나리오 제작 툴 / 퀘스트 제작 툴 / 서버 제작 툴의 상세 기획
- 에디터형 authoring tool의 파일 포맷 및 workflow

즉 지금 단계에서 이 문서는 **developer tools 전체 백로그**를 소유하지 않는다.
현재는 direct-to-shell 개발 시작점 우회만 먼저 다룬다.

---

## 6. 향후 확장 메모

장기적으로는 이 허브 아래에 아래 성격의 개발자 도구 논의를 연결할 수 있다.

- 추가 개발 시작점 override
- scenario/quest/server authoring tool
- debug/preview/validation toolchain

다만 이 항목들은 아직 아이디어 단계이며, 현재 문서의 concrete scope에는 포함하지 않는다.

---

## 7. 권장 읽기 순서

1. 이 문서 (`108`)
2. `15`
3. `13`
4. `16`
5. `12`

---

## 8. 한 줄 결론

**개발자용 direct-to-shell 시작점은 정식 플레이 흐름을 대체하는 기능이 아니라, shell 반복 테스트 속도를 높이기 위한 개발 전용 우회 진입점이다.**
 

