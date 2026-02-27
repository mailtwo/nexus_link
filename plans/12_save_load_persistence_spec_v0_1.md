# Save/Load 영속화 스펙 v0.1 (Blueprint 재사용 + Binary Container)

목적:  
게임 런타임 상태를 저장/복원할 때, **시나리오/Blueprint는 재로딩**하고 저장 파일에는 **런타임 변경분만** 담는다.  
저장 포맷은 JSON이 아닌 **바이너리 컨테이너 + MessagePack + Brotli + HMAC** 조합을 사용한다.

범위(v0.1):
- 엔진: Godot C#
- 저장/로드 UI는 알파 버전 범위(엔진 API + UI 연동 포함)
- 로드시 시작 컨텍스트는 항상 `myWorkstation`
- 유저/서버 세션 연결 상태는 저장하지 않음

---

## 0) 핵심 결정 요약

- 로드 시 월드는 Blueprint/시나리오를 먼저 로딩해 기본 월드를 재구성한 뒤, 저장된 런타임 상태를 델타 적용한다.
- 저장 포맷은 바이너리 컨테이너를 직접 정의하고, 각 청크 payload는 MessagePack으로 직렬화한다.
- tamper-evident(변조 감지) 목표로 HMAC-SHA256을 붙인다.
- 압축은 Brotli를 사용한다.
- 스키마 확장은 `FormatMajor/Minor` + `ChunkVersion` + MessagePack `[Key(n)]` 고정 규칙으로 관리한다.
- 로더는 모르는 청크를 skip 가능해야 한다.

---

## 1) 저장/비저장 범위 계약

### 1.1 저장 대상 (필수)

1. 세이브 메타
- `saveSchemaVersion`
- `activeScenarioId`
- `worldSeed`

2. 월드 시간/시퀀스
- `worldTickIndex`
- `eventSeq`
- `nextProcessId`

3. 탐색/가시성 상태
- `visibleNets`
- `knownNodesByNet`

4. 월드 플래그
- `scenarioFlags`

5. 이벤트 진행 상태
- `firedHandlerIds` (once-only 보장)

6. 프로세스 테이블
- `processList` 전체

7. 서버별 mutable 상태
- `status`, `reason`
- `users` 런타임 상태(최소 privilege, 권장: user runtime 전체)
- `diskOverlay` 변경분 (`overlayEntries`, `tombstones`)
- `logs` (필수, **순서/ID 보존**)
- `ports`, `daemons` (런타임에서 변경될 수 있으면 저장)

8. 멀티 윈도우 레이아웃 상태(13 문서 계약)
- 모드별 윈도우 지오메트리(위치/크기/최대화 등)
- WindowKind별 직렬화 상태(`serialize_state` 결과)

### 1.2 저장 제외 대상 (필수)

1. UI/터미널 일시 상태
- 터미널 버퍼
- 명령 히스토리
- 에디터 UI 상태
- (예외) 멀티 윈도우 레이아웃 상태는 저장 대상(위 1.1-8)

2. 세션 연결 상태
- 터미널 연결 스택 (`terminalConnectionFramesBySessionId`)
- 서버 `sessions`
- 실행 중 프로그램 상태 (`terminalProgramExecutionsBySessionId`)

3. 파생/재구축 가능 캐시/인덱스
- `ipIndex`
- `eventIndex`
- `processScheduler`
- `scheduledProcessEndAtById`
- `dirDelta`

4. 정적 원본 데이터
- Base 이미지 원본
- Blueprint/시나리오 원문

5. 이벤트 큐
- `eventQueue`는 v0.1에서 비저장

### 1.3 로드 후 고정 규칙

- 로드 완료 후 시작 위치는 항상 `myWorkstation`.
- 유저/서버 세션 상태는 항상 비어 있어야 한다.
- 터미널 컨텍스트는 기본 로그인 컨텍스트를 다시 생성한다.

---

## 2) 바이너리 컨테이너 포맷

숫자 타입은 모두 little-endian.

### 2.1 파일 헤더

```text
SaveFileHeader
- magic: 4 bytes          # "ULS1"
- formatMajor: uint16     # 깨지는 변경에서 증가
- formatMinor: uint16     # 하위 호환 확장에서 증가
- flags: uint32           # bit0: Brotli, bit1: HmacSha256
- chunkCount: uint32
```

### 2.2 청크 레이아웃

```text
Chunk
- chunkId: uint32
- chunkVersion: uint16
- reserved: uint16        # 0 고정
- payloadLength: uint32
- payload: byte[payloadLength]
```

- payload는 MessagePack bytes.
- `flags.bit0`가 켜져 있으면 payload는 Brotli 압축된 MessagePack bytes.
- 로더는 알 수 없는 `chunkId`를 무시(skip)할 수 있어야 한다.

### 2.3 무결성 트레일러

- `flags.bit1`가 켜져 있으면 파일 끝에 32바이트 HMAC-SHA256 추가.
- HMAC 대상: 파일 시작부터 마지막 청크 끝까지 모든 바이트(트레일러 제외).
- HMAC 검증 실패 시 로드 실패.

참고:
- 이 방식은 “변조 감지” 목적이며, 클라이언트 단독 환경에서 완전한 anti-cheat 보안은 불가능하다.

---

## 3) 청크 계약 (v0.1)

### 3.1 Chunk ID 할당

```text
0x0001 SaveMetaChunk
0x0002 WorldStateChunk
0x0003 EventStateChunk
0x0004 ProcessStateChunk
0x0100 ServerStateChunk (서버당 1개, 반복 가능)
```

### 3.2 SaveMetaChunk v1

- `saveSchemaVersion: string`
- `activeScenarioId: string`
- `worldSeed: int`
- `savedAtUnixMs: long` (선택)

### 3.3 WorldStateChunk v1

- `worldTickIndex: long`
- `eventSeq: long`
- `nextProcessId: int`
- `visibleNets: List<string>`
- `knownNodesByNet: Dictionary<string, List<string>>`
- `scenarioFlags: Dictionary<string, object>`

### 3.4 EventStateChunk v1

- `firedHandlerIds: List<string>`
- `eventQueue`는 포함하지 않음

### 3.5 ProcessStateChunk v1

- `processes: List<ProcessSnapshot>`

`ProcessSnapshot`:
- `processId: int`
- `name: string`
- `hostNodeId: string`
- `userKey: string`
- `state: enum`
- `path: string`
- `processType: enum/string`
- `processArgs: Dictionary<string, object>`
- `endAt: long`   # worldTimeMs 기준

### 3.6 ServerStateChunk v1

- `nodeId: string`
- `status: enum`
- `reason: enum`
- `users: Dictionary<string /*userKey*/, UserSnapshot>`
- `diskOverlay: DiskOverlaySnapshot`
- `logs: List<LogSnapshot>` (삽입 순서 그대로)
- `logCapacity: int` (선택)
- `ports: Dictionary<int, PortSnapshot>` (선택)
- `daemons: Dictionary<string, DaemonSnapshot>` (선택)

`UserSnapshot`:
- `userId`
- `userPasswd`
- `authMode`
- `privilege(read/write/execute)`
- `info[]`

`DiskOverlaySnapshot`:
- `entries: List<OverlayEntrySnapshot>`
- `tombstones: List<string>`

`OverlayEntrySnapshot`:
- `path`
- `entryKind`
- `fileKind` (file일 때)
- `size`
- `content` (file일 때 문자열 payload; binary/image는 기존 런타임 규칙대로 base64 문자열 저장)

`LogSnapshot`:
- `id`
- `time`
- `user`
- `sourceNodeId` (필수, 내부 추적/역산 식별 키. UI 직접 노출 금지)
- `remoteIp`
- `actionType`
- `action`
- `dirty`
- `origin` (선택; dirty 최초 원본 복원용)

`LogSnapshot` 규칙:
- `sourceNodeId`는 공백/누락을 허용하지 않는다(누락 시 로드 실패).
- `remoteIp`는 표시용 관측값이며, 추적 판정 키로 사용하지 않는다.

---

## 4) 직렬화 규칙 (MessagePack)

- DTO는 `[MessagePackObject]` + `[Key(n)]`를 사용한다.
- `Key 번호`는 고정하고 재사용 금지.
- 필드 추가는 새 `Key`만 추가하고, 기존 `Key`의 의미/타입 변경 금지.
- 필드 제거가 필요하면 즉시 제거하지 않고 deprecated로 남기고 로더에서 무시한다.
- 로드 시 기본값으로 복원 가능한 필드는 optional로 유지한다.

권장:
- `MessagePackSecurity.UntrustedData` 설정 사용.
- Contractless 모드는 피하고 명시 Key 기반 사용.

---

## 5) 세이브/로드 절차 계약

### 5.1 Save 절차

1. 저장 대상 런타임 상태를 스냅샷 DTO로 수집한다.
2. Chunk DTO를 MessagePack으로 직렬화한다.
3. 필요 시 Brotli 압축한다.
4. 헤더 + 청크를 기록한다.
5. HMAC 트레일러를 기록한다.

### 5.2 Load 절차

1. 헤더 파싱 후 format/flags 검증.
2. HMAC 검증.
3. 청크 파싱(MessagePack 역직렬화, 알 수 없는 청크 skip).
4. `activeScenarioId/worldSeed` 기반으로 Blueprint/시나리오 재로딩 후 기본 월드 재구성.
5. 월드 런타임 상태(시간/탐색/flags/fired/process/server mutable)를 델타 적용.
6. 세션 관련 상태 초기화(항상 비움).
7. 터미널 시작 컨텍스트를 `myWorkstation` 기준으로 재초기화.
8. 파생 캐시/인덱스 재구축.

---

## 6) 호환성 정책

### 6.1 버전 정책

- `formatMajor`:
  - 호환 불가 변경에서만 증가.
  - 로더 major mismatch 시 로드 실패.
- `formatMinor`:
  - 하위 호환 확장에서 증가.
  - 로더가 더 낮은 minor라도, 알 수 없는 chunk/field skip으로 가능한 범위에서 복원.

### 6.2 청크 버전 정책

- 각 청크는 독립 `chunkVersion`을 가진다.
- 동일 `chunkId` 내에서 breaking 변경 시 `chunkVersion` 증가.
- 로더는 지원하지 않는 `chunkVersion`에 대해:
  - 필수 청크면 로드 실패
  - 선택 청크면 skip + warn

### 6.3 확장 원칙

- 필드는 optional/default 중심으로 추가.
- 기존 필드 번호/의미 재사용 금지.
- 청크 추가는 자유롭게 가능(unknown skip).

---

## 7) 구현 시 주의사항 (엔진 계약)

- 저장 파일만으로 월드를 새로 만들지 않는다.  
  항상 Blueprint/시나리오 기본 월드를 먼저 만든 후, 저장된 런타임 상태를 apply한다.
- 세션 비저장 정책은 저장/로드 양쪽에서 일관되게 유지한다.
- 로그는 반드시 순서/ID를 유지해 복원한다.
- `eventQueue` 비저장으로 인해 세이브 시점의 미처리 이벤트는 로드 후 유실될 수 있다(v0.1 허용).

---

## 8) 체크리스트

### 8.1 구현 체크리스트

- [ ] `src/` 하위에 save/load 컨테이너/DTO/IO 코드 추가
- [ ] 바이너리 헤더/청크 읽기-쓰기 구현
- [ ] MessagePack DTO 정의(`[Key(n)]` 고정)
- [ ] Brotli 압축/해제 연결
- [ ] HMAC 생성/검증 연결
- [ ] WorldRuntime Save/Load 엔진 API + 저장/로드 UI 연동 추가
- [ ] 로드 시 `myWorkstation` 시작 컨텍스트 강제
- [ ] 세션 상태 미복원 정책 반영
- [ ] 파생 캐시 재구축 루틴 반영

### 8.2 테스트 체크리스트

- [ ] Save -> Load 후 필수 상태 동등성 검증(플래그/탐색/프로세스/overlay/log)
- [ ] 로그 순서/ID 복원 검증
- [ ] 세션 미복원 검증(서버 sessions/터미널 스택/실행중 프로그램)
- [ ] unknown chunk skip 검증
- [ ] minor 확장 필드 추가 후 구버전 로더 동작 검증
- [ ] HMAC 변조 검출 검증
