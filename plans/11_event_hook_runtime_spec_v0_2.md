# Event Hook Runtime Spec v0.2 (Unified Server / Scenario / Mission Hooks)

Purpose: Runtime hook dispatch and execution contract for unified server defense, scenario reactions, mission completion logic, bound config, and trace/evidence handoff.
Keywords: event hook, runtime dispatch, server defense, scenario hook, mission hook, trace evidence, session lineage, scriptRef, bound config, match filter, world tick
Aliases: unified hook system, event system v2

목적: 기존 `11` v0.1의 **processFinished / privilegeAcquire / fileAcquire + guard/action** 중심 모델을 재정의하여,
서버 방어 시스템, 시나리오 반응, 미션 완료 판정을 **하나의 unified hook runtime**으로 통합한다.

이 문서는 런타임 규칙의 SSOT다.
Codex는 이 문서만 보고 아래를 구현할 수 있어야 한다.

- GameEvent enqueue / end-of-tick drain
- 서버 / 시나리오 / 미션 훅 등록과 고정 실행 순서
- hook payload DTO
- `handler(evt, cfg, ops)` 실행 모델
- bound config merge / validation
- capability profile 분리
- `sessionId`와 `09`의 `server.sessions` / `SessionKey` 연결
- trace / evidence handoff
- v1 범위의 재귀 금지 / 에러 처리 / time budget

---

## 0) 문서 소유 범위와 비소유 범위

이 문서는 아래를 **소유한다**.

- unified hook runtime 모델
- core hook event 집합
- event envelope / payload DTO
- dispatch / ordering / drain semantics
- hook script 실행 계약
- capability profile 개념
- trace / evidence runtime semantics
- `09`의 세션 런타임 구조와 hook payload의 연결 규칙

이 문서는 아래를 **소유하지 않는다**.

- privileged intrinsic의 최종 함수 시그니처 / 반환 / 에러 코드 상세
  - Canonical rule: See DOCS_INDEX -> 03
- hook authoring YAML / blueprint 원본 스키마
  - Canonical rule: See DOCS_INDEX -> 10
- 서버 런타임 저장 구조의 필드/타입
  - Canonical rule: See DOCS_INDEX -> 09
- save/load persistence 경계
  - Canonical rule: See DOCS_INDEX -> 12
- trace가 플레이어에게 어떻게 보이고 느껴지는가
  - Canonical rule: See DOCS_INDEX -> 04

즉, 이 문서는 **"훅이 어떻게 돈다"**를 소유하며,
**"훅을 YAML에 어떻게 쓰는가"**, **"intrinsic이 정확히 어떤 시그니처를 가지는가"**,
**"저장 구조가 어떤 필드/타입을 가지는가"**는 다른 owner 문서가 소유한다.

---

## 1) 핵심 결정 요약

### 1.1 unified hook system
- 서버 방어 시스템, 시나리오 반응, 미션 완료/실패 판정은 **동일한 hook runtime**을 사용한다.
- 차이는 "누가 등록했는가"와 "어떤 capability가 열리는가"에 있다.

### 1.2 script-first
- v0.2의 본체는 **script-first** 모델이다.
- 기존 v0.1의 `guardContent + actions` 구조는 v0.2의 중심이 아니다.
- v1에서는 **hook마다 실행할 함수가 반드시 있어야 한다.**
- 단순 action 리스트만으로 동작하는 레거시 authoring이 필요하면, `10` 단계에서 script로 lowering해야 한다.

### 1.3 repeat 기본, once는 opt-in
- 모든 hook의 기본 실행 정책은 **repeat**다.
- `once: true`는 예외적 1회성 hook을 위한 선택 필드다.
- v0.1의 once-only 기본 규칙을 v0.2에 그대로 가져오지 않는다.

### 1.4 handler-local persistent state 미도입
- v1 hook script는 자유형 `state` bag이나 handler-local save chunk를 갖지 않는다.
- 공식 상태는 엔진이 의미를 아는 runtime state (`09`)에 두고,
  script는 privileged intrinsic을 통해서만 간접 변경한다.
- 플레이어가 세계 안에서 보는 흔적은 VFS / log / mail 쪽에 남긴다.
- 자유형 `server.memory` / `state` / `kv` 류는 v1에서 도입하지 않는다.
- future `slot(...)`형 persistent handle은 보류다.

### 1.5 post-action semantics
- hook은 **원본 행동의 성공/실패가 확정된 뒤** 실행된다.
- payload의 `resultCode`는 이미 확정된 최종 결과를 나타낸다.
- hook은 원본 행동을 취소하거나 롤백하지 않는다.

### 1.6 end-of-tick drain
- 엔진 액션은 우선 GameEvent를 enqueue한다.
- 모든 hook script 실행은 **WorldTick 끝의 drain**에서 처리한다.
- 일반 이벤트와 onTick 모두 같은 drain 프레임워크 안에서 처리한다.

### 1.7 고정 실행 순서
- 같은 이벤트에 대해 hook 실행 순서는 항상 아래와 같다.
  1. 서버 hook
  2. 시나리오 hook
  3. 미션 hook
- 의미: 방어 시스템이 먼저 반응하고, 그 결과를 시나리오/미션이 해석한다.

### 1.8 raw runtime state 직접 접근 금지
- hook script는 `serverState.*` 같은 raw runtime object에 직접 접근하지 않는다.
- 필요한 조회/변경은 모두 capability profile을 통해 제공되는 privileged intrinsic으로만 수행한다.

### 1.9 hook 내부 privileged action의 재발화 금지 (v1)
- hook script 안에서 호출한 privileged intrinsic은 **새 hook event를 다시 발생시키지 않는다.**
- v1에서는 reentrant event chain을 막아 루프/중복 발화를 방지한다.
- `event.emit(...)` 류 escape hatch는 보류다.

### 1.10 import 미지원 (v1)
- hook script v1은 `import`를 지원하지 않는다.
- 재사용은 외부 `.ms` 파일의 함수 참조(`scriptRef`)로 해결한다.
- hook 전용 library/import 정책은 later milestone로 보류한다.

### 1.11 bound config (v1)
- v1 hook 재사용의 핵심은 **고정 시그니처 + YAML bound config**다.
- 같은 behavior function을 여러 handler 인스턴스에서 재사용하되,
  handler마다 서로 다른 정적 `config`를 바인딩할 수 있어야 한다.
- `config`는 positional arg list가 아니라 **named config map**이어야 한다.
- 동적인 값은 `evt`에서 읽고, 정적인 값은 `cfg`에서 읽는다.
- `cfg`는 read-only이며 runtime 중 변경할 수 없다.
- `cfg`는 literal 값만 허용한다.
  - expression / mini-DSL / `evt.userKey` 같은 동적 참조는 v1에서 지원하지 않는다.
- load 단계에서 behavior schema default를 merge하고, unknown key / 타입 오류 / 제약 위반은 모두 **load error**로 처리한다.

### 1.12 v1 core에 포함하지 않는 것
- `onDaemonEvent`
- `onNetBanner`
- `onFileList`
- `onFileStat`
- `onCommandExec`
- 자유형 handler state
- hook 내부 event 재발행
- `id-...` 형태의 `scriptRef`
- inline `script-...` 형태의 `scriptRef`
- `config` expression / dynamic binding

이 항목들은 필요성이 생기면 v0.3 이후에 다시 검토한다.

---

## 2) 시간 기준과 drain 모델

### 2.1 WorldTick
- 월드는 고정 스텝 시뮬레이션으로 동작한다.
- `worldTick`는 60Hz 기준으로 증가한다.
- `worldTimeMs = floor(worldTickIndex * 1000 / 60)`를 기준 시간으로 사용한다.
- 본 문서의 `now`는 항상 `worldTimeMs`를 의미한다.

### 2.2 이벤트 enqueue 시점
- 엔진 액션은 자신의 원본 동작을 수행하고 결과를 확정한 뒤, 대응하는 GameEvent를 enqueue한다.
- hook script는 enqueue 시점에 즉시 실행하지 않는다.

### 2.3 drain 시점
- WorldTick의 끝에서 `EventHookRuntime.Drain(now)`를 호출한다.
- 이 drain 한 번 안에서:
  - queue에 쌓인 일반 이벤트를 처리하고
  - due 상태인 onTick hook용 synthetic event를 처리한다.

### 2.4 onTick 생성 규칙
- onTick은 물리 액션에서 직접 enqueue되지 않는다.
- drain 시작 직전, runtime은 등록된 onTick binding을 훑어
  `intervalSec`가 지난 binding에 대해 synthetic event를 queue tail에 append한다.
- 즉, 같은 tick 동안 engine이 enqueue한 일반 이벤트가 먼저 queue에 있고,
  due onTick은 그 뒤에 붙는다.
- 이 규칙은 "이번 tick에 일어난 플레이어 행동"을 먼저 처리하고,
  그 뒤 housekeeping/periodic logic을 돌리기 위한 것이다.

### 2.5 time budget
- hook script 1회 실행 최대 예산: **1 tick ≈ 0.0166s**
- 한 WorldTick drain 전체의 총 hook script 예산: **약 0.05s**
- 총 예산을 초과하면:
  - 현재 drain은 즉시 중단하고
  - 남은 GameEvent는 queue에 남겨
  - 다음 tick의 drain에서 이어서 처리한다.
- 이벤트를 드랍하지 않는다.

---

## 3) unified runtime 모델

## 3.1 GameEvent envelope

런타임의 기본 이벤트 envelope는 아래를 권장한다.

```csharp
public sealed record GameEvent(
    string eventType,
    string? targetNodeId,   // 서버 문맥이 있는 이벤트면 필수, global onTick이면 null 가능
    long timeMs,            // worldTimeMs
    long seq,               // 단조 증가 sequence
    object payload
);
```

규칙:
- `seq`는 drain/디버깅/결정론 보장을 위한 단조 증가 번호다.
- `targetNodeId`는 "이 이벤트가 어느 서버 문맥에서 발생했는가"를 뜻한다.
- `payload`는 아래 event-specific DTO 중 하나다.

### 3.2 script에 전달되는 `evt`
hook script가 받는 `evt`는 envelope meta와 payload를 합친 **read-only view**다.
최소 공통 메타 필드는 아래와 같다.

```text
evt.eventType
evt.targetNodeId
evt.worldTimeMs
evt.seq
...payload fields...
```

규칙:
- `evt`는 immutable이다.
- script가 `evt`를 수정해도 엔진 상태에 반영되지 않는다.

### 3.3 HookBinding (정규화된 런타임 구조)

원본 YAML/blueprint 스키마는 `10`이 소유하지만,
런타임이 실제로 들고 도는 정규화된 binding 구조는 아래를 권장한다.

```text
HookBinding
- hookId: string
- ownerKind: ENUM { server, scenario, mission }
- ownerId: string
- boundNodeId?: string            # server hook이면 필수, scenario/mission은 보통 null
- eventType: string
- match?: Dictionary<string, Any>
- scriptRef: string
- config?: Dictionary<string, Literal>
- priority: int                   # 기본 0, 클수록 먼저
- once: bool                      # 기본 false
- intervalSec?: int               # onTick 전용
- enabled: bool                   # 기본 true
- registrationOrder: long         # 로드 순서 안정성 보장용
```

### 3.4 registry 구조

v1 권장 coarse registry는 아래다.

```text
serverHooksByNodeAndEvent:
  nodeId -> eventType -> List<HookBinding>

scenarioHooksByEvent:
  eventType -> List<HookBinding>

missionHooksByEvent:
  eventType -> List<HookBinding>
```

규칙:
- 서버 hook은 `boundNodeId + eventType`로 1차 조회한다.
- 시나리오 hook은 `eventType`로 조회한다.
- 미션 hook은 **active mission instance**만 registry에 등록한다.
- finer-grained indexing은 추후 최적화 대상이며, v1 필수는 아니다.
- v1에서는 coarse bucket을 가져온 뒤 `match`를 평가하는 방식으로 충분하다.

---

## 4) scriptRef와 함수 해석

### 4.1 기본 방향
- hook 로직은 가능한 한 외부 `.ms` 파일의 **함수 단위**로 분리한다.
- YAML/blueprint는 "무슨 이벤트에 무엇을 연결할지"만 얇게 적고,
  실제 로직은 behavior script로 뺀다.

### 4.2 권장 scriptRef 포맷

v1 권장 포맷은 아래다.

```text
path-<relativePath>::<functionName>
```

규칙:
- `path-...`는 프로젝트 루트 기준 상대경로다.
- `::functionName`은 v1에서 **필수**다.
- `id-...` 참조는 v1에서 지원하지 않는다.
- inline `script-...`는 v1에서 지원하지 않는다.
- v1에서는 `scriptRef`가 반드시 하나 있어야 한다.

### 4.3 bound config와 behavior schema

`scriptRef`는 단순 함수 참조가 아니라, **함수 + handler-bound config** 재사용 모델과 함께 해석한다.
즉 개념적으로는 Python `partial`과 유사하지만, 실제 런타임 형태는
**고정 시그니처 + YAML bound config**다.

규칙:
- 각 handler는 optional `config`를 가질 수 있다.
- `config`는 function 호출 시 세 번째 권한 인자(`ops`)와 분리된 **정적 설정값 묶음**이다.
- 권한은 execution profile이 결정하며, `config`가 권한을 상승시키지 못한다.
- 예:
  - 누구를 잠글지, 어떤 세션을 볼지는 `evt`
  - 몇 초 잠글지, 임계치를 얼마로 둘지는 `cfg`

`config`의 허용 타입(v1):
- `null`
- `bool`
- `int`
- `float`
- `string`
- `List<Literal>`
- `Dictionary<string, Literal>`

여기서 `Literal`은 위 집합을 재귀적으로 포함하는 값이다.

`config` 규칙(v1):
- `cfg`는 read-only snapshot이다.
- `config`의 key는 named field여야 한다. positional arg list는 사용하지 않는다.
- `config`에는 expression을 허용하지 않는다.
  - 예: `targetUser: evt.userKey` 같은 표현은 load error 대상이다.
- 동적 값은 `evt`, 정적 값은 `cfg`에서 읽는 것을 원칙으로 한다.

behavior schema 규칙(v1):
- `config` 검증용 schema는 **script 파일 옆의 sidecar manifest**에서 읽는다.
- 권장 해석:
  - `path-behaviors/security.ms::lockout_on_auth_fail`
  - -> schema path `behaviors/security.behavior.yaml`
- exact manifest YAML shape와 authoring placement는 `10` owner가 확정한다.
- 이 문서에서 중요한 것은 runtime이 **function별 config schema를 로드 단계에서 읽고 검증한다**는 점이다.

merge / validation 순서(v1):
1. sidecar schema에서 function별 default를 읽는다.
2. handler authoring의 `config` override를 merge한다.
3. unknown key / 타입 오류 / required 누락 / min/max 같은 제약을 검증한다.
4. 성공하면 immutable `cfg` snapshot으로 고정한다.

실패 규칙(v1):
- handler에 `config`가 있는데 schema를 찾지 못함 -> **load error**
- schema에 없는 key가 `config`에 들어옴 -> **load error**
- 타입 불일치 / required 누락 / 제약 위반 -> **load error**

### 4.4 compile/load 정책
- path 해석 실패 -> **load error**
- 함수명을 찾지 못함 -> **load error**
- compile 오류 -> **load error**
- hook는 load 단계에서 최대한 fail-fast로 검증한다.

---

## 5) hook script 실행 계약

### 5.1 함수 시그니처

모든 hook 함수는 아래 시그니처를 사용한다.

```miniscript
handler = function(evt, cfg, ops)
  // evt: immutable payload view
  // cfg: handler에 bound된 read-only static config
  // ops: capability profile에 따라 주입되는 privileged namespace 집합
end function
```

### 5.2 `state` 없음
- v1에서는 `handler(evt, state, api)`가 아니다.
- `state`는 주입되지 않는다.
- `cfg`는 state가 아니라 **load 단계에서 검증/고정된 static config**다.
- 공식 상태 저장은 엔진이 소유하는 runtime state / VFS / log / mail / flags / mission state로만 한다.

### 5.3 반환값
- hook 함수의 반환값은 v1에서 사용하지 않는다.
- `return`은 조기 종료 용도로만 쓸 수 있다.
- 판정/행동은 모두 script 내부 side-effect로 수행한다.

### 5.4 에러 처리
- runtime error / timeout -> warn 로그 기록, 해당 hook는 실패로 간주
- 다른 hook 실행은 계속 진행한다.
- 원본 행동은 rollback하지 않는다.
- `once: true` hook는 **성공적으로 함수가 끝났을 때만** 소모된다.
  - runtime error/timeout이면 once 소모 안 함

### 5.5 import
- v1에서는 hook script에서 `import`를 허용하지 않는다.
- 재사용은 `scriptRef`로 다른 파일의 함수를 직접 바인딩하는 방식으로 해결한다.

### 5.6 rate limit
- 플레이어 intrinsic에 적용되는 shared `100k/s` rate limit은 hook execution profile에 그대로 적용하지 않는다.
- hook runtime은 별도 실행 프로필을 사용한다.

---

## 6) capability profile

상세 함수 시그니처/반환/오류는 `03`이 소유한다.
이 문서는 **어떤 namespace가 어떤 ownerKind에서 열리는가**만 확정한다.

### 6.1 server hook
server hook은 아래 namespace만 사용할 수 있다.

```text
flags.*
mailbox.*
world.*
server.*
trace.*
```

필수 최소 표면(이름 수준):
- `flags.*`
- `mailbox.*`
- `world.*`
- `server.sessions.get(...)`
- `server.sessions.listOpen()`
- `server.account.*`
- `server.port.*`
- `server.log.*`
- `server.fs.*`
- `trace.startHotFromSession(...)`
- `trace.startForensicFromSession(...)`
- `trace.markEvidence(...)`
- `trace.hasEvidence(...)`
- `trace.consumeEvidence(...)`

비고:
- `server.sessions.*`는 "서버 런타임 전체 접근"이 아니라, **현재 hook의 target server 문맥에서 세션을 읽기 전용으로 조회하는 accessor**다.
- raw `server.sessions[sessionId]` object를 script에 직접 노출하지 않는다.

### 6.2 scenario / mission hook
scenario hook과 mission hook은 **동일한 capability profile**을 사용한다.
즉, v1에는 별도의 "scenario-only profile"을 두지 않는다.

```text
flags.*
mailbox.*
world.*
mission.*
reward.*
```

규칙:
- scenario hook과 mission hook의 차이는 runtime ownerKind / registry / 실행 순서에만 있다.
- capability matrix 관점에서는 둘 다 동일한 Mission / Scenario Profile로 본다.

### 6.3 금지 규칙
- server hook은 `mission.*`, `reward.*`를 사용할 수 없다.
- scenario/mission hook은 `server.*`, `trace.*`를 사용할 수 없다.
- 모든 hook script는 raw runtime state에 직접 접근할 수 없다.

---

## 7) hook 실행 의미론

### 7.1 post-action
- hook은 원본 행동의 성공/실패가 확정된 뒤 실행된다.
- payload의 `resultCode`는 이미 확정된 최종 결과다.
- hook은 원본 행동을 막는 pre-hook / interceptor가 아니다.

### 7.2 multiple handlers
- 같은 event에 여러 handler가 연결되어 있으면 모두 실행한다.
- 한 handler의 오류는 다른 handler의 실행을 막지 않는다.

### 7.3 source-order
같은 event에 대한 최상위 실행 순서는 아래로 고정한다.

1. 서버 hook
2. 시나리오 hook
3. 미션 hook

### 7.4 intra-bucket order
같은 ownerKind bucket 안에서는 아래 순서로 실행한다.

1. `priority` 높은 것 먼저
2. `priority`가 같으면 `registrationOrder` 오름차순

즉 정렬 기준은:

```text
priority DESC, registrationOrder ASC
```

### 7.5 match 평가
- coarse bucket을 가져온 뒤 각 binding의 `match`를 평가한다.
- `match`를 통과한 hook만 script를 실행한다.
- `match`는 prefilter일 뿐이고, 복잡한 최종 판정은 script가 담당한다.

### 7.6 once
- `once=false`가 기본이다.
- `once=true`인 hook는 정상 종료 후 비활성화된다.
- 비활성화 상태는 runtime metadata로 관리한다.

### 7.7 reentrancy 금지 (v1)
- hook script 안에서 호출한 privileged intrinsic은 새로운 hook event를 emit하지 않는다.
- 예:
  - `onFileDelete` 안에서 `server.fs.delete(...)`를 호출해도
    그것이 다시 `onFileDelete`를 만들지 않는다.
- 이 규칙은 v1의 안정성과 디버깅 단순화를 위한 것이다.

---

## 8) match 규칙

### 8.1 기본 규칙
- `match`는 optional이다.
- 없으면 해당 event bucket에서 항상 script를 실행한다.
- 값이 `null`이면 ANY로 해석한다.
- key가 생략되면 필터링하지 않는다.

### 8.2 타입 규칙
- `match`의 unknown key -> **load error**
- 타입 불일치 -> **load error**
- v1은 authoring 실수를 조기 검출하는 쪽을 우선한다.

### 8.3 값 비교
- v1 `match`는 단순 exact equality만 지원한다.
- range / regex / complex expression은 `match`가 아니라 script 내부에서 처리한다.

---

## 9) event emission과 ordering

이 절은 "엔진 액션이 어떤 GameEvent를 언제 enqueue하는가"를 정의한다.

## 9.1 auth / session ordering

### 인증 실패
- 원본 auth 시도 결과가 실패로 확정되면:
  1. `onAuthAttempt`
  2. `onAuthFail`
  순서로 enqueue한다.

### 인증 성공
- 원본 auth 시도 결과가 성공으로 확정되고 session이 생성되면:
  1. `onAuthAttempt`
  2. `onAuthSuccess`
  3. `onSessionOpen`
  순서로 enqueue한다.

규칙:
- `onAuthAttempt`는 **모든 auth 시도마다** 발생한다.
- `onAuthSuccess` / `onAuthFail`는 특화 convenience event다.
- 성공 케이스에서만 session이 존재하므로, `onAuthSuccess`와 `onSessionOpen`은 session 정보를 가진다.

## 9.2 file op
- `fs.read/write/delete`는 원본 동작 성공/실패가 확정된 뒤 각각
  `onFileRead`, `onFileWrite`, `onFileDelete`를 enqueue한다.
- 실패 케이스도 event를 발생시킨다.
- script는 `resultCode`를 보고 분기한다.

## 9.3 recon
- `net.scan`, `net.ports`, `ssh.inspect`는 각각
  `onNetScan`, `onNetPorts`, `onSshInspect`를 enqueue한다.
- 실패 케이스도 event를 발생시킨다.

## 9.4 privilegeAcquire
- 새로운 권한이 실제로 추가될 때만 `onPrivilegeAcquire`를 enqueue한다.
- 이미 보유 중인 권한이면 중복 event를 만들지 않는다.

## 9.5 fileAcquire
- 전송/생성 결과로 파일이 local endpoint에서 **실제로 접근 가능해지는 순간** `onFileAcquire`를 enqueue한다.
- 기존 v0.1 의미를 유지한다.
- `ftp.get` / `fs.write` 등 local availability를 만드는 경로가 emission point다.

## 9.6 ftp transfer
- FTP 전송 시 서버 방어/forensic 용도로 `onFtpTransfer`를 enqueue한다.
- 이 event는 **FTP 포트를 가진 remote endpoint 서버 문맥**에서 발생한다.
- direction은 해당 서버 기준이다.
  - remote endpoint에서 바깥으로 나가면 `outbound`
  - remote endpoint 안으로 들어오면 `inbound`

## 9.7 processFinished
- ProcessScheduler가 due process를 완료 처리할 때 `onProcessFinished`를 enqueue한다.

---

## 10) v1 core event type 집합

v1 core hook event는 아래를 포함한다.

### 10.1 engine / progression
- `onProcessFinished`
- `onPrivilegeAcquire`
- `onFileAcquire`

### 10.2 auth / session
- `onAuthAttempt`
- `onAuthSuccess`
- `onAuthFail`
- `onSessionOpen`
- `onSessionClose`

### 10.3 recon
- `onNetScan`
- `onNetPorts`
- `onSshInspect`

### 10.4 file / transfer
- `onFileRead`
- `onFileWrite`
- `onFileDelete`
- `onFtpTransfer`

### 10.5 periodic
- `onTick`

### 10.6 deferred
- `onDaemonEvent`
- `onNetBanner`
- `onFileList`
- `onFileStat`
- `onCommandExec`

---

## 11) 공통 payload 규칙

### 11.1 script-visible 공통 meta
모든 event에서 `evt`는 최소 아래 meta를 가진다.

```text
evt.eventType: string
evt.targetNodeId: string | null
evt.worldTimeMs: long
evt.seq: long
```

### 11.2 source identity
server 문맥 event는 가능한 경우 아래 source identity를 payload에 넣는다.

```text
sourceIp: string
sourceNodeId: string | null
```

의미:
- `sourceIp`: 현재 target server가 관측한 direct peer IP
- `sourceNodeId`: 런타임이 direct peer node를 알면 그 nodeId, 모르면 null

### 11.3 target session identity
target server에 실제 session이 존재하는 event는 가능한 경우 아래를 payload에 넣는다.

```text
sessionId: int
userKey: string
```

규칙:
- `sessionId`는 **현재 event의 `targetNodeId` 서버의 `sessions` dictionary key**다.
- 즉 `09`의 `server.sessions[sessionId]`와 직접 연결된다.
- `sessionId`는 서버 단위 유일성만 보장한다.
- 전역 식별이 필요하면 엔진이 `(targetNodeId, sessionId)`를 결합해
  `SessionKey`로 승격한다.
- 이 규칙은 `09`의 `SessionKey(targetNodeId, sessionId)`와 정합해야 한다.
  Canonical rule: See DOCS_INDEX -> 09.

### 11.4 sessionId가 없는 경우
아래 경우에는 `sessionId`가 없거나 nullable일 수 있다.
- auth fail / no_such_user
- pre-login recon against remote host (`onNetPorts`, `onSshInspect`)
- session이 없는 로컬 컨텍스트 file op (`onFileRead`, `onFileWrite`, `onFileDelete`)
- process/system-origin event
- script/system grant event

이 경우 actor 식별은 `sourceIp`, `sourceNodeId`, 기타 event-specific field로 수행한다.
- 단, event payload가 actor `userKey` 필드를 가진다면 system/자동 실행 주체는
  `null`이 아니라 literal `"system"`을 사용한다.

### 11.5 resultCode
- `resultCode`는 원본 행동의 확정된 최종 결과다.
- 가능한 값은 event/underlying subsystem에 따라 달라도 되지만,
  최소한 deterministic string enum이어야 한다.
- exact error code ownership은 `03`이 가진다.
  Canonical rule: See DOCS_INDEX -> 03.

---

## 12) event payload DTO

이 절은 v1 core event의 script-visible payload를 정의한다.
아래 목록은 `evt.*`에 들어오는 필드다.
공통 meta(`eventType`, `targetNodeId`, `worldTimeMs`, `seq`)는 생략하고, payload 고유 필드만 적는다.

## 12.1 `onProcessFinished`

발생 문맥:
- `targetNodeId = process.hostNodeId`

```text
processId: int
userKey: string
name: string
path: string
processType: string
processArgs: Dictionary<string, Any>
scheduledEndAtMs: long
finishedAtMs: long
effectApplied: bool
effectSkipReason: string | null
```

권장 match key:
- `targetNodeId`
- `processType`
- `name`

## 12.2 `onPrivilegeAcquire`

발생 문맥:
- 새로운 권한이 target server에 실제로 추가된 순간

```text
sessionId: int | null
userKey: string
privilege: string                 # "read" | "write" | "execute"
acquiredAtMs: long
via: string | null                # "ssh.connect" | "otp" | "exploit" | "script" ...
unlockedNetIds: List<string> | null
sourceIp: string | null
sourceNodeId: string | null
```

규칙:
- `sessionId`는 privilege가 session-based flow에서 생긴 경우에만 채운다.
- `via`는 debugging / hook routing 보조용이다.

권장 match key:
- `targetNodeId`
- `userKey`
- `privilege`
- `via`

## 12.3 `onFileAcquire`

발생 문맥:
- local endpoint에서 파일이 실제 접근 가능해진 순간
- `targetNodeId`는 local endpoint server다

```text
sessionId: int | null
userKey: string
fromNodeId: string
fileName: string
acquiredAtMs: long
remotePath: string | null
localPath: string | null
sizeBytes: int | null
transferMethod: string | null     # "ftp" | "fs.write" | "pkg" ...
sourceIp: string | null
sourceNodeId: string | null
```

규칙:
- 기존 v0.1의 mission completion 용도를 유지한다.
- `userKey`는 local endpoint에서 이 파일 접근 가능 상태를 만든 실행 주체다.
- system/자동 실행이 local availability를 만들었으면 `userKey = "system"`으로 둔다.
- `fileName`은 basename(확장자 포함) 기준이다.

권장 match key:
- `targetNodeId`
- `fromNodeId`
- `fileName`
- `transferMethod`

## 12.4 auth event 공통 필드

`onAuthAttempt`, `onAuthSuccess`, `onAuthFail`는 아래 공통 필드를 가진다.

```text
authAttemptId: string
inputUserId: string
userKey: string | null
service: string
port: int
sourceIp: string
sourceNodeId: string | null
resultCode: string
```

설명:
- `inputUserId`: 플레이어/원격이 실제로 입력한 userId 문자열
- `userKey`: lookup 성공 시 내부 userKey, 실패하면 null 가능
- `resultCode`: 최종 auth 결과

### 12.4.1 `onAuthAttempt`
위 공통 필드만 가진다.

권장 match key:
- `targetNodeId`
- `inputUserId`
- `userKey`
- `service`
- `port`
- `resultCode`

### 12.4.2 `onAuthSuccess`
공통 필드에 아래가 추가된다.

```text
sessionId: int
```

규칙:
- 성공 케이스에서는 session이 이미 생성된 뒤 event가 enqueue된다.

권장 match key:
- `targetNodeId`
- `userKey`
- `service`
- `port`

### 12.4.3 `onAuthFail`
위 공통 필드만 가진다.

권장 match key:
- `targetNodeId`
- `inputUserId`
- `userKey`
- `service`
- `port`
- `resultCode`

## 12.5 session event 공통 필드

`onSessionOpen`, `onSessionClose`는 아래 공통 필드를 가진다.

```text
sessionId: int
userKey: string
service: string
port: int
sourceIp: string
sourceNodeId: string | null
```

### 12.5.1 `onSessionOpen`

추가 필드:

```text
authAttemptId: string | null
```

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `service`
- `port`

### 12.5.2 `onSessionClose`

추가 필드:

```text
closeReason: string
durationMs: int
wasForced: bool
```

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `closeReason`

## 12.6 `onNetScan`

발생 문맥:
- `net.scan`이 실행된 **actor server**에서 발생한다.
- 즉 `targetNodeId`는 "스캔을 수행한 현재 endpoint server"다.

```text
sessionId: int | null
userKey: string | null
scanNetId: string | null
sourceIp: string | null
sourceNodeId: string | null
resultCode: string
```

규칙:
- actor server 안에서 실행된 정찰 행위로 본다.
- 해당 server가 session 문맥에서 사용되었으면 `sessionId`, `userKey`를 채운다.

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `scanNetId`
- `resultCode`

## 12.7 `onNetPorts`

발생 문맥:
- `net.ports(hostOrIp, ...)`의 **inspected remote server**에서 발생한다.
- 즉 `targetNodeId`는 포트 열람 대상 서버다.

```text
sourceIp: string
sourceNodeId: string | null
resultCode: string
```

규칙:
- target server 문맥에는 로그인 session이 없을 수 있으므로 `sessionId`를 두지 않는다.
- actor identity는 `sourceIp`, `sourceNodeId`로 전달한다.

권장 match key:
- `targetNodeId`
- `sourceNodeId`
- `resultCode`

## 12.8 `onSshInspect`

발생 문맥:
- `ssh.inspect(hostOrIp, userId, ...)`의 inspected remote server에서 발생한다.

```text
inputUserId: string
userKey: string | null
port: int
sourceIp: string
sourceNodeId: string | null
resultCode: string
```

규칙:
- inspect는 target server login session을 만들지 않으므로 `sessionId`를 두지 않는다.
- inspected user lookup이 되면 `userKey`를 채우고, 아니면 null이다.

권장 match key:
- `targetNodeId`
- `inputUserId`
- `userKey`
- `port`
- `resultCode`

## 12.9 file op 공통 필드

`onFileRead`, `onFileWrite`, `onFileDelete`는 아래 공통 필드를 가진다.

```text
sessionId: int | null
userKey: string
path: string
sourceIp: string
sourceNodeId: string | null
resultCode: string
```

규칙:
- `path`는 canonical absolute VFS path다.
- cwd 해석은 event emission 전에 끝나 있어야 한다.
- `sessionId = null`은 **session이 없는 로컬 컨텍스트에서 수행된 file op**일 때만 허용한다.
- session 문맥에서 수행된 file op이면 `sessionId`를 반드시 채워야 한다.
- `sessionId = null`이어도 `userKey`는 해당 로컬 컨텍스트 실행 주체의 userKey를 채운다.

### 12.9.1 `onFileRead`
위 공통 필드만 가진다.

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `path`
- `resultCode`

### 12.9.2 `onFileWrite`

추가 필드:

```text
writeMode: string | null          # "create" | "overwrite" | "append" | "touch"
```

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `path`
- `writeMode`
- `resultCode`

### 12.9.3 `onFileDelete`
위 공통 필드만 가진다.

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `path`
- `resultCode`

## 12.10 `onFtpTransfer`

발생 문맥:
- FTP 전송의 remote endpoint server 문맥에서 발생한다.
- 즉 `targetNodeId`는 FTP 포트를 가진 remote endpoint server다.

```text
sessionId: int
userKey: string
path: string
direction: string                 # "inbound" | "outbound" (target server 기준)
sourceIp: string
sourceNodeId: string | null
resultCode: string
```

규칙:
- `path`는 target server 쪽 파일 경로다.
- `direction="outbound"`는 target server에서 바깥으로 빠져나간 경우다.
- `direction="inbound"`는 target server 안으로 들어온 경우다.
- 상대방 IP를 payload에 직접 넣지 않는다.
  필요하면 script가 `server.sessions.get(sessionId)`를 통해 현재 session의 `remoteIp`를 조회한다.

권장 match key:
- `targetNodeId`
- `sessionId`
- `userKey`
- `path`
- `direction`
- `resultCode`

## 12.11 `onTick`

```text
deltaMs: int
tickSeq: long
```

규칙:
- server hook의 onTick이면 `targetNodeId`는 해당 server다.
- global scenario/mission onTick이면 `targetNodeId = null`일 수 있다.
- `resultCode`는 없다.

권장 match key:
- 없음 (`onTick`은 interval 기반으로만 라우팅)

---

## 13) sessionId와 `09`의 연결 규칙

### 13.1 `server.sessions[sessionId]`
v1에서 payload에 들어가는 `sessionId`는 **현재 event의 `targetNodeId` 서버의 세션 딕셔너리 키**다.

즉 아래가 성립해야 한다.

```text
serverList[targetNodeId].sessions[sessionId]
```

여기서 조회되는 값은 `09`의 `SessionConfig`다.

Canonical rule: See DOCS_INDEX -> 09.

### 13.2 hook script 조회
server hook script는 아래 성격의 privileged accessor를 통해 세션 정보를 조회할 수 있어야 한다.

```text
server.sessions.get(sessionId)
server.sessions.listOpen()
```

의도:
- 이 accessor는 **현재 hook의 `targetNodeId` 서버 문맥**에서만 해석한다.
- cross-server 조회를 허용하지 않는다.
- 반환값은 live runtime object가 아니라 **read-only session snapshot**이어야 한다.
- script가 반환값을 수정해도 엔진의 실제 세션 상태는 바뀌지 않는다.

권장 의미:
- `server.sessions.get(sessionId)`
  - 현재 `targetNodeId` 서버의 `sessionId`를 조회해 단일 session snapshot을 반환한다.
- `server.sessions.listOpen()`
  - 현재 `targetNodeId` 서버에 열려 있는 session snapshot 목록을 반환한다.

반환 shape 원칙:
- exact ResultMap shape와 field 목록은 `03` owner가 확정한다.
- snapshot field는 `09`의 `SessionConfig` 및 그 직접 파생 정보 범위 안에서만 정의한다.
- `03` accessor가 `09`에 없는 임의의 hidden runtime field를 새로 노출해서는 안 된다.

이 문서에서 중요한 것은 **`sessionId`가 09의 세션 런타임과 직접 이어지며, script는 그 상태를 read-only projection으로만 본다**는 점이다.

### 13.3 SessionKey 승격
forensic / lineage / trace store처럼 전역 식별이 필요한 순간에는 엔진이 아래로 승격한다.

```text
SessionKey = (targetNodeId, sessionId)
```

이 규칙은 `09`의 SessionHistory / IncidentBuffer / ForensicTrace runtime과 정합해야 한다.

---

## 14) trace / evidence semantics

이 절은 server hook에서만 사용하는 trace/evidence 의미를 정의한다.
정확한 intrinsic 시그니처는 `03` owner가 확정한다.

## 14.1 목적
- hot trace 시작
- forensic trace 시작
- 여러 event 사이의 상관관계 저장
- 세션 종료 시 "수사 대상으로 넘길지" 판정

## 14.2 기본 개념
- evidence는 **세션 단위의 수사 표식**이다.
- v1에서 evidence는 자유형 payload 저장소가 아니라,
  "이 세션은 수사 대상으로 볼 만한 incident를 발생시켰다"는 표식의 집합으로 본다.
- 이때 evidence는 hook author 관점의 **논리적 API 용어**이고,
  실제 런타임 저장 구조는 `09`의 `ForensicIncidentBufferStore`를 사용한다.
- 즉 `trace.markEvidence/hasEvidence/consumeEvidence`는 별도 `sessionEvidence` 저장소를 만드는 함수가 아니라,
  `SessionKey(targetNodeId, sessionId)`에 대응하는 `IncidentBufferEntry`를 조작하는 façade로 본다.
- hook-visible set semantics는 `IncidentBufferEntry.incidentKinds`에 대응한다.
- `incidentCount`, `firstIncidentAt`, `lastIncidentAt` 같은 bookkeeping 필드는 엔진이 관리하며,
  hook API는 기본적으로 `incidentKinds` 존재 여부/소비 여부 중심으로 동작한다.

Canonical rule: See DOCS_INDEX -> 09.

### 14.3 required runtime functions (name level)

```text
trace.startHotFromSession(sessionId)
trace.startForensicFromSession(sessionId)

trace.markEvidence(sessionId, kind, ttlSec?)
trace.hasEvidence(sessionId, kind?)
trace.consumeEvidence(sessionId, kind?)
```

### 14.4 semantics

#### `trace.startHotFromSession(sessionId)`
- 현재 hook의 `targetNodeId` 서버 문맥에서 `SessionKey(targetNodeId, sessionId)`를 만들고,
  해당 세션 기준 hot trace를 시작한다.

#### `trace.startForensicFromSession(sessionId)`
- 현재 hook의 `targetNodeId` 서버 문맥에서 `SessionKey(targetNodeId, sessionId)`를 만들고,
  해당 세션 기준 forensic trace를 시작한다.

#### `trace.markEvidence(sessionId, kind, ttlSec?)`
- 해당 세션에 특정 종류의 evidence를 기록한다.
- `kind`는 required다.
- 내부적으로는 현재 server 문맥의 `SessionKey(targetNodeId, sessionId)`에 대응하는
  `ForensicIncidentBufferStore.bySessionKey[sessionKey]`를 생성/갱신한다고 본다.
- 최소한 `IncidentBufferEntry.incidentKinds`에 `kind`가 반영되어야 한다.
- 같은 kind가 여러 번 들어와도 v1 기본은 set semantics다.
  - 즉 count를 올리기보다 "존재 여부"만 유지한다.

#### `trace.hasEvidence(sessionId, kind?)`
- 내부적으로는 해당 `sessionKey`의 `IncidentBufferEntry.incidentKinds`를 조회한다.
- `kind`가 있으면 그 kind가 있는지 본다.
- `kind`를 생략하면 그 세션에 **아무 evidence나 하나라도 있는지** 본다.

#### `trace.consumeEvidence(sessionId, kind?)`
- 내부적으로는 해당 `sessionKey`의 `IncidentBufferEntry`를 소비/정리하는 동작으로 본다.
- `kind`가 있으면 그 kind만 제거한다.
- `kind`를 생략하면 그 세션의 **모든 evidence를 소비/제거**한다.
- 모든 kind가 비면 엔진은 해당 incident buffer entry를 제거할 수 있다.

### 14.5 권장 사용 패턴

#### hot trace
- 예: `onSessionOpen`에서 `userKey == "root"`이면 `trace.startHotFromSession(sessionId)`

#### forensic trace
- 예: `onFtpTransfer`, `onFileWrite`, `onFileDelete`에서 evidence를 남기고,
  `onSessionClose`에서 아래 패턴으로 forensic을 시작한다.

```miniscript
if trace.hasEvidence(evt.sessionId) then
  trace.startForensicFromSession(evt.sessionId)
  trace.consumeEvidence(evt.sessionId)
end if
```

### 14.6 evidence와 kind
- 기록할 때(`markEvidence`)는 반드시 `kind`를 남긴다.
- 판정할 때(`hasEvidence`, `consumeEvidence`)는 `kind`를 생략해 "아무 evidence나 있으면"으로 쓸 수 있다.
- 이 정책은 "왜 forensic 대상이 되었는가"는 보존하면서,
  "forensic을 시작할지 여부"는 간단히 판정하게 해 준다.

---

## 15) onTick runtime state

v1은 handler-local script state를 두지 않지만,
onTick scheduling을 위한 **엔진 소유 metadata**는 필요하다.

권장 구조:

```text
HookRuntimeState
- firedOnce: bool
- lastRunAtMs: long | null
```

규칙:
- `lastRunAtMs`는 onTick interval 판정용이다.
- script에서 직접 읽거나 쓸 수 없다.
- save/load 영속화 여부는 `12`가 소유한다.

---

## 16) 09 / 10 / 12와의 연결

### 16.1 `09`와의 연결
- `sessionId` -> `server.sessions[sessionId]`
- forensic / lineage는 `SessionKey(targetNodeId, sessionId)`를 사용한다.
- trace 관련 저장 구조(SessionHistoryStore, IncidentBufferStore, ForensicTraceStore)는 `09`가 소유한다.

### 16.2 `10`과의 연결
- YAML/blueprint 원본 스키마는 `10`이 소유한다.
- `10`은 server/spec/scenario/mission authoring을 runtime `HookBinding`으로 lowering해야 한다.
- legacy `guardContent`, `actions`를 유지해야 한다면 `10` 단계에서 script-first runtime 형태로 변환해야 한다.

### 16.3 `12`와의 연결
- active mission hook registry, once-fired metadata, onTick `lastRunAtMs`, trace/evidence persistence 경계는 `12`가 소유한다.
- 이 문서는 저장 경계를 중복 정의하지 않는다.

---

## 17) v0.1에서 v0.2로의 주요 변화

- `guardContent + actions` 중심 -> `scriptRef` 중심
- once-only 기본 -> repeat 기본, once opt-in
- server/scenario/mission이 별개 시스템 -> unified hook runtime
- handler signature `handler(evt, state, api)` -> `handler(evt, ops)`
- handler-local state 제거
- post-action semantics 명시
- fixed owner execution order 추가
- trace/evidence semantics 통합
- `sessionId`를 `09` 세션 구조와 직접 연결
- hook 내부 privileged action의 재발화 금지

---

## 18) 구현 체크리스트 (Codex)

### 18.1 core runtime
- [ ] `GameEvent(eventType, targetNodeId, timeMs, seq, payload)` 구현
- [ ] end-of-tick `Drain(now)` 구현
- [ ] tick budget(개별 0.0166s / 총 0.05s) 구현
- [ ] queue carry-over 구현 (budget 초과 시 다음 tick로 이월)

### 18.2 registry
- [ ] `HookBinding` 정규화 구조 구현
- [ ] server/scenario/mission registry 분리 구현
- [ ] `priority DESC, registrationOrder ASC` 정렬 구현
- [ ] `once` runtime metadata 구현
- [ ] onTick `lastRunAtMs` metadata 구현

### 18.3 script loader / executor
- [ ] `scriptRef`의 `path-...::func` 형태 해석
- [ ] load-time compile / function lookup / fail-fast 구현
- [ ] sidecar behavior schema 로드 + default merge + config validation 구현
- [ ] `handler(evt, cfg, ops)` 호출 규약 구현
- [ ] `evt` immutable view 구현
- [ ] `cfg` immutable view 구현
- [ ] import 비활성 정책 구현
- [ ] runtime error / timeout -> warn + non-rollback 구현

### 18.4 capability profile
- [ ] ownerKind별 namespace injection 구현
- [ ] server/scenario/mission 간 capability 분리 구현
- [ ] raw runtime state 직접 접근 금지 보장 구현

### 18.5 event emission points
- [ ] `onProcessFinished`
- [ ] `onPrivilegeAcquire`
- [ ] `onFileAcquire`
- [ ] `onAuthAttempt`
- [ ] `onAuthSuccess`
- [ ] `onAuthFail`
- [ ] `onSessionOpen`
- [ ] `onSessionClose`
- [ ] `onNetScan`
- [ ] `onNetPorts`
- [ ] `onSshInspect`
- [ ] `onFileRead`
- [ ] `onFileWrite`
- [ ] `onFileDelete`
- [ ] `onFtpTransfer`
- [ ] `onTick`

### 18.6 ordering semantics
- [ ] auth success 시 `Attempt -> Success -> SessionOpen` 순서 구현
- [ ] auth fail 시 `Attempt -> Fail` 순서 구현
- [ ] drain 시 `server -> scenario -> mission` 순서 구현
- [ ] hook 내부 privileged action의 재발화 금지 구현

### 18.7 09 bridge
- [ ] `evt.sessionId` -> `server.sessions[sessionId]` 연결 구현
- [ ] `SessionKey(targetNodeId, sessionId)` 승격 helper 구현
- [ ] `09`의 SessionHistory / IncidentBuffer / ForensicTrace runtime과 정합성 확인

### 18.8 trace/evidence
- [ ] `trace.startHotFromSession`
- [ ] `trace.startForensicFromSession`
- [ ] `trace.markEvidence`
- [ ] `trace.hasEvidence`
- [ ] `trace.consumeEvidence`
- [ ] evidence set semantics 구현
- [ ] `kind` 생략 시 any/all semantics 구현

---

## 19) deferred / open questions

아래는 v1 범위 밖으로 미룬다.

- `onDaemonEvent`
- `onNetBanner`
- `onFileList`
- `onFileStat`
- `onCommandExec`
- `event.emit(...)`
- hook import / hook 전용 stdlib
- persistent `slot(...)`
- `match`의 고급 연산(range / regex / predicate)
- exact persistence 경계와 migration
- mission board / mission lifecycle schema (future owner doc)

---

## 20) 최종 구현 방향 한 줄 요약

v0.2의 `11`은 기존의 guard/action 기반 이벤트 문서를 유지보수하는 문서가 아니라,
**서버 방어 시스템, 시나리오 반응, 미션 완료 판정을 하나의 post-action, script-first, capability-separated hook runtime으로 통합하는 문서**다.
