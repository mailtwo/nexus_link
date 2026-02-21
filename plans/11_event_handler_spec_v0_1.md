# Event System / Event Handler Spec v0.1 (MiniScript GuardContent)

목적: **process(endAt) / privilegeAcquire / fileAcquire**를 한 전역 시스템에서 처리하고, 시나리오(blueprint)의 이벤트 핸들러를 **event-driven**으로 디스패치한다.  
Codex는 이 문서만 보고 **인덱싱 기반 디스패치 + MiniScript guard 평가 + action 실행**까지 구현할 수 있어야 한다.

---

## 0) 핵심 결정 요약

- 핸들러는 **기본 once-only**(한 번 트리거되면 다시 실행되지 않음).
- `conditionArgs`의 와일드카드:
  - 값이 `null`이면 **ANY**(필터링 안 함)
  - 내부 인덱싱에서는 sentinel 문자열 `__ANY__`로 치환
  - **required 키**는 *누락이면 로드 에러*, 값이 `null`이면 “의도적 ANY”로 허용
- 이벤트 처리 타이밍: **WorldTick(고정 스텝) 끝에서 Drain**.
- WorldTick 시간 기준: **고정 스텝 누적 (60Hz)**. 렌더 프레임레이트(delta) 기반 누적은 사용하지 않는다.
- Guard는 **MiniScript 기반**. `guardContent` 한 필드로 script/id/path 3가지 소스를 표현.
- Guard 오류/타임아웃 시 결과는 **항상 false + warn 로그**.
- Guard 실행 예산:
  - 1회 호출 최대: **1 tick = 0.0166s**
  - 1 tick 전체 총합: **3 ticks ≈ 0.05s** (초과분은 다음 tick으로 이월)
- Action 실행은 **부분 성공 허용 + warn** (실패해도 다음 action 진행).
- `print` action의 출력 목적지는 **터미널 즉시 출력(프로그램 stdout 유사)** 으로 고정한다.
- `fileAcquire.fileName` 매칭 기준은 **basename(확장자 포함 파일명 전체)** 으로 고정한다.
- `guardContent`의 `path-...`는 **프로젝트 루트 기준 상대경로**로 해석한다.
- Save/Load는 아직 확정하지 않되, 저장 후보(필수)만 본문에 언급한다.

---

## 1) 시간 기준: WorldTick 60Hz

### 1.1 Tick 모델
- 월드는 “고정 스텝 시뮬레이션”으로 동작한다.
- `worldTick`는 60Hz로 증가하며(= tick당 1/60초), **렌더 FPS 변동과 무관**하다.
- 구현 권장:
  - Godot의 **_PhysicsProcess**(기본 60Hz)에서 `WorldTick()`을 호출한다.
  - 프로젝트 설정에서 Physics tick rate를 바꾸면(예: 30/120) 동일하게 반영해야 한다.

### 1.2 시간 값 표현
- 내부 기준 시간: `worldTickIndex: long` (0,1,2,...)
- 편의 시간(ms): `worldTimeMs = floor(worldTickIndex * 1000 / 60)`
  - 이 값은 결정론적이며(고정 스텝 누적), wall-clock(실시간)과는 별개다.
- 본 문서에서 `now`는 `worldTimeMs`를 의미한다.

> 기존 런타임 문서에서 `ProcessStruct.endAt`에 “Unix time”이라고 적혀 있었더라도,  
> **이 스펙에서는 `endAt`을 `worldTimeMs` 기준의 ‘월드 시간’으로 해석**한다.  
> (wall-clock 연동이 필요해지는 시점에만 별도 정책을 도입한다.)

---

## 2) 이벤트 타입과 Payload DTO

전역 이벤트 큐는 아래 3개 타입을 지원한다.

### 2.1 Event Envelope (권장)
```csharp
public sealed record GameEvent(
    string eventType,    // "processFinished" | "privilegeAcquire" | "fileAcquire"
    long timeMs,         // worldTimeMs (event 발생 시각)
    long seq,            // 단조 증가 시퀀스(디버그/결정론)
    object payload       // 아래 DTO 중 하나
);
```

### 2.2 `processFinished` (엔진 내부 이벤트)
**발생 조건**
- ProcessScheduler가 `now >= endAt`인 `running` 프로세스를 완료 처리할 때 1회 enqueue.

**Payload**
```csharp
public sealed record ProcessFinishedDto(
    int processId,
    string hostNodeId,
    string userKey,                // "system" 가능
    string name,
    string path,
    string processType,            // 예: "booting", "ftpSend", "fileWrite" ...
    Dictionary<string, object> processArgs,

    long scheduledEndAtMs,         // ProcessStruct.endAt
    long finishedAtMs,             // now

    bool effectApplied,            // 서버 reason 보호 규칙으로 false 가능
    string? effectSkipReason       // effectApplied=false일 때만
);
```

> 서버 상태 보호 규칙(기존 합의): 서버 reason이 `disabled` 또는 `crashed`면 “완료 효과”는 적용하지 않고 finished 처리만 한다.

### 2.3 `privilegeAcquire` (시나리오 이벤트 지원)
**발생 조건**
- 월드 상태에 새로운 권한 `(nodeId,userKey,privilege)`가 “추가되는 순간” 1회 enqueue.
- 이미 보유 중이면 중복 enqueue하지 않는다.

**Payload**
```csharp
public sealed record PrivilegeAcquireDto(
    string nodeId,
    string userKey,
    string privilege,        // "read" | "write" | "execute"
    long acquiredAtMs,

    // 선택(디버그/가드용)
    string? via,             // "ssh.connect" | "otp" | "exploit" | "script" ...
    List<string>? unlockedNetIds // privilege=="execute"로 인해 새로 Visible 된 netId들(선택)
);
```

### 2.4 `fileAcquire` (시나리오 이벤트 지원)
**발생 조건**
- “플레이어 로컬(워크스테이션)에 파일이 획득되어 접근 가능해지는 순간” 1회 enqueue.
- 전송 방식(ftp/process 완료/직접 복사 등)과 무관하게 **로컬 반영 지점 1곳**에서 발행한다.

**Payload**
```csharp
public sealed record FileAcquireDto(
    string fromNodeId,     // 출처 노드
    string userKey,        // 획득 주체(보통 플레이어)
    string fileName,       // 시나리오 필터 키(basename; 확장자 포함)
    long acquiredAtMs,

    // 선택(디버그/가드용)
    string? remotePath,
    string? localPath,
    int? sizeBytes,
    string? contentId,     // BlobStore 참조(있으면)
    string? transferMethod // "ftp" | "fs.read" | "pkg" ...
);
```

---

## 3) 시나리오(EventBlueprint) 확장 v0.1

Blueprint v0의 EventBlueprint는 아래 필드가 있다.
- `conditionType: { privilegeAcquire, fileAcquire }`
- `conditionArgs: Dictionary<string, Any>`
- `actions: List<ActionBlueprint>`

문서 유지보수 규칙:
- EventBlueprint의 기본 스키마(필드/args 정의)는 `plans/10_blueprint_schema_v0.md`를 단일 소스로 참조한다.
- 이후 EventBlueprint 스키마 변경 시, `plans/10_blueprint_schema_v0.md`를 우선 갱신하고 본 문서는 확장/런타임 규약만 동기화한다.

v0.1에서는 여기에 **guardContent**를 추가한다.

```text
EventBlueprint (v0.1)
- conditionType: ENUM { privilegeAcquire, fileAcquire }
- conditionArgs: Dictionary<string, Any>
- guardContent?: string
- actions: List<ActionBlueprint>
```

또한 시나리오 최상위에 Scripts를 둘 수 있다.

```text
Scripts?: Dictionary<string /*scriptId*/, string /*MiniScript body*/>
```

---

## 4) conditionArgs 키 규칙(필수/옵션, wildcard)

### 4.1 공통
- `null` 값은 **ANY(와일드카드)**로 해석한다.
- 내부 인덱스에는 `null`을 `__ANY__`로 치환해 저장한다.
- **required 키 누락은 로드 에러** (단, required 키에 `null`을 명시하는 것은 “의도적 ANY”로 허용).

### 4.2 `privilegeAcquire` conditionArgs
```text
- nodeId?: string | null        # null이면 ANY
- userKey?: string | null       # null이면 ANY
- privilege: string | null      # required(키 누락은 에러), null이면 ANY
```

예시:
- 특정 서버/유저/권한: `{ nodeId: "n1", userKey: "u1", privilege: "execute" }`
- 어떤 서버든, execute만: `{ nodeId: null, userKey: null, privilege: "execute" }`
- 어떤 privilege든(=전체): `{ nodeId: null, userKey: null, privilege: null }`

### 4.3 `fileAcquire` conditionArgs
```text
- nodeId?: string | null        # 출처 노드(fromNodeId), null이면 ANY
- fileName: string | null       # required(키 누락은 에러), basename(확장자 포함) 기준, null이면 ANY(=모든 파일)
```

예시:
- 어떤 노드에서든 특정 파일: `{ nodeId: null, fileName: "something" }`
- 어떤 파일이든(=전체): `{ nodeId: null, fileName: null }`

### 4.4 잘못된 키/타입
- `conditionArgs`에 **알 수 없는 키**가 들어오면: warn 로그를 남기고 무시(고정)
- required/optional 키에 타입이 잘못되면: 로드 에러(고정)

---

## 5) guardContent (MiniScript Guard) 규약

### 5.1 목적
- `conditionArgs` 인덱싱으로 후보를 좁힌 뒤, **payload(evt) + state**를 써서 최종 판정을 수행한다.
- Guard는 “미션/트리거 로직”을 더 유연하게 만들기 위한 옵션이며, 없으면 항상 true.

### 5.2 guardContent 문자열 포맷(3가지 prefix)
`guardContent`는 아래 3가지 방식 중 하나로만 해석된다.

1) **Inline 스크립트**: multi-line 문자열의 첫 줄이 `script-`
2) **Scripts 참조**: 단일 라인 `id-<scriptId>`
3) **외부 파일**: 단일 라인 `path-<relativePath>` (프로젝트 루트 기준)

그 외 prefix는 **파싱 에러(로드 실패)**.

#### YAML 예시: inline
```yaml
guardContent: |-
  script-
  return evt.privilege == "execute"
```

#### YAML 예시: id 참조
```yaml
Scripts:
  hardWinGuard: |-
    return evt.privilege == "execute"

events:
  hardScenarioWinEvent:
    guardContent: "id-hardWinGuard"
```

#### YAML 예시: path 참조
```yaml
guardContent: "path-plans/guards/hard_win_guard.ms"
```

`path-` 해석 기준:
- `path-foo/bar.ms` -> `<project_root>/foo/bar.ms`
- YAML 파일 위치 기준 상대경로를 사용하지 않는다.

### 5.3 “함수 body만 작성” + 엔진 자동 래핑
기획자는 guard를 함수로 작성하지 않고 **body만** 작성한다.  
엔진이 자동으로 아래 형태로 감싸서 컴파일한다.

```miniscript
func guard(evt, state)
  <body lines...>
end func
```

- `evt`: 이벤트 payload DTO (PrivilegeAcquireDto / FileAcquireDto / ProcessFinishedDto 등)
- `state`: 읽기 전용 상태 뷰(현재 v0.1에서는 “빈 객체”로 시작, 필요 시 확장)

### 5.4 실행기(인터프리터) 정책
- Guard 전용 인터프리터는 “유저 스크립트(툴)용 제약(성장/밸런스/샌드박스)”은 적용하지 않는다.
- 단, **시간 예산(타임아웃)**은 적용한다(프리즈 방지 퓨즈).

#### 시간 예산
- 1회 guard 호출 최대: `0.0166s`
- tick 전체 guard 총합 최대: `0.05s`
- tick 총합 예산을 초과하면:
  - 남은 이벤트 처리는 **다음 tick으로 이월**(드랍하지 않음)

> 구현 권장: MiniScript의 `RunUntilDone(timeSliceSeconds)` 모델을 그대로 사용.

#### 오류 처리
- 컴파일 오류: 로드 에러(권장, 개발자 작성이므로 fail-fast)
- 런타임 오류/타임아웃: guard 결과는 **false**, warn 로그 기록

로그에는 최소:
- scenarioId / eventId(핸들러 id), conditionType, guard 출처(script/id/path), 에러 메시지(+가능하면 라인/스택)

---

## 6) 인덱싱/디스패치(핵심)

### 6.1 인덱스 개요
- 목표: “모든 핸들러를 매 이벤트마다 순회”하지 않는다.
- `conditionType`별로 별도 인덱스를 둔다.
- 각 차원에서 **정확 키 + __ANY__**를 모두 고려해 후보를 가져온다.

#### Sentinel
- `const string ANY = "__ANY__";`
- 로드 시: `null` 또는 누락(optional) → ANY로 치환

### 6.2 privilegeAcquire 인덱스 예시(3차원)
키 순서(권장): `privilege` → `nodeId` → `userKey`
```text
indexPriv:
  privilegeKey(string) -> nodeIdKey(string) -> userKeyKey(string) -> List<HandlerId>
```

조회 시 후보 key 조합은 최대 2^3 = 8개:
- privilege: [actual, ANY]
- nodeId:    [actual, ANY]
- userKey:   [actual, ANY]

### 6.3 fileAcquire 인덱스 예시(2차원)
키 순서(권장): `fileName` → `nodeId`
```text
indexFile:
  fileNameKey(string) -> nodeIdKey(string) -> List<HandlerId>
```
조회 시 후보 key 조합은 최대 2^2 = 4개:
- fileName: [actual, ANY]
- nodeId:   [actual, ANY]

### 6.4 once-only 처리
- 전역 `firedHandlerIds: HashSet<string>` 유지
- 핸들러 실행 성공(=actions 실행 시도 완료) 시 `firedHandlerIds`에 추가
- 디스패치 시 `firedHandlerIds`에 있으면 무조건 skip

---

## 7) Event 처리 파이프라인(월드 tick 단위)

### 7.1 WorldTick() 권장 순서
1) `worldTickIndex++`, `now = worldTimeMs`
2) ProcessScheduler 업데이트:
   - **min-heap(top=endAt)** 기준으로 `now >= endAt`인 due 프로세스를 pop/finished 처리
   - `processFinished` 이벤트 enqueue
3) 월드 시뮬레이션 중 발생한 이벤트 enqueue(예: privilegeAcquire, fileAcquire)
4) tick 끝에서 `EventSystem.Drain(now)` 호출

### 7.2 Drain(now) 처리 흐름
- 입력: 현재 `now`(worldTimeMs)
- 내부: `eventQueue`를 앞에서부터 처리하되, **tick guard 총 예산(0.05s)**을 초과하면 중단하고 다음 tick으로 이월

각 이벤트에 대해:
1) **System Hooks (하드코딩 규칙)**  
   - `privilegeAcquire` + `privilege=="execute"`면, VisibleNets/KnownNodes 갱신 규칙을 먼저 적용(시나리오 actions보다 선행).
2) **Scenario Handler Dispatch**  
   - `eventType`이 `privilegeAcquire` 또는 `fileAcquire`일 때만 시나리오 핸들러 디스패치
   - 인덱스로 후보 핸들러 집합 획득
   - firedHandlerIds로 once-only 스킵
   - guardContent가 있으면 MiniScript guard 평가(개별 0.0166s 제한)
   - guard가 true면 actions 실행
3) 이벤트 타입이 시나리오에서 지원하지 않으면(예: processFinished) “시스템 훅만 수행”하거나 무시(v0.1에서는 무시해도 됨)

### 7.3 Reentrancy (v0.1)
- v0.1에서는 action이 새로운 이벤트를 emit하지 않는 것으로 가정한다.
- 추후 action이 이벤트를 emit하게 되면:
  - “즉시 재진입 처리” 대신 **queue에 append**하고 다음 Drain 루프에서 처리(무한 루프 방지).

---

## 8) Action 실행 규칙(v0)

ActionBlueprint (v0): `{ print, setFlag }`

- `print(text)`:
  - 터미널 출력 버퍼에 즉시 출력(프로그램 stdout과 동일한 사용자 경험)
- `setFlag(key,value)`:
  - 월드 플래그 저장소에 기록

실행 정책:
- 액션별로 실패 가능(잘못된 args 타입, 키 누락 등)
- 실패 시: warn 로그 남기고 **다음 액션 계속 실행**
- 핸들러 once-only 기록은 “actions 실행 시도 완료” 기준으로 남긴다(권장)

---

## 9) Save/Load (언급만)

저장/로드 포맷은 추후 결정한다. 다만 EventSystem 관점에서 저장 후보는 다음과 같다.

- `firedHandlerIds` (once-only 보장)
- `processList` (및 endAt/state 등)
- `worldTickIndex` (또는 now)
- (선택) `eventQueue` 미처리 이벤트들  
  - 결정론/정합성 요구에 따라 저장 여부 결정

로드 시에는:
- 시나리오 events를 다시 인덱싱
- guardContent를 다시 resolve/wrap/compile
- processList로부터 ProcessScheduler 힙(또는 정렬 리스트)을 재구축

---

## 10) 구현 체크리스트(Codex)

- [ ] WorldTick 60Hz 고정 스텝(Physics tick 기반)으로 `worldTickIndex/now` 관리
- [ ] EventQueue + seq 생성
- [ ] ProcessScheduler(min-heap 필수) + processFinished enqueue
- [ ] conditionType별 인덱스 구축 (`__ANY__` sentinel 포함)
- [ ] once-only: firedHandlerIds
- [ ] guardContent 파서(script-/id-/path-) + 래핑 + 로드타임 컴파일
- [ ] Guard 실행: 개별 0.0166s, tick 총 0.05s 예산, 에러/타임아웃=false + warn
- [ ] ActionExecutor: 부분 성공 + warn
- [ ] (선택) privilegeAcquire(execute) 시스템 훅 적용 시점(시나리오 actions보다 선행)
