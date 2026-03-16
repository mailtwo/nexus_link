# 05 — Mission Blueprint and Board Schema v0.1 (Alpha SSOT Draft)

Purpose: Mission authoring schema, mission lifecycle, mission board listing semantics, side mission template selection at world initialization, and mission-runtime behavior contract.
Keywords: mission blueprint, mission template, side mission, mission board, mission lifecycle, mission progress, contract board, story mission, side mission template, mission rewards, mission hooks
Aliases: mission system, contract system, mission manager

이 문서는 **미션 시스템의 구체 규칙**을 정의한다.
Codex는 이 문서를 기준으로 다음을 구현할 수 있어야 한다.

- Story mission / side mission의 공통 런타임 모델
- MissionTemplate 기반 side mission 선택 / concrete materialization
- MissionBoard listing / reveal / accept 동작
- Mission lifecycle 상태 전이
- unified hook system(`11`)과 연결되는 mission handler 등록/해제
- mission-local progress 저장과 hook-driven completion/failure
- 미션 수락 시 서버 발견(reveal) 처리
- save/load에 필요한 미션 런타임 의미론

이 문서는 `05` 후보 문서이며, `DOCS_INDEX` 갱신 시 Tier 1 SSOT로 편입되는 것을 전제로 작성한다.

---

## 관련 문서와 소유권 경계

이 문서는 아래를 **소유한다**.

- MissionBlueprint / MissionTemplate의 authoring schema
- MissionEntryPolicy(board/auto) 규칙
- mission lifecycle 상태 집합과 상태 전이 규칙
- mission-local progress와 completion/failure 의미론
- mission board listing / 노출 / 수락 의미론
- story mission / side mission 공존 규칙
- side mission template selection / materialization 의미론
- 미션 수락/자동 시작 시 reveal 처리 규칙

이 문서는 아래를 **소유하지 않는다**.

- hook runtime dispatch / payload / ordering / reentrancy 규칙 → `11`
- privileged intrinsic의 함수 시그니처 / ResultMap / 에러 코드 → `03`
- mission/server가 사용하는 runtime storage field 이름 / 저장 컨테이너 구조 → `09`
- exact save file format / persistence boundary → `12`
- 플레이어 경험상 라이선스/온보딩 흐름 → `15`
- 미션의 서사적 소재 / 공격 루트 재미 / 힌트 시스템의 디자인 의도 → `04`
- scenario/world generation의 일반 스키마 → `10`

참조 관계:
- Canonical hook runtime: See DOCS_INDEX -> 11
- Canonical intrinsic surface: See DOCS_INDEX -> 03
- Canonical runtime storage owner: See DOCS_INDEX -> 09
- Canonical save/load owner: See DOCS_INDEX -> 12
- Related context (mission design intent): See DOCS_INDEX -> 04
- Related context (progression flow): See DOCS_INDEX -> 15
- Related context (scenario/blueprint pipeline): See DOCS_INDEX -> 10

`05`와 `10`의 handoff는 아래처럼 고정한다.
- `05`는 **mission file 자체**(`MissionBlueprint` / `MissionTemplate`)의 YAML schema와,
  그 파일이 concrete mission runtime으로 materialize된 뒤의 lifecycle / progress / completion semantics를 소유한다.
- `10`은 scenario/campaign blueprint가 **어떤 mission file set을 참조하는지**,
  그리고 world generation / initialization pipeline에서 그 mission file들을 언제 로딩해
  `05`의 selection/materialization pass에 넘기는지의 entrypoint를 소유한다.
- 즉 `10`은 mission content의 **참조/연결 지점** owner이고,
  `05`는 mission content **파일 내부 구조와 runtime 의미론** owner다.

---

## 0) 핵심 결정 요약

- 미션과 서버는 분리한다.
  - 서버는 자기 보안/구성/데이터를 소유한다.
  - 미션은 “플레이어가 무엇을 해야 하는가”만 소유한다.
- 미션 완료는 **generic hook-driven model**로 처리한다.
  - 엔진 수준의 별도 `completionMode`는 두지 않는다.
  - 미션 handler script가 직접 `mission.complete()` / `mission.fail()`를 호출한다.
- 복합 조건(AND)은 hook 하나로 해결하지 않고 **mission-local progress**로 해결한다.
  - handler가 `mission.setProgress()`를 호출하고,
  - 최종 handler가 `mission.hasProgress()`를 확인한 뒤 `mission.complete()`를 호출한다.
- 다단계 미션은 checkpoint가 아니라 **연계 미션 체인**으로 푼다.
  - 한 미션 완료 후 다음 미션이 `entryPolicy.kind=auto`로 시작된다.
  - 알파 버전에서는 범용 `mission.start(nextMissionId)`를 요구하지 않는다.
- 미션 진입 방식은 **`MissionEntryPolicy` discriminated union**으로 정의한다.
  - `kind=board` → `revealWhen`, `acceptWhen`, `revealOnAccept`
  - `kind=auto` → `startWhen`, `revealOnStart`
- Story mission과 side mission은 같은 런타임 모델을 공유한다.
  - story mission: concrete MissionBlueprint 사용
  - side mission: MissionTemplate을 world initialization 시 concrete mission 집합으로 선택 / 확장
- 모든 mission은 **현재 플레이스루에서 1회용 concrete mission**이다.
  - mid-game에 새 mission을 생성하거나 refill하지 않는다.
- 알파 버전에서는 mission-linked server group도 world initialization 시 함께 materialize한다.
  - 즉, side mission의 concrete target binding은 게임 시작부터 확정된다.
  - mission board는 새 서버나 새 mission을 생성하지 않고, **이미 생성된 concrete mission을 표시**한다.
- 미션 스크립트는 unified hook system(`11`) 위에서 실행되며, 알파 capability는 아래로 제한한다.
  - 허용: `flags.*`, `mailbox.*`, `world.*`, `mission.*`, `reward.*`
  - 금지: `server.*`, `trace.*`, `import`
- mission-local progress는 미션 runtime state에 저장한다.
  - `scenarioFlags`는 cross-system gating용으로 유지하되, 미션 내부 진행 저장소로 남용하지 않는다.

---

## 1) 핵심 개념

### 1.1 MissionBlueprint
Concrete mission authoring unit.
이미 concrete target binding이 끝난 미션 정의다.
주로 story mission에 사용한다.

### 1.2 MissionTemplate
Side mission generator input.
슬롯 기반 서버 요구사항과 display/reward/hook authoring을 정의한다.
world initialization 시 concrete mission record로 확장된다.

### 1.3 Concrete Mission Record
런타임에서 실제로 존재하는 mission 단위.
story mission은 authoring 시점부터 concrete이고,
side mission은 MissionTemplate selection/materialization 결과로 concrete가 된다.

Concrete mission record는 최소한 아래를 포함한다.
- stable `missionId`
- bound node/file/account references
- resolved display strings
- resolved reward values
- resolved handler bindings
- `entryPolicy`
- runtime lifecycle state

### 1.4 MissionManager
미션 시스템의 런타임 소유자.
최소 역할:
- concrete mission registry 보유
- lifecycle 상태 전이 수행
- board entry reevaluation 및 현재 board listing 계산
- active mission handler 등록/해제
- mission-local progress 읽기/쓰기
- accept / abandon / complete / fail 처리
- reveal 처리
- save/load 연계용 runtime snapshot 제공

### 1.5 Mission-local progress
미션 하나 안에서만 유효한 로컬 상태 저장소.
복합 조건, turn-in, 보고 메일, 부분 성공 여부를 관리한다.

알파 canonical model은 아래로 고정한다.

```text
MissionProgressStore
- Dictionary<string, ProgressValue>

ProgressValue
- bool
- int
- string
```

규칙:
- progress key는 생성 전까지 존재하지 않으며, 타입도 없다.
- 어떤 key의 **첫 `mission.setProgress(key, value)` 호출이 그 key의 타입을 확정**한다.
- 이미 존재하는 key는 overwrite를 허용한다.
  - 단, 이후 `setProgress` 값의 타입이 최초 확정 타입과 다르면 API error다.
- `mission.getProgress(key)`는 key가 없으면 API error를 반환한다.
  - runtime exception을 던지는 방식으로 처리하지 않는다.
- `mission.hasProgress(key)`는 key 존재 여부만 확인한다.
- 알파에서는 개별 key 삭제 API를 두지 않는다.
  - progress reset은 abandon / retry / mission restart 같은 lifecycle 전이에서 MissionManager가 store 전체를 초기화하는 방식으로 처리한다.

의도:
- `bool`은 단계 완료 플래그에 사용한다.
- `int`는 누적 횟수/카운팅 조건에 사용한다.
- `string`은 discrete phase/state label에 사용한다.

예:
- `root_ok = true`
- `artifact_ok = true`
- `upload_count = 2`
- `report_state = "sent"`

---

## 2) 서버와 미션의 분리

미션 시스템은 서버 스펙의 내부 보안 구현을 모른다는 전제를 유지한다.

```text
ServerSpecBlueprint  -> "나는 이런 서버다" (방어 / 포트 / 계정 / 데이터)
ServerSpawnBlueprint -> "이 시나리오에서 이 서버를 이렇게 배치한다"
MissionBlueprint     -> "플레이어에게 이런 일을 시킨다"
MissionTemplate      -> "이런 형태의 랜덤 계약을 만든다"
```

중요 원칙:
- 미션은 서버가 OTP인지, firewall인지, trace rule이 무엇인지 알 필요가 없다.
- 그런 정보는 플레이어가 정찰/실험/공략으로 알아내야 한다.
- 미션은 target node/file/account를 참조할 뿐이다.

---

## 3) Mission authoring 모델

## 3.1 MissionBlueprint (concrete)

```yaml
story_root_breach_01:
  sourceKind: story
  display:
    title: "내부 관리자 계정 탈취"
    briefing: "의뢰인이 {target_server.hostname}의 관리자 세션 확보를 원합니다."
    requesterMail: "broker@contracts.net"

  entryPolicy:
    kind: board
    revealWhen:
      - licenseMin: 1
      - flagCheck: { key: "intro_done", op: "eq", value: true }
    acceptWhen:
      - serverAlive: { nodeId: "$target_server" }
    revealOnAccept:
      nodes: ["$target_server"]
      nets: []

  settings:
    allowAbandon: true
    allowRetryOnFail: false
    timeLimitSec: null

  bindings:
    target_server: node_story_corp_admin_01

  handlers:
    - id: mark_root_ok
      event: onAuthSuccess
      match:
        nodeId: "$target_server"
        userKey: root
      scriptRef: path-missions/story_root_breach.ms::markRoot

    - id: complete_on_report
      event: onMailSent
      match: {}
      scriptRef: path-missions/story_root_breach.ms::completeOnReport

  rewards:
    credits: 5000
    flags:
      - { key: "story_root_breach_01_done", value: true }
```

### 3.1.1 필드 설명
- `sourceKind`: `story | side`
- `display`: title / briefing / requester 표시 텍스트
- `entryPolicy`: `board` 또는 `auto`
- `settings.allowAbandon`: board mission active 상태에서 포기 허용 여부
- `settings.allowRetryOnFail`: 실패 후 재도전 허용 여부 (알파 기본 false)
- `settings.timeLimitSec`: active 진입 후 제한 시간(없으면 null)
- `bindings`: concrete mission의 binding namespace (`bindingKey -> concrete value`)
- `handlers`: unified hook system(`11`) 위에 등록되는 mission-owned handlers
- `rewards`: board/UI에 미리 표시할 수 있는 **기본 완료 보상**
  - `active -> completed` 전이에서 MissionManager가 정확히 1회 자동 적용한다.

### 3.1.2 MissionBlueprint의 원칙
- concrete mission은 이미 concrete node/file/account 참조가 확정되어 있다.
- story mission은 보통 이 형식을 직접 사용한다.
- side mission도 world initialization 후에는 내부적으로 concrete MissionBlueprint와 같은 형태가 된다.

---

## 3.2 MissionTemplate (side mission generator input)

```yaml
file_theft_basic:
  sourceKind: side

  slots:
    target_server:
      specPool: [corp_ftp_small, corp_ftp_medium]
      requirements:
        - hasPort: 21
        - hasFilePattern: "/data/*"

  generatedBindings:
    client:
      type: string
      valuePool:
        - "Blue Finch Logistics"
        - "Aster Vault Procurement"

  display:
    titlePool:
      - "내부 문서 유출 의뢰"
      - "기밀 파일 확보 건"
    briefingTemplate: "{client}가 {target_server.hostname}의 내부 문서를 원합니다."
    requesterMailPool:
      - "broker@contracts.net"
      - "ops@shadowmail.net"

  entryPolicy:
    kind: board
    revealWhen:
      - licenseMin: 1
    acceptWhen:
      - serverAlive: { nodeId: "$target_server" }
    revealOnAccept:
      nodes: ["$target_server"]
      nets: []

  settings:
    allowAbandon: true
    allowRetryOnFail: false
    timeLimitSec: null

  handlers:
    - id: mark_artifact
      event: onFileAcquire
      match:
        fromNodeId: "$target_server"
      scriptRef: path-missions/file_theft.ms::markArtifact

    - id: complete_on_turnin_mail
      event: onMailSent
      match: {}
      scriptRef: path-missions/file_theft.ms::completeOnMail

  rewards:
    creditsRange: [3000, 5000]
    flags: []
```

### 3.2.1 MissionTemplate의 역할
- slot 기반으로 “어떤 종류의 서버/파일/타깃이 필요한가”를 정의한다.
- world initialization 시 선택되면 concrete target group과 concrete display/reward 값으로 확장된다.
- 이후 런타임에서는 concrete mission처럼 동작한다.

### 3.2.2 slots
`slots`는 템플릿 내부 symbolic binding 자리다.

예:
- `$target_server`
- `$relay_server`
- `$db_server`

각 슬롯은 최소한 아래를 정의할 수 있다.
- `specPool`: 사용할 서버 스펙 후보군
- `requirements`: 스펙/초기 데이터/포트/파일 관련 필수 조건

알파 권장 requirements:
- `hasPort: <portNum>`
- `hasFilePattern: <glob>`
- `hasUser: <userKey or auth category>`
- `roleIs: <server role>`

### 3.2.3 generatedBindings

`generatedBindings`는 side mission template materialization 시점에 함께 확정되는
**template-local scalar binding** 정의다.

예:

```yaml
generatedBindings:
  client:
    type: string
    valuePool:
      - "Blue Finch Logistics"
      - "Aster Vault Procurement"
```

규칙:
- `generatedBindings`의 key는 `slots`와 같은 binding namespace를 공유한다.
  - 같은 key를 `slots`와 `generatedBindings`에 동시에 선언하면 load error다.
- 알파에서는 `generatedBindings.*.type`으로 `string`만 지원한다.
- `valuePool`은 비어 있을 수 없다.
- materialization 시 각 key는 deterministic sampling으로 **concrete string value** 하나로 확정된다.
- materialization이 끝난 concrete mission record에는
  slot 기반 concrete ref와 generated string binding이 모두 하나의 binding namespace로 저장된다.

### 3.2.4 Binding / interpolation grammar (alpha)

mission authoring은 **공통 binding namespace**를 사용한다.

- story mission:
  - `bindings:`가 이미 concrete value를 직접 가진다.
- side mission template:
  - `slots:`가 world에서 선택되는 ref binding을 정의한다.
  - `generatedBindings:`가 world와 무관한 generated scalar binding을 정의한다.
  - materialization 후 concrete mission은 둘을 합친 binding namespace를 가진다.

알파 문법:

```text
BindingKey           = [A-Za-z_][A-Za-z0-9_]*
BindingProperty      = [A-Za-z_][A-Za-z0-9_]*
SlotRef              = "$" BindingKey
InterpolationExpr    = "{" BindingKey [ "." BindingProperty ] "}"
```

지원 binding kind:
- `node`
  - concrete value는 nodeId string이다.
  - slot 기반 server binding은 알파에서 모두 `node` kind로 본다.
- `string`
  - concrete value는 plain string이다.
  - `generatedBindings.*.type = string`이 이 kind를 만든다.

허용 위치:
- `SlotRef`
  - **node-ref를 기대하는 필드에서만** 허용한다.
  - 알파에서 명시적으로 허용하는 위치:
    - rule operand의 `nodeId` / `fromNodeId` / `toNodeId`
    - `revealOnAccept.nodes[]`
    - `revealOnStart.nodes[]`
  - 그 외 위치에서 `$target_server` 같은 token이 나오면 load error다.
- `InterpolationExpr`
  - **display 문자열 필드에서만** 허용한다.
  - 알파에서 허용하는 위치:
    - `display.title`
    - `display.briefing`
    - `display.requesterMail`
    - `display.titlePool[]`
    - `display.briefingTemplate`
    - `display.requesterMailPool[]`
  - handler `config`, rule operand, path, reward field에는 interpolation을 허용하지 않는다.

속성 접근 규칙:
- `{bindingKey}`
  - `string` binding에 허용한다.
  - `node` binding에도 허용할 수 있으나, 알파 canonical form은 `{bindingKey.nodeId}`를 권장한다.
- `{bindingKey.property}`
  - `node` binding에만 허용한다.
  - 알파 허용 property는 아래 3개로 제한한다.
    - `nodeId`
    - `hostname`
    - `ip`
  - 그 외 property는 load error다.
- `string` binding에 property 접근(`{client.name}` 등)은 허용하지 않는다.

문자열 규칙:
- 하나의 문자열 안에 interpolation expression을 0개 이상 포함할 수 있다.
- nested expression, indexing, function call, arithmetic는 지원하지 않는다.
- `{{`와 `}}`는 각각 literal `{`, `}`로 escape된다.
- escape되지 않은 잘못된 `{...}` 패턴이나 unmatched brace는 load error다.

검증 / 해석 시점:
- story mission load:
  - placeholder 문법 검사
  - 허용 위치 검사
  - `bindings` key 존재 여부 검사
  - binding kind / property 호환성 검사
  - 실패 시 mission load error
- side mission template load:
  - placeholder 문법 검사
  - 허용 위치 검사
  - referenced key가 `slots` 또는 `generatedBindings`에 존재하는지 검사
  - slot kind / property compatibility 검사
  - 실패 시 template load error
- side mission materialization:
  - 모든 `SlotRef`는 concrete ref로 lowering한다.
  - 모든 display interpolation은 concrete string으로 eager render한다.
  - materialization 이후 concrete mission runtime data에 unresolved `$...` / `{...}`가 남아 있으면 load error다.

### 3.2.5 requirements rule grammar (selection-time)

`requirements`는 side mission template materialization 시점의 **candidate selection rule**이다.

```yaml
slots:
  target_server:
    specPool: [corp_ftp_small, corp_ftp_medium]
    requirements:
      - hasPort: 21
      - hasFilePattern: "/data/*"
      - roleIs: gateway
```

규칙:
- `requirements`는 **AND 리스트**다.
  - 리스트의 모든 rule이 참이어야 해당 candidate가 통과한다.
- 알파에서는 `OR` / `anyOf` / nested boolean tree를 지원하지 않는다.
- 각 rule item은 **top-level key 1개만 가지는 tagged union**이어야 한다.
  - 예: `- hasPort: 21`
  - 예: `- hasFilePattern: "/data/*"`
  - 잘못된 예: `- { hasPort: 21, roleIs: gateway }`
- `requirements`는 **selection-time only**다.
  - world initialization/materialization 시점에만 평가하고, runtime 재평가하지 않는다.
- 알파에서는 `requirements` 안에서 `$slot` 참조를 허용하지 않는다.
  - `requirements`는 “현재 slot candidate 자체의 속성”만 검사한다.
  - slot 간 상호의존 selection은 deferred다.

---

## 4) MissionEntryPolicy (중요)

미션 시작 조건은 entry policy로 묶는다.

두 branch는 **상호배타적**이다.

```text
MissionEntryPolicy
- kind: ENUM { board, auto }
```

### 4.1 `kind = board`
보드에 등장하고 플레이어가 수락해서 시작하는 미션.

```yaml
entryPolicy:
  kind: board
  revealWhen:
    - ...
  acceptWhen:
    - ...
  revealOnAccept:
    nodes: [...]
    nets: [...]
```

의미:
- `revealWhen`을 만족하면 board candidate가 된다.
- `acceptWhen`을 만족하면 수락 가능 상태가 된다.
- 실제 `active` 진입은 **플레이어 accept 시점**이다.
- `revealOnAccept`는 accept 즉시 world에 공개할 node/net을 정의한다.

### 4.2 `kind = auto`
보드를 거치지 않고 조건 만족 시 즉시 시작하는 미션.

```yaml
entryPolicy:
  kind: auto
  startWhen:
    missionCompleted: story_root_breach_01
  revealOnStart:
    nodes: ["$next_target"]
    nets: []
```

의미:
- `startWhen`을 만족하는 순간 바로 `active`가 된다.
- accept 단계가 없다.
- `revealOnStart`는 active 전환 직후 world에 공개할 node/net을 정의한다.

### 4.3 Alpha의 `startWhen` 최소 지원
알파 버전에서는 `auto.startWhen`을 아래 하나만 필수 지원으로 본다.

```yaml
startWhen:
  missionCompleted: <missionId>
```

즉, 연계 미션 체인의 기본 케이스를 공식 지원한다.

후속 확장 후보:
- `flagCheck`
- `licenseMin`
- composite start rule
- script-based start guard

### 4.4 왜 entry policy를 union으로 묶는가
- board 미션은 `revealWhen/acceptWhen`이 필요하고 `startWhen`은 필요 없다.
- auto 미션은 `startWhen`이 필요하고 `revealWhen/acceptWhen`은 필요 없다.
- nullable 필드를 섞어 두면 잘못된 조합이 쉽게 생긴다.
- union으로 두면 authoring / validation / runtime code가 단순해진다.

### 4.5 Rule grammar (runtime predicates)

`revealWhen`, `acceptWhen`, `failWhen`은 공통 rule list grammar를 사용한다.

```yaml
revealWhen:
  - licenseMin: 1
  - flagCheck: { key: "intro_done", op: "eq", value: true }

acceptWhen:
  - serverAlive: { nodeId: "$target_server" }

failure:
  failWhen:
    - serverNotAlive: { nodeId: "$target_server" }
    - fileMissing: { nodeId: "$target_server", path: "/data/secret.doc" }
```

규칙:
- `revealWhen`, `acceptWhen`, `failWhen`은 모두 **AND 리스트**다.
- 알파에서는 `OR` / `anyOf` / nested boolean tree를 지원하지 않는다.
- 각 rule item은 **top-level key 1개만 가지는 tagged union**이어야 한다.
- `startWhen`은 예외적으로 알파에서 **single rule object**로 제한한다.
  - 현재 공식 지원은 `missionCompleted: <missionId>` 하나다.
  - composite `startWhen`은 deferred다.
- rule에 `$slot`이 들어갈 수 있는 위치에서는 authoring 단계에서 허용한다.
  - 단, runtime evaluator가 `$slot`를 직접 해석해서는 안 된다.
  - 모든 `$slot` 참조는 world initialization/materialization/load 시점에 concrete ref로 resolve되어야 한다.
  - runtime에 unresolved `$slot`가 남아 있으면 load error다.

### 4.6 reveal / accept / start 평가 의미론

- `revealWhen`
  - board mission의 **노출 조건**이다.
  - `hidden` 상태에서만 평가한다.
  - 한 번 참이 되어 `hidden -> visible`이 되면 **latched**로 본다.
  - 이후 `revealWhen`이 다시 false가 되어도 `visible -> hidden`으로 되돌아가지 않는다.

- `acceptWhen`
  - 이미 노출된 board mission의 **현재 수락 가능 조건**이다.
  - `visible` 또는 `available` 상태에서만 평가한다.
  - true면 `available`, false면 `visible`이다.
  - 따라서 `visible <-> available` 왕복은 허용한다.

- `startWhen`
  - auto mission의 **자동 시작 조건**이다.
  - `hidden` 상태 auto mission에서만 평가한다.
  - true가 되면 즉시 `active`로 전환하고, 이후 재평가하지 않는다.

### 4.7 Event-driven reevaluation (MissionManager watchers)

알파에서는 `revealWhen/acceptWhen/startWhen/failWhen`을 매 tick polling하지 않는다.
대신 MissionManager가 rule dependency를 **watch key**로 컴파일하고,
relevant state change가 발생했을 때 영향받을 수 있는 mission만 재평가한다.

권장 구조:

```text
RuleWatchKey
- license
- flag:<flagKey>
- missionCompleted:<missionId>
- nodeAlive:<nodeId>
- fileExists:<nodeId>:<path>
```

```text
revealWatchersByKey
acceptWatchersByKey
startWatchersByKey
failWatchersByKey
```

규칙:
- world initialization/materialization/load 시점에 MissionManager는 rule을 concrete ref 기준 watch key로 컴파일한다.
- 어떤 world state change가 발생하면, MissionManager는 **해당 watch key를 구독한 mission만** 재평가한다.
- “상태가 하나 바뀔 때마다 모든 mission을 재검사”하는 방식은 알파 canonical model이 아니다.
- watcher 등록은 mission lifecycle state에 따라 달라진다.
  - `hidden` board mission -> `revealWatchersByKey`
  - `visible` / `available` board mission -> `acceptWatchersByKey`
  - `hidden` auto mission -> `startWatchersByKey`
  - `active` mission -> `failWatchersByKey`
- mission state가 바뀌면 기존 watcher 등록을 해제하고 새 상태에 맞는 watcher 집합으로 재등록한다.

대표 relevant state change:
- license 변경 -> `license`
- `flags.set/unset` -> `flag:<key>`
- mission terminal state 변경 -> `missionCompleted:<missionId>`
- node online/offline 변경 -> `nodeAlive:<nodeId>`
- 특정 `(nodeId, path)`의 존재 상태 변경 -> `fileExists:<nodeId>:<path>`

알파 구현 원칙:
- MissionManager는 affected watch key bucket에 걸린 mission의 **관련 clause만 재평가**하면 충분하다.
- predicate truth cache나 범용 hook system 재사용은 필수가 아니다.

---

## 5) Mission lifecycle

알파 canonical state는 아래 6개로 고정한다.

```text
MissionState
- hidden
- visible
- available
- active
- completed
- failed
```

### 5.1 상태 의미
- `hidden`
  - concrete mission은 존재하지만 플레이어에게 아직 노출되지 않음
- `visible`
  - board에는 보이지만 아직 수락 가능 조건을 만족하지 않음
- `available`
  - board에 보이며 수락 가능함
- `active`
  - 플레이어가 수락했거나(auto mission이면 자동 시작) 현재 진행 중
- `completed`
  - 성공 완료
- `failed`
  - 실패 완료(terminal)

### 5.2 상태 전이

#### board 미션
```text
hidden -> visible -> available -> active -> completed
                             \-> active -> failed
```

세부 규칙:
- `hidden -> visible`
  - `revealWhen == true` 이고 board entry candidate가 될 수 있을 때
  - 이 전이는 latched다. 이후 revealWhen이 false가 되어도 `visible -> hidden`으로 되돌아가지 않는다.
- `visible -> available`
  - `acceptWhen == true`
- `available -> visible`
  - acceptWhen이 다시 false가 되면 visible로 되돌아갈 수 있다
- `available -> active`
  - 플레이어가 accept
- `active -> completed`
  - mission script가 `mission.complete()` 호출
- `active -> failed`
  - mission script가 `mission.fail()` 호출
  - 또는 시간 제한 만료
  - 또는 explicit fail condition 만족

#### auto 미션
```text
hidden -> active -> completed
                 \-> failed
```

### 5.3 Abandon은 canonical state가 아니라 operation으로 처리한다
알파에서는 `abandoned`를 별도 canonical state로 두지 않는다.

이유:
- board 미션에서 포기는 대개 “진행 중이던 계약을 내려놓고 나중에 다시 잡는 행위”에 가깝다.
- terminal state로 저장할 필요 없이, progress reset 후 `available`(또는 `visible`)로 되돌리면 된다.

규칙:
- `settings.allowAbandon == true` 인 board mission만 abandon 가능
- abandon 시:
  - mission-local progress reset
  - timer reset
  - state는 `available` 또는 `visible`로 되돌림
    - `acceptWhen == true`면 `available`
    - 아니면 `visible`
- auto mission은 알파에서 abandon 불가

### 5.4 Retry on fail
알파 기본값은 `allowRetryOnFail = false`다.

규칙:
- `failed`는 terminal state다.
- `allowRetryOnFail = true`인 board mission만 예외적으로 reset 후 `available/visible`로 되돌릴 수 있다.
- auto mission은 알파에서 fail retry를 공식 지원하지 않는다.

---

## 6) Mission completion / failure model

## 6.1 Generic hook-driven completion
미션 완료는 엔진 고정 규칙으로 판정하지 않는다.

대신:
- mission-owned handler가 unified hook(`11`)에서 payload를 받는다.
- script가 payload와 mission-local progress를 검사한다.
- 조건이 맞으면 `mission.complete()` 또는 `mission.fail()`를 호출한다.

즉 “이메일 보고형”과 “실시간 감시형”은 엔진 필드가 아니라 **서로 다른 hook script authoring 패턴**이다.

예:
- `onAuthSuccess`에서 target root 로그인 성공 시 바로 `mission.complete()`
- `onFileAcquire`에서 progress를 쌓고, `onMailSent`에서 첨부/수신자를 확인한 뒤 `mission.complete()`

### 6.2 Mission-local progress API (alpha minimum)
이 문서는 `03`에 아래 mission module surface가 필요하다고 요구한다.

- `mission.complete()`
- `mission.fail(reason?)`
- `mission.setProgress(key, value)`
- `mission.getProgress(key)`
- `mission.hasProgress(key)`

후속 확장 후보:
- `mission.clearProgress(key)`
- `mission.listProgressKeys()`

### 6.3 Active-state handler registration
중요 규칙:
- mission handlers는 **mission이 active일 때만 등록/활성**된다.
- `hidden/visible/available/completed/failed` 상태의 미션은 hook를 소비하지 않는다.

이유:
- 미션이 아직 시작되지 않았는데 완료 trigger가 발동하면 안 된다.
- board reveal/accept 조건은 handler가 아니라 MissionManager의 entry policy reevaluation으로 처리한다.

### 6.4 Reward application model

보상은 **공개 기본 보상**과 **숨겨진 추가 보상**으로 나눈다.

- `rewards:` 블록은 board/UI가 플레이어에게 미리 보여줄 수 있는 **공개 기본 보상**이다.
- story mission은 concrete reward 값을 직접 authoring한다.
- side mission은 template authoring(`creditsRange` 등)에서 시작하더라도,
  world initialization/materialization 시점에 **concrete reward value**로 확정된다.
- MissionManager는 `active -> completed` 전이에서 authored `rewards:`를 **정확히 1회 자동 적용**한다.
- mission script는 기본 보상을 직접 지급하지 않는다.
  - script의 책임은 완료/실패 판정(`mission.complete()` / `mission.fail()`)이다.
  - 기본 보상 집행은 MissionManager의 lifecycle 처리 책임이다.
- `reward.*`는 board/UI에 미리 노출하지 않는 **숨겨진 추가 보상 / bonus payout**에만 사용한다.
  - 예: stealth bonus, speed bonus, optional artifact bonus
- `reward.*`는 `rewards:`를 대체하지 않는다.
  - 즉, “기본 완료 보상 전체를 script가 직접 지급”하는 모델은 alpha canonical model이 아니다.
- alpha에서는 hidden bonus의 중복 지급을 막는 별도 문법/런타임 가드를 두지 않는다.
  - 따라서 기획자는 `mission.hasProgress()` / `mission.setProgress()` 같은 mission-local progress로
    bonus를 1회만 지급하도록 스스로 보장해야 한다.

### 6.5 onMailSent 의존성
알파 미션 시스템은 `onMailSent` hook를 필요로 한다.

대표 사용 사례:
- turn-in report 메일 완료
- 첨부 파일 전달 완료
- 특정 수신자에게 특정 template 또는 결과물을 보내는 완료 판정

`onMailSent`의 runtime semantics / payload owner는 `11`이다.
이 문서는 해당 hook가 필요하다는 요구사항만 명시한다.

권장 검사 대상:
- recipient
- subject / templateId
- attachment content ids
- mission-local progress 선행 조건

### 6.6 예시: 파일 탈취 + 보고 메일 미션

```yaml
handlers:
  - id: mark_artifact
    event: onFileAcquire
    match:
      fromNodeId: "$target_server"
    scriptRef: path-missions/file_theft.ms::markArtifact

  - id: complete_on_turnin_mail
    event: onMailSent
    match: {}
    scriptRef: path-missions/file_theft.ms::completeOnMail
    config:
      requesterMail: "broker@contracts.net"
      expectedTemplateId: "mission_turnin_default"
      requiredAttachment: "res://scenario_content/resources/text/executions/password_breaker.ms"
```

비고:
- 위 `config.requiredAttachment`는 authoring 시점에는 `contentRef`다.
- runtime script가 받는 `cfg.requiredAttachment`는 `11`의 config normalization을 거친 **resolved contentId**다.

```miniscript
# markArtifact(evt, cfg, ops)
mission.setProgress("artifact_ok", true)
```

```miniscript
# completeOnMail(evt, cfg, ops)
if not mission.hasProgress("artifact_ok") then return end if
if evt.toAddress != cfg.requesterMail then return end if
if evt.templateId != cfg.expectedTemplateId then return end if
if not evt.attachmentContentIds.contains(cfg.requiredAttachment) then return end if
mission.complete()
```

### 6.7 예시: 실시간 감시형 미션

```yaml
handlers:
  - id: complete_on_root_auth
    event: onAuthSuccess
    match:
      nodeId: "$target_server"
      userKey: root
    scriptRef: path-missions/root_breach.ms::completeNow
```

```miniscript
mission.complete()
```

---

## 7) Failure semantics

미션 실패는 아래 세 가지 경로를 공식 지원한다.

### 7.1 Script-driven fail
mission handler가 명시적으로 `mission.fail(reason?)`를 호출한다.

예:
- wrong recipient에 결과물 전송
- forbidden target server 변조
- 특정 민감 파일 삭제

### 7.2 Time limit fail
`settings.timeLimitSec != null`인 경우:
- active 진입 시 `expiresAt = now + timeLimitSec`
- `now >= expiresAt`이면 즉시 failed
- fail reason은 `expired`

알파 기본값은 `null`이며, 대부분의 미션은 시간 제한이 없다.

### 7.3 Explicit fail conditions
미션 정의가 아래를 가지는 경우:

```yaml
failure:
  failWhen:
    - serverNotAlive: { nodeId: "$target_server" }
    - fileMissing: { nodeId: "$target_server", path: "/data/secret.doc" }
```

MissionManager는 relevant world-state change 시 이 조건을 평가해 failed로 전환한다.

알파 권장 fail rules:
- `serverNotAlive`
- `fileMissing`
- `flagCheck`

주의:
- `failWhen`은 `active` 상태에서만 의미가 있다.
- `failWhen` 재평가는 MissionManager의 event-driven watcher 모델로 수행한다.
  - 해당 fail rule이 구독한 watch key가 바뀐 경우에만 재평가한다.
- `visible/available` 상태에서 타깃이 먼저 죽었다면, acceptWhen/revealWhen이 false로 풀리거나 별도 content rule로 처리한다.

---

## 8) Reveal 처리

미션은 world discovery와 직접 연결된다.

### 8.1 board 미션
`entryPolicy.kind = board`인 경우:
- accept 직후 `revealOnAccept`를 적용한다.
- 권장 동작:
  - `nodes[]`는 `world.revealNode(nodeId)`
  - `nets[]`는 `world.revealNet(netId)`

### 8.2 auto 미션
`entryPolicy.kind = auto`인 경우:
- start 직후 `revealOnStart`를 적용한다.

### 8.3 왜 reveal을 entryPolicy branch에 붙이는가
- board 미션은 accept 시 reveal되는 것이 자연스럽다.
- auto 미션은 start 시 reveal되는 것이 자연스럽다.
- 공용 top-level 필드로 두면 timing ambiguity가 생긴다.

### 8.4 인터넷 서버 발견 모델과의 연결
미션 시스템은 인터넷 서버 발견을 “기본 노출”이 아니라 “미션/브리핑/DNS를 통한 점진 노출”로 본다.

알파 권장:
- mission-linked private/public target은 기본적으로 known 아님
- board accept 또는 auto start에서 reveal 패키지를 통해 known으로 전환
- 완료 후 known 상태는 유지 가능

관련 맥락: `10`의 발견 모델 재정의 필요

---

## 9) Story mission과 side mission의 공존

### 9.1 공통 원칙
- story와 side는 같은 concrete mission runtime model을 사용한다.
- 둘 다 같은 lifecycle / handler / reward / reveal semantics를 쓴다.
- 차이는 “어떻게 concrete mission이 만들어졌는가”에 있다.

### 9.2 Story mission
- concrete MissionBlueprint를 직접 authoring
- 특정 서버/노드/파일에 강하게 바인딩 가능
- `entryPolicy.kind = board` 또는 `auto` 모두 허용

### 9.3 Side mission
- MissionTemplate에서 world initialization 시 일부가 선택되어 concrete mission으로 materialize된다.
- 일반적으로 `entryPolicy.kind = board`를 사용한다.
- 알파에서는 side mission의 mid-game 추가 생성/교체를 지원하지 않는다.

### 9.4 Board 공존 규칙
알파 권장 규칙:
- board는 `entryPolicy.kind = board`인 concrete mission의 현재 상태를 보여주는 인터페이스다.
- story mission과 side mission은 같은 board에 함께 노출될 수 있다.
- UI는 필요하면 story/side section을 나눌 수 있지만, 런타임 차원에서 별도 slot/pool 시스템을 요구하지 않는다.
- side mission의 선택 개수는 world initialization 시점에 이미 확정되며, board는 terminal mission을 다른 mission으로 교체하지 않는다.

이유:
- progression-critical story mission을 side mission selection 규칙과 분리해 관리하기 위함
- board를 mission 생성기가 아니라 mission 진입 인터페이스로 고정하기 위함

---

## 10) Side mission template selection

## 10.1 Alpha 결정: side mission selection은 world initialization 시 1회만 수행한다
알파에서는 side mission을 보드 open 시점이나 게임 중간에 새로 생성하지 않는다.

대신:
- world initialization 시 side mission template group에서 일부 template를 고른다.
- mission-linked server group도 이 시점에 함께 생성한다.
- 선택된 template만 concrete mission으로 materialize한다.
- 이후 board는 이미 존재하는 concrete mission의 상태만 표시한다.

비고:
- 이 selection/materialization pass 자체의 mission runtime semantics는 `05` owner다.
- 어떤 scenario/campaign가 어떤 mission file set을 이 pass에 공급하는지는 `10` owner다.
- 즉 generator pipeline의 **mission entrypoint wiring**은 `10`,
  mission file 내부 schema와 expansion 의미론은 `05`로 분리한다.

이 결정의 장점:
- mission-target binding이 게임 시작부터 안정적이다.
- save/load가 단순해진다.
- “accept 시점/board open 시점에 새 서버가 갑자기 생기는 문제”를 피할 수 있다.
- mission summary에서 논의된 “모든 미션의 연결 서버 인스턴스를 런타임으로 올린다”는 방향과 일치한다.

### 10.2 SideMissionTemplateGroup
권장 개념:

```text
SideMissionTemplateGroup
- groupId
- pickCount
- templateRefs / weights
```

world initialization 시:
1. group별로 `pickCount`만큼 template refs를 deterministic sampling
2. 선택된 template만 concrete side mission으로 확장
3. 필요한 server group을 생성하고 binding 수행
4. concrete mission은 초기 state=`hidden` 또는 entry policy reevaluation 결과 상태를 가진다
5. 선택되지 않은 template는 이번 플레이스루에서 사용되지 않는다

### 10.3 Concrete binding 결과
MissionTemplate expansion 결과는 최소한 아래를 고정한다.
- stable concrete `missionId`
- slot -> concrete nodeId mapping
- generated string binding values
- generated requester/title/briefing
- concrete reward value
- script config에 필요한 concrete ids

예:
- `$target_server -> node_side_corp_013`
- `{client} -> "Blue Finch Logistics"`
- `creditsRange [3000, 5000] -> 4210`

### 10.4 No runtime refill / no mid-game generation
- world initialization이 끝난 뒤에는 selected side mission 집합이 그 플레이스루 전체에서 고정된다.
- completed/failed mission은 소모되며, 다른 template mission으로 대체되지 않는다.
- board는 terminal mission을 대신할 새 mission을 생성하거나 reveal하지 않는다.
- 따라서 플레이어가 해당 플레이스루의 board-entry mission을 모두 소진하면 board가 비어 있을 수 있다.

---

## 11) MissionBoard

## 11.1 Board는 concrete mission의 projection이다
board는 별도 mission 생성기나 slot runtime이 아니라,
이미 존재하는 concrete mission 중 `entryPolicy.kind = board`인 mission을 보여주는 인터페이스다.

기본 규칙:
- board는 mission을 생성/교체/refill하지 않는다.
- board가 보여주는 대상은 현재 world에 이미 존재하는 concrete mission뿐이다.
- story mission과 side mission은 동일한 규칙으로 board entry candidate가 된다.

### 11.2 Board listing 규칙
알파 권장 규칙:
- `entryPolicy.kind = board`인 mission만 board listing 후보가 된다.
- `hidden` mission은 board에 보이지 않는다.
- `visible` mission은 board에 보이지만 accept 불가다.
- `available` mission은 board에 보이며 accept 가능하다.
- `active/completed/failed` mission을 board의 신규 계약 목록에 포함할지 여부는 UI 정책이지만,
  본 문서의 기본 listing semantics는 `visible/available` 중심으로 본다.

### 11.3 Board reevaluation 시점
MissionManager는 적어도 아래 시점에 board listing을 재계산한다.
- world initialization 직후
- board UI open 시
- `revealWhen` / `acceptWhen`에 영향을 주는 relevant global state 변화 후
- mission state가 `hidden/visible/available/active/completed/failed` 사이에서 바뀐 직후

알파 권장 구현:
- board listing 계산은 lazy하게 수행한다.
- board UI open 시점과 relevant state change 시점에만 재평가한다.
- 매 tick reevaluate는 금지한다.
- 재평가는 **기존 mission 상태를 읽어 표시를 갱신하는 것**이지, 새 mission을 생성하는 것이 아니다.

### 11.4 Empty board는 정상 상태다
- 현재 world에 `visible` 또는 `available`인 board-entry mission이 하나도 없으면 board는 비어 있을 수 있다.
- 알파 버전은 플레이어에게 무한한 side mission 공급을 보장하지 않는다.
- 플레이어가 현재 플레이스루의 mission을 모두 소진한 뒤 board가 비는 것은 허용되는 상태다.

### 11.5 Abandon 시 board behavior
board mission을 abandon하면:
- mission-local progress와 timer를 reset한다.
- state는 `available` 또는 `visible`로 되돌린다.
- board listing은 그 새로운 state를 반영해 다시 계산한다.
- abandon이 새 mission 생성이나 교체를 유발하지는 않는다.

---

## 12) Mission script capability profile

Mission/Scenario script는 unified hook system(`11`) 위에서 실행되지만,
capability는 알파에서 아래로 제한한다.

### 12.1 허용 namespace
- `flags.*`
- `mailbox.*`
- `world.*`
- `mission.*`
- `reward.*`

비고:
- `reward.*`는 board/UI에 미리 표시되지 않는 숨겨진 추가 보상용으로만 사용한다.
- 공개 기본 보상은 `rewards:` 블록의 owner이며, MissionManager가 완료 전이에서 자동 집행한다.

### 12.2 금지 namespace
- `server.*`
- `trace.*`
- `import`

### 12.3 import 정책
알파 버전에서는 mission/scenario execution profile에 전역 `import`를 바인딩하지 않는다.

즉:
- 플레이어 MiniScript에는 `import`가 존재할 수 있다.
- mission/scenario hook script에는 `import`가 존재하지 않는다.

이유:
- authoring profile을 단순하게 유지
- 콘텐츠 스크립트의 의존성 확산 억제
- deterministic packaging 단순화

### 12.4 이 문서가 `03`에 요구하는 mission/reward surface
이 문서는 `03`에 아래 함수를 요구한다.

`mission.*`
- `mission.complete()`
- `mission.fail(reason?)`
- `mission.setProgress(key, value)`
- `mission.getProgress(key)`
- `mission.hasProgress(key)`

`reward.*`
- `reward.grantCredits(amount)`

비고:
- `reward.*`의 exact API contract owner는 `03`이다.
- 이 문서(`05`)에서 `reward.*`는 `rewards:`를 대체하는 기본 보상 지급 수단이 아니라,
  숨겨진 추가 보상 지급 수단으로만 사용한다.

`flags.*`, `mailbox.*`, `world.*`는 `03`의 공통 도메인 모듈 규약을 따른다.

---

## 13) Mission script authoring 패턴

## 13.1 권장 패턴: progress-first, complete-later
복합 조건은 아래 패턴을 권장한다.

1. 개별 사건 hook에서 progress를 기록한다.
2. 마지막 확인 hook에서 progress를 읽고 complete한다.

예:
- `onAuthSuccess` → `root_ok=true`
- `onFileAcquire` → `artifact_ok=true`
- `onMailSent` → `root_ok && artifact_ok && correctAttachment`이면 complete

### 13.2 권장 패턴: immediate complete
실시간 감시형/단일 목표형은 즉시 complete 가능하다.

예:
- `onAuthSuccess`에서 target root 로그인 성공 즉시 complete

### 13.3 금지 패턴
- mission script가 `server.*`로 world를 직접 뒤흔들며 미션을 성립시키는 것
- mission script가 다른 미션을 직접 생성/시작시키는 것
- `import`로 외부 라이브러리를 가져오는 것

연계 미션은 `entryPolicy.kind=auto`와 `startWhen.missionCompleted`로 해결한다.

---

## 14) 연계 미션 체인

다단계 진행은 checkpoint 대신 **연계 미션 체인**으로 구현한다.

### 14.1 기본 규칙
- 한 단계 = 하나의 mission
- 다음 단계 = 새 mission
- 이전 단계 완료가 다음 단계의 `auto.startWhen` 조건

예:

```yaml
story_phase_02:
  entryPolicy:
    kind: auto
    startWhen:
      missionCompleted: story_phase_01
```

### 14.2 왜 checkpoint를 피하는가
- 한 미션 안에 단계/체크포인트/부분 보상을 다 넣으면 lifecycle이 복잡해진다.
- save/load, retry, script authoring 모두 어려워진다.
- 여러 개의 작은 mission으로 나누면 pacing과 보상이 더 명확해진다.

### 14.3 Alpha 비목표
알파에서는 범용 `mission.start(nextMissionId)` API를 요구하지 않는다.

필요한 연결은 대부분 `auto.startWhen.missionCompleted`로 표현한다.

---

## 15) Save/Load semantic minimum

Exact save file format owner는 `12`다.
본 섹션은 미션 시스템이 저장해야 하는 **의미론적 최소 단위**만 정의한다.

### 15.1 Concrete mission persistence minimum
각 concrete mission에 대해 최소한 아래 의미가 저장되어야 한다.

- `missionId`
- `sourceKind` (`story` / `side`)
- `state`
- `boundRefs`
  - concrete node ids / generated attachment ids / generated requester ids 등
- `mission-local progress`
- `startedAt` / `expiresAt` (timed mission인 경우)
- `failureReason` (failed mission인 경우)

### 15.2 Board persistence minimum
알파 board는 concrete mission의 projection이므로,
slot/pool/deck cursor 같은 별도 런타임 상태를 필수로 저장하지 않는다.

최소 의미론:
- save가 concrete mission runtime state를 저장하고 있다면,
  board listing은 load 후 그 mission state들에서 다시 계산할 수 있어야 한다.
- 즉 board는 독립 저장소가 아니라 mission runtime의 파생 뷰로 본다.

### 15.3 Load 후 reconciliation
권장 규칙:
- `active/completed/failed`는 저장값을 신뢰한다.
- `hidden/visible/available` board mission은 load 직후 MissionManager가 `revealWhen/acceptWhen`을 한 번 재평가해 reconcile할 수 있다.
- concrete mission binding과 selected side mission 집합은 저장값을 기준으로 유지한다.
- load 후 새 template selection이나 새 mission generation을 다시 수행하지 않는다.

---

## 16) Runtime integration points

### 16.1 `10`과의 연결
- side mission template selection/materialization은 scenario/world generator pass의 확장으로 본다.
- mission-linked server group 생성도 같은 phase에서 수행한다.
- 이 phase는 world initialization 1회만 수행되며, mid-game mission refill/generation은 포함하지 않는다.
- discovery model(`initiallyExposed` 재정의 / DNS / mission reveal 연계)은 `10`과 함께 정리해야 한다.

### 16.2 `11`과의 연결
- mission handlers는 unified hook system owner인 `11`에 의해 dispatch된다.
- `11`은 runtime event semantics를 소유하고,
- `05`는 “어떤 mission이 어떤 handler를 언제 활성화하는가”를 소유한다.
- 알파 mission integration을 위해 `11`에는 최소 `onMailSent` 추가가 필요하다.

### 16.3 `03`과의 연결
- mission/reward/profile capability matrix는 `03`가 owner다.
- 이 문서는 미션 시스템이 요구하는 최소 module surface만 명시한다.

### 16.4 `09`와의 연결
- concrete mission runtime storage field name / container shape / indexes는 `09`에서 확정한다.
- 이 문서는 `MissionManager`가 보유해야 하는 semantic state만 정의한다.

### 16.5 `12`와의 연결
- save slot serialization exact format은 `12`가 확정한다.
- 이 문서는 semantic minimum만 정의한다.

---

## 17) 추천 프로젝트 구조

```text
scenario_content/
  missions/
    templates/
      file_theft_basic.yaml
      root_breach_basic.yaml
    story/
      story_phase_01.yaml
      story_phase_02.yaml
    scripts/
      file_theft.ms
      root_breach.ms
      story_root_breach.ms

  server_specs/
    corp_ftp_small.yaml
    corp_ftp_medium.yaml
    corp_internal_admin.yaml

  campaigns/
    base/
      campaign.yaml
      mission_board.yaml
```

권장 분리:
- `templates/` = side mission template input
- `story/` = concrete authored missions
- `scripts/` = mission-owned hook scripts

---

## 18) Codex 구현 체크리스트

### 18.1 Mission data model
- [ ] `MissionState = hidden|visible|available|active|completed|failed` 구현
- [ ] `MissionEntryPolicy(kind=board|auto)` discriminated union 구현
- [ ] `MissionBlueprint` / `MissionTemplate` 로딩 구현
- [ ] concrete mission runtime model 정의
- [ ] mission-local progress dictionary 구현

### 18.2 Side mission selection at world initialization
- [ ] side mission template group 개념 구현
- [ ] world initialization 시 template selection/materialization 수행
- [ ] requirements / specPool 기반 concrete server group 생성
- [ ] binding/interpolation parser + validator 구현
- [ ] generatedBindings deterministic sampling 구현
- [ ] requirements AND grammar + selection-time evaluator 구현
- [ ] concrete binding(slot -> nodeId) 저장
- [ ] generated string binding 저장
- [ ] generated display/reward 값 고정
- [ ] mid-game mission refill/generation 비지원 고정

### 18.3 MissionManager
- [ ] board listing projection 계산
- [ ] revealWhen / acceptWhen / auto.startWhen 평가
- [ ] board open / relevant state change 시 listing 재평가
- [ ] runtime evaluator 진입 전 `$slot` -> concrete ref lowering 구현
- [ ] display interpolation eager render / unresolved placeholder 검증 구현
- [ ] rule -> watch key compile 구현
- [ ] watcher bucket(`reveal/accept/start/fail`) registry 구현
- [ ] affected watch key 기반 partial reevaluation 구현
- [ ] mission state 전이 시 watcher 재등록 구현
- [ ] accept 처리
- [ ] abandon 처리 (board mission only)
- [ ] complete / fail 처리
- [ ] `active -> completed` 전이에서 authored `rewards:` 1회 자동 적용
- [ ] timer 처리
- [ ] revealOnAccept / revealOnStart 처리

### 18.4 Hook integration
- [ ] active mission만 handler 등록
- [ ] mission terminal state에서 handler 해제
- [ ] mission script capability profile 적용 (`flags/mailbox/world/mission/reward` only)
- [ ] mission/scenario profile에서 `import` 비바인딩 처리
- [ ] `onMailSent` hook 추가 연동 (`11` owner patch 필요)

### 18.5 Persistence integration
- [ ] concrete mission runtime state 저장/복원
- [ ] selected side mission 집합과 concrete binding 저장/복원
- [ ] load 시 재샘플링 없이 hidden/visible/available reconcile pass 구현

---

## 19) Deferred / future extension

아래는 알파에서 미룬다.

- generic `mission.start(nextMissionId)` API
- `abandoned` canonical state
- script-based `startWhen`
- mission-local nested data schema
- mission UI 섹션/필터 상세 규칙
- turn-in attachment 검증을 위한 richer mail schema
- mission hint budget / hint unlock 규칙

---

## 20) 최종 구현 지침 (요약)

Codex는 아래 방향으로 구현하면 된다.

1. 미션은 서버와 분리된 concrete runtime 객체로 관리한다.
2. side mission은 world initialization 시 한 번만 선택 / concrete materialization 한다.
3. board/auto entry policy를 union으로 구현한다.
4. 미션 완료는 engine-level completion mode가 아니라 hook script가 직접 결정한다.
5. 복합 조건은 mission-local progress로 처리한다.
6. 연계 미션은 `auto.startWhen.missionCompleted`로 연결한다.
7. board는 이미 존재하는 concrete mission을 보여주는 projection이며, terminal mission을 새 mission으로 교체하지 않는다.
8. active mission만 hook handler를 활성화한다.
9. mission script는 `flags/mailbox/world/mission/reward`만 쓸 수 있고 import는 금지한다.
10. `onMailSent`를 포함해 mission-facing hook set이 `11`과 정합되도록 맞춘다.
