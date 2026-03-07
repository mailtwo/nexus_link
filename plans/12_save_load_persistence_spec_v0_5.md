# 12 — Save Slot Persistence Contract v0.5 (Alpha Redraft)

- 문서 상태: REDRAFT
- 대상 엔진: Godot 4.6 / C#
- 타겟 플랫폼: PC / Windows
- 문서 목적: **게임 진행 상태를 저장하는 save slot persistence**의 범위, 비범위, 저장 포맷, 로드 규칙을 정의한다.
- 본 문서는 기존 v0.1 내용을 알파 기준으로 재정의한 v0.5 초안이다.

> 관련 문서
> - Shell workspace / pane 상태 계약: `13_nexus_shell_workspace_contract.md`
> - 공식 프로그램 계약 (`nexus_shell` 포함): `14_official_programs.md`
> - 플레이어 여정 / shell 도입 흐름: `15_game_flow_design.md`
> - 터미널 명령 / `run` launcher: `07_ui_terminal_prototype_godot.md`
> - 서버 런타임 스키마: `09_server_node_runtime_schema_v0.md`
> - 이벤트/프로세스 런타임: `11_event_handler_spec_v0_1.md`

---

## 0. 범위와 핵심 결정

### 0.1 본 문서가 다루는 것
- save slot 파일에 어떤 데이터를 저장하는가
- save slot 파일에 무엇을 저장하지 않는가
- save slot 파일의 바이너리 컨테이너/청크 포맷
- save/load 시 월드 재구성 절차
- shell workspace 복원과 save slot의 경계

### 0.2 본 문서가 다루지 않는 것
- user options 저장 포맷
- workspace UI layout 저장 포맷
- pane resident/active/focus/pinned/maximized 직렬화 상세
- Settings system pane의 저장 포맷
- 개별 명령어/프로그램 동작 계약

위 항목들은 save slot과 **분리된 profile/workspace persistence** 범위이며, 별도 문서가 소유해야 한다.

### 0.3 핵심 결정 요약
1. save slot은 **게임 진행 상태**만 저장한다.
2. user options와 workspace UI 상태는 save slot에 포함하지 않는다.
3. 로드 시 월드는 Blueprint/시나리오를 먼저 재구성한 뒤, save slot의 런타임 델타를 적용한다.
4. 세션 연결 상태, 실행 중 프로그램 상태, 터미널 스크롤백 등은 저장하지 않는다.
5. `pre-shell / shell` UI 모드를 save slot의 별도 필드로 저장하지 않는다.
6. shell 사용 가능 여부는 **로컬 워크스테이션 VFS의 `/opt/bin/nexus_shell` 존재 및 실행 가능성**으로부터 유도한다.
7. 정상적인 알파 save slot은 NEXUS Shell 접근 이후에만 생성된다고 가정한다.

---

## 1. 저장/비저장 범위 계약

### 1.1 저장 대상 (필수)

#### A. Save 메타
- `saveSchemaVersion`
- `activeScenarioId`
- `worldSeed`
- `savedAtUnixMs` (선택)

#### B. 월드 시간 / 시퀀스
- `worldTickIndex`
- `eventSeq`
- `nextProcessId`

#### C. 탐색 / 가시성 / 진행 상태
- `visibleNets`
- `knownNodesByNet`
- `scenarioFlags`
- shell 도입과 관련된 **일반 진행 플래그** (예: 특정 연출 소비 여부)는 `scenarioFlags`에 포함될 수 있다.
  - 단, `pre-shell / shell` 자체를 별도 save field로 두지는 않는다.

#### D. 이벤트 진행 상태
- `firedHandlerIds`

#### E. 프로세스 테이블
- `processList` 전체

#### F. 서버별 mutable 상태
- `status`, `reason`
- `location` 런타임 상태 (`regionId`, `lat`, `lng`)
  - `displayName`은 저장하지 않고 로드 시 RegionData 규칙으로 재계산한다.
- `users` 런타임 상태
- `diskOverlay` 변경분 (`overlayEntries`, `tombstones`)
- `logs` (순서/ID 유지)
- `ports`, `daemons` (런타임에서 변경 가능하다면 저장)
- 기타 월드 진행에 영향을 주는 서버 mutable state

#### G. 로컬 워크스테이션 VFS 상태
- 로컬 워크스테이션 overlay는 일반 서버 mutable state와 동일 규칙으로 저장한다.
- 따라서 `/opt/bin/nexus_shell` 존재 여부, 권한, overlay 변경분도 save slot 복원 대상에 포함된다.
- shell 사용 가능 여부는 이 VFS 상태를 통해 간접적으로 보존된다.

### 1.2 저장 제외 대상 (필수)

#### A. 터미널 / UI 일시 상태
- 터미널 스크롤백
- 명령 히스토리
- 입력창 내용
- 에디터 임시 버퍼 / 커서 위치 / selection 상태
- Toast queue
- Activity Popup 상태
- `SSH_AUTH_ACTIVITY` 내부 항목
- `FTP_TRANSFER_ACTIVITY` 내부 항목
- Settings 열림 상태

#### B. Workspace UI 상태 (save slot 밖의 범위)
- 좌/우 split 비율
- `RIGHT_TOP` / `RIGHT_BOTTOM` 비율
- pane resident/open_hidden 상태
- slot별 `ActiveDockPane`
- `FocusedPane`
- `PinnedSet`
- `WorkspaceMode`
- `MaximizedPane`
- pane별 `CurrentDockSlot`

위 항목들은 save slot 저장 범위가 아니다. 별도의 profile/workspace persistence가 담당한다.

#### C. 세션 / 실행 중 상태
- 터미널 연결 스택
- 서버 `sessions`
- 실행 중 프로그램 상태
- `terminalProgramExecutionsBySessionId`
- `SessionHistoryStore`
- `ActiveSessionIndex`
- `ForensicIncidentBufferStore`
- `ForensicTraceStore`

#### D. 파생 / 재구축 가능 캐시 / 인덱스
- `ipIndex`
- `eventIndex`
- `processScheduler`
- `scheduledProcessEndAtById`
- `dirDelta`
- `location.displayName`
- 기타 로드 시 재구성 가능한 캐시

#### E. 정적 원본 데이터
- Base 이미지 원본
- Blueprint/시나리오 원문
- RegionData 원본 및 전처리 캐시

#### F. 이벤트 큐
- `eventQueue`는 알파 save slot에서 비저장

### 1.3 저장 범위의 의도
save slot은 **플레이어가 어떤 세계 상태에 도달했는가**를 저장한다.
반면 profile/workspace persistence는 **플레이어가 어떤 작업 환경으로 플레이하고 싶어 하는가**를 저장한다.
이 둘은 명확히 분리한다.

---

## 2. shell 관련 save/load 경계

### 2.1 별도 shell phase 필드를 저장하지 않는다
save slot은 `pre-shell`, `shell`, `shell-open`, `shell-closed` 같은 UI phase 필드를 저장하지 않는다.

이유:
- shell 사용 가능 여부는 로컬 워크스테이션의 `/opt/bin/nexus_shell` 존재와 실행 가능성으로부터 유도 가능하다.
- 알파에서 normal save는 pre-shell 상태에서 생성되지 않는다.
- shell이 현재 열려 있었는지 여부는 save slot이 아니라 workspace persistence의 영역이다.

### 2.2 shell 사용 가능 여부 판정
로드 후 shell 사용 가능 여부는 아래 순서로 판정한다.

1. 로컬 워크스테이션 VFS에서 `/opt/bin/nexus_shell` 존재 확인
2. 해당 파일이 실행 가능한 program contract를 만족하는지 확인
3. 만족하면 shell-capable 상태로 간주
4. 만족하지 않으면 terminal-only fallback 가능

### 2.3 `restore_shell`과의 관계
향후 `restore_shell` system call이 도입되더라도 save slot은 별도 shell phase 필드를 필요로 하지 않는다.
`restore_shell`의 결과는 결국 로컬 워크스테이션 VFS 변경(예: `/opt/bin/nexus_shell` 복구)으로 귀결되므로, save slot은 그 VFS 결과만 저장하면 충분하다.

---

## 3. 바이너리 컨테이너 포맷

숫자 타입은 모두 little-endian.

### 3.1 파일 헤더

```text
SaveFileHeader
- magic: 4 bytes          # "ULS1"
- formatMajor: uint16
- formatMinor: uint16
- flags: uint32           # bit0: Brotli, bit1: HmacSha256
- chunkCount: uint32
```

### 3.2 청크 레이아웃

```text
Chunk
- chunkId: uint32
- chunkVersion: uint16
- reserved: uint16        # 0 고정
- payloadLength: uint32
- payload: byte[payloadLength]
```

- payload는 MessagePack bytes
- `flags.bit0`가 켜져 있으면 payload는 Brotli 압축된 MessagePack bytes
- 로더는 알 수 없는 `chunkId`를 skip 가능해야 한다

### 3.3 무결성 트레일러
- `flags.bit1`가 켜져 있으면 파일 끝에 32바이트 HMAC-SHA256 추가
- HMAC 대상: 파일 시작부터 마지막 청크 끝까지 모든 바이트(트레일러 제외)
- HMAC 검증 실패 시 로드 실패

---

## 4. 청크 계약 (Alpha Redraft)

### 4.1 Chunk ID 할당

```text
0x0001 SaveMetaChunk
0x0002 WorldStateChunk
0x0003 EventStateChunk
0x0004 ProcessStateChunk
0x0100 ServerStateChunk (서버당 1개, 반복 가능)
```

알파 save slot 포맷에는 **WorkspaceStateChunk** 또는 **ProfileOptionsChunk**를 정의하지 않는다.

### 4.2 SaveMetaChunk v1
- `saveSchemaVersion: string`
- `activeScenarioId: string`
- `worldSeed: int`
- `savedAtUnixMs: long` (선택)

### 4.3 WorldStateChunk v1
- `worldTickIndex: long`
- `eventSeq: long`
- `nextProcessId: int`
- `visibleNets: List<string>`
- `knownNodesByNet: Dictionary<string, List<string>>`
- `scenarioFlags: Dictionary<string, object>`

### 4.4 EventStateChunk v1
- `firedHandlerIds: List<string>`
- `eventQueue`는 포함하지 않음

### 4.5 ProcessStateChunk v1
- `processes: List<ProcessSnapshot>`

### 4.6 ServerStateChunk v1
- `nodeId: string`
- `status: enum`
- `reason: enum`
- `location: ServerLocationSnapshot`
  - `regionId: string`
  - `lat: double`
  - `lng: double`
  - `displayName`은 포함하지 않음
- `users: Dictionary<string, UserSnapshot>`
- `diskOverlay: DiskOverlaySnapshot`
- `logs: List<LogSnapshot>`
- `logCapacity: int` (선택)
- `ports: Dictionary<int, PortSnapshot>` (선택)
- `daemons: Dictionary<string, DaemonSnapshot>` (선택)
- `icon: ServerIconSnapshot` (선택)

> 상세 DTO 스키마(`UserSnapshot`, `DiskOverlaySnapshot`, `LogSnapshot`, `ServerLocationSnapshot`, `ServerIconSnapshot` 등)는 본 문서의 청크 계약을 기준으로 구현한다. 단, UI/workspace 관련 snapshot은 포함하지 않는다.

---

## 5. 직렬화 규칙 (MessagePack)

- DTO는 `[MessagePackObject]` + `[Key(n)]`를 사용한다.
- `Key` 번호는 고정하고 재사용 금지
- 필드 추가는 새 `Key`만 추가
- 기존 `Key`의 의미/타입 변경 금지
- 필드 제거는 즉시 삭제보다 deprecated 후 무시를 권장
- optional/default 복원이 가능한 필드는 optional로 유지

권장:
- `MessagePackSecurity.UntrustedData`
- Contractless 모드 회피

---

## 6. Save / Load 절차 계약

### 6.1 Save 절차
1. 저장 대상 런타임 상태를 DTO로 수집한다.
2. save slot 범위 밖의 profile/workspace 상태는 수집하지 않는다.
3. Chunk DTO를 MessagePack으로 직렬화한다.
4. 필요 시 Brotli 압축한다.
5. 헤더 + 청크를 기록한다.
6. HMAC 트레일러를 기록한다.

### 6.2 Load 절차
1. 헤더 파싱 및 format/flags 검증
2. HMAC 검증
3. 청크 파싱(MessagePack 역직렬화, unknown skip)
4. `activeScenarioId/worldSeed` 기반으로 Blueprint/시나리오 재로딩 후 기본 월드 재구성
5. 월드 런타임 상태(시간/탐색/flags/fired/process/server mutable)를 델타 적용
   - 서버 `location.regionId/lat/lng`를 적용한 뒤 `displayName`은 RegionData 규칙으로 재계산한다
6. 세션 관련 상태 초기화(항상 비움)
7. 터미널 시작 컨텍스트를 로컬 워크스테이션 기준으로 재초기화
8. shell 사용 가능 여부를 로컬 워크스테이션 VFS의 `/opt/bin/nexus_shell` 존재로부터 판정
9. shell-capable면 profile/workspace persistence 복원 루틴을 **별도**로 호출할 수 있다
10. shell-capable가 아니면 terminal-only fallback이 가능하다
11. 파생 캐시/인덱스를 재구축한다

### 6.3 로드 후 고정 규칙
- 로드 후 세션 연결은 항상 비어 있어야 한다.
- 실행 중 프로그램은 복원되지 않는다.
- 터미널은 항상 로컬 워크스테이션 컨텍스트로 재초기화된다.
- save slot만으로 shell workspace를 강제 복원하지 않는다.
- workspace 복원은 profile/workspace persistence가 존재할 때만 시도한다.

---

## 7. Profile / Workspace persistence와의 경계

### 7.1 경계 원칙
- save slot은 **게임 진행 상태**를 저장한다.
- profile/workspace persistence는 **사용자 취향 및 작업환경**을 저장한다.

### 7.2 save slot이 알 필요가 없는 것
save slot은 아래의 구체 포맷을 알 필요가 없다.
- `OptionsState`
- `WorkspaceUiState`
- pane pin/layout/focus/maximized 상세

단, 로드 이후 별도 복원 루틴이 실행될 수 있다는 사실만 안다.

### 7.3 로드 후 sanitize 책임
workspace restore 시 다음과 같은 sanitize는 save slot 문서가 아니라 **profile/workspace persistence 문서**가 소유한다.

예:
- 현재 save에서 사용 불가한 pane 제거
- invalid `ActiveDockPane` fallback 처리
- invalid `MaximizedPane` -> `DOCKED` fallback
- invalid pinned pane 제거

본 문서는 그러한 sanitize가 별도 계층에서 수행되어야 한다는 사실만 정의한다.

---

## 8. 호환성 정책

### 8.1 버전 정책
- `formatMajor`: 호환 불가 변경에서 증가
- `formatMinor`: 하위 호환 확장에서 증가
- major mismatch 시 로드 실패

### 8.2 청크 버전 정책
- 각 청크는 독립 `chunkVersion`을 가진다
- breaking 변경 시 `chunkVersion` 증가
- 지원하지 않는 필수 청크 버전은 로드 실패
- 지원하지 않는 선택 청크 버전은 skip + warn 가능

### 8.3 workspace 관련 legacy 데이터
기존 구현/실험 버전에 save slot 내부 레이아웃 데이터가 포함되어 있더라도, 알파 redraft에서는 이를 canonical data로 취급하지 않는다.
가능하면 무시 또는 migrate-out 방향을 권장한다.

---

## 9. 구현 시 주의사항

- save slot 저장/로드는 항상 Blueprint 기반 월드 재구성 후 델타 적용으로 처리한다.
- shell 가능 여부를 별도 enum/flag로 중복 저장하지 않는다.
- `nexus_shell` 존재 여부는 로컬 워크스테이션 VFS에서 판정한다.
- 서버 location 저장 시 `regionId/lat/lng`만 저장하고 `displayName`은 저장하지 않는다.
- pre-shell에서 save UI를 제공하지 않는 현재 기획을 전제로 한다.
- 추후 debug/autosave/checkpoint가 pre-shell에도 생긴다면, 그때 별도 phase field 필요성을 다시 검토할 수 있다.

---

## 10. 체크리스트

### 10.1 구현 체크리스트
- [ ] save slot 저장 대상에서 workspace/layout 데이터를 제거
- [ ] 로컬 워크스테이션 VFS 복원으로 shell capability가 유지되는지 검증
- [ ] save/load 후 세션 상태가 항상 비워지는지 검증
- [ ] load 후 터미널 컨텍스트가 로컬 워크스테이션으로 재초기화되는지 검증
- [ ] save slot 로드 뒤 profile/workspace restore가 별도 단계로 분리되는지 검증

### 10.2 테스트 체크리스트
- [ ] Save -> Load 후 월드 진행 상태 동등성 검증
- [ ] Save -> Load 후 서버 `location.regionId/lat/lng` 복원 및 `displayName` 재계산 검증
- [ ] logs 순서/ID 복원 검증
- [ ] sessions / 실행 중 프로그램 미복원 검증
- [ ] `nexus_shell` 존재 save 로드 후 shell-capable 판정 검증
- [ ] `nexus_shell` 부재 save(디버그/예외 상황) 로드 시 terminal-only fallback 검증
- [ ] profile/workspace 파일이 없어도 save slot 자체는 정상 로드되는지 검증
- [ ] invalid workspace restore 참조가 있어도 save slot 로드 자체는 실패하지 않는지 검증
