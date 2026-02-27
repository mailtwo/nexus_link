# Blueprint 데이터 스키마 v0 (ServerSpec / Scenario / Campaign)

목적: **프로토타입(쉬움/중간/어려움)** 월드를 데이터로 정의하고, Codex가 이 문서만 보고 **월드 생성(instantiate), IP 할당, 네트워크 인접 계산, 계정/권한/OTP 인증, 오버레이 병합, 이벤트 트리거**를 구현할 수 있도록 한다.

범위(v0)
- 엔진/플랫폼: Godot(로직 C#), PC
- 동적 생성/랜덤 생성: **알파 버전 범위에 포함**
  - 본 문서에서의 의미: 월드 생성 시점의 **서버 런타임 동적 생성**
    (서버 수/배치, 토폴로지, 이벤트, 데이터, 계정/자격정보 생성/변형 포함)
  - 예: 랜덤 서버 노드 추가, 링크/허브 재배치, 이벤트 초기 집합 재구성, 초기 파일/계정 데이터 주입
  - 랜덤 생성은 `worldSeed` 기반 결정적 생성만 허용한다(현재 시각/환경값 기반 비결정 입력 금지).
  - `AUTO` 계정/패스워드 생성은 동적 생성 범위에 포함되며 기존 규칙을 그대로 유지한다.
- 서브넷 가시화 규칙: v0에서는 **고정 규칙(하드코딩 가능)** 으로 둔다(아래 “런타임 규칙” 참고)
- Uplink 스타일 “공용 인터넷 + 사설 서브넷 + 게이트웨이(인터넷 노출 1대)” 구조를 지원한다.

---

## 0) 핵심 개념

- **ServerSpecBlueprint**: 재사용 가능한 “서버 레시피(프리팹)”  
  계정/포트/디스크/데몬(인증 모듈 설정 포함) 등의 기본값을 정의한다.

- **ServerSpawnBlueprint**: 시나리오에서 실제로 배치되는 서버 1대의 선언  
  `blueprintId`(스펙 참조) + 오버라이드(ports/daemons/diskOverlay 등) + 인터페이스(네트워크)로 구성된다.

- **ScenarioBlueprint**: 인게임 “시나리오” 단위 데이터  
  서버 목록 + (옵션) 서브넷 토폴로지 + 이벤트 정의를 포함한다.

- **CampaignBlueprint**: 시나리오를 묶는 상위 단위  
  알파 버전에서는 진행/해금/순서/스테이지 등 game flow 메타 확장을 포함한다.
  단, 스테이지의 상세 내용(플레이 흐름/연출/난이도 설계)은 `15_game_flow_design.md`를 따른다.
  See DOCS_INDEX.md → 15.
  본 문서(10)는 해당 game flow 메타를 YAML로 어떻게 작성하고, Blueprint 로더가 이를 어떻게 읽어 초기화 매핑하는지만 다룬다.

---

## 1) ServerSpecBlueprint (서버 스펙)

```text
ServerSpecBlueprint
- specId: string
- initialStatus: ENUM { online, offline }
- initialReason: ENUM { OK, reboot, disabled, crashed }
- hostname: string
- users: Dictionary<string /*userKey*/, UserBlueprint> # userKey는 게임 전체에 대해 유일
- ports: Dictionary<int /*portNum*/, PortConfig>
- daemons: Dictionary<daemonType, DaemonBlueprint>
- diskOverlay:
    - overlayEntries: Dictionary<string /*path*/, EntryMeta>
    - tombstones: Set<string /*path*/>
- logCapacity: int
```

### 1.1 UserBlueprint

```text
UserBlueprint
- userId: string                  # 로그인에 사용되는 실제 표시 ID
    - "AUTO:<policy>" 지원 (예: "AUTO:casual")
- passwd: Optional<string>         # authMode에 따라 None 가능
    - "AUTO:<policy>" 지원 (예: "AUTO:c5_base64")
- authMode: ENUM { none, static, otp, ... }
    - none: 비밀번호 검사 없음 (로그인 비밀번호 입력을 무시)
    - static: passwd 일치 검사
    - otp: OTP 검증(아래 OTP 데몬 설정과 결합)
- privilege:
    - read: bool
    - write: bool
    - execute: bool
```

**참조 규칙(중요)**
- 블루프린트에서 특정 계정을 가리킬 때는 `userId`가 아니라 **`userKey`** 를 사용한다.  
  (`userId`는 AUTO로 변할 수 있으나 `userKey`는 항상 고정이기 때문)
- 단, 플레이어 입력/시스템콜/공개 API 경계에서는 `userId`만 사용하고 `userKey`는 외부에 노출하지 않는다.

### 1.2 PortConfig

```text
PortConfig
- portType: ENUM { none, ssh, ftp, http, sql }
- serviceId?: string
- exposure: ENUM { public, lan, localhost }
- banner?: string
```

규칙(v0.2):
- 모든 서버는 월드 생성 시 기본 포트 시드를 가진다.
  - `22`: `{ portType: ssh, exposure: public }`
  - `21`: `{ portType: ftp, exposure: public }`
- 이후 `ServerSpec.ports`와 `ServerSpawnBlueprint.ports` overlay가 같은 `portNum`을 정의하면 해당 값으로 교체된다.
- `portType=none`이면 해당 포트는 비할당(unassigned) 상태로 간주한다.
  - 비할당 포트는 연결 가능한 서비스가 없는 것으로 판정한다.
  - 이 경우 `exposure` 값은 입력 가능하지만 런타임 판정에서 무시된다.
- `banner`는 `net.banner` 단서 문자열용 선택 필드다.
  - 생략 시 런타임에서 빈 문자열로 취급할 수 있다.

### 1.3 DaemonBlueprint: OTP (인증 모듈 설정)

v0에서는 “데몬”을 실제 백그라운드 프로세스라기보다, **서버의 인증 모듈 설정**으로 취급한다.

참고:
- daemonType/daemonArgs의 런타임 상세 계약(예: `connectionRateLimiter` 인자와 처리 규칙)은  
  `plans/09_server_node_runtime_schema_v0.md`의 **8) Daemons** 섹션을 참고하면 된다.

```text
DaemonBlueprint (OTP)
- userKey: string                 # OTP로 제어되는 계정 key
- stepMs: int                     # TOTP 스텝(ms)
- allowedDriftSteps: int          # 드리프트 허용 스텝 수 (예: 1이면 ±1 스텝 허용)
- otpPairId: string               # OTP 검증용 페어(시리얼/키) 식별자
```

> otpPairId는 플레이어에게 노출될 필요가 없다(엔진 내부용).  
> 실제 OTP 문자열 포맷은 엔진이 고정된 표준(예: 6자리 숫자 or 8자리 base32 등)로 정의해도 된다.
> v0 OTP 모델은 `stepMs/allowedDriftSteps` 기반 TOTP만 허용하며, 서버 발급형 TTL 토큰 모델은 사용하지 않는다.

---

## 2) ScenarioBlueprint (시나리오)

```text
ScenarioBlueprint
- scenarioId: string
- servers: List<ServerSpawnBlueprint>
- subnetTopology: Dictionary<string /*subnetId*/, SubnetBlueprint>
- Scripts?: Dictionary<string /*scriptId*/, string /*MiniScript body*/>
- events: Dictionary<string /*eventId*/, EventBlueprint>
```

### 2.1 ServerSpawnBlueprint (서버 1대 배치)

```text
ServerSpawnBlueprint
- nodeId: string                      # 게임 전체에 대해 유일
- specId: string                      # ServerSpecBlueprint.specId 참조
- hostname?: string                   # 있으면 spec.hostname override
- initialStatus?: ENUM                # 있으면 spec.initialStatus override
- initialReason?: ENUM                # 있으면 spec.initialReason override
- role: ENUM { terminal, otpGenerator, mainframe, tracer, gateway }
- info: List<string>

- diskOverlay?: (overlay)
    - overlayEntries: Dictionary<path, Optional<EntryMeta>>
        - path 키 단위 replace
        - value가 None이면: 시나리오에서 해당 path 제거(스펙에 있던 것도 삭제)
    - tombstones: Set<path> (union)

- ports?: Dictionary<portNum, Optional<PortConfig>> (overlay)
    - portNum 키가 존재하면: **통째로 교체(replace)**
    - value가 None이면: 해당 portNum 제거

- daemons?: Dictionary<daemonType, Optional<DaemonBlueprint>> (overlay)
    - daemonType 키가 존재하면: **통째로 교체(replace)**
    - value가 None이면: 해당 daemonType 제거
    - 특히 OTP가 양쪽에 정의되면: Scenario 쪽 OTP가 우선(스펙 OTP 무시)

- interfaces: List<InterfaceBlueprint>
```

#### 2.1.1 Overlay 병합 규칙(필수 구현)

월드 생성 시, 각 서버는 아래 순서로 병합한다.

1) **Base = ServerSpecBlueprint**
2) ServerSpawnBlueprint의 Optional 필드로 override (`hostname`, `initialStatus`, `initialReason`)
3) `ports`, `daemons`, `diskOverlay`를 overlay 규칙대로 반영

**키 단위 replace 원칙**
- `ports[portNum]`: 필드 merge 금지, 값 전체를 교체
- `daemons[daemonType]`: 필드 merge 금지, 값 전체를 교체
- `diskOverlay.overlayEntries[path]`: 값 전체를 교체 (EntryMeta 단위)
- `diskOverlay.tombstones`: union

**삭제 지원**
- `ports[portNum] = None` → 해당 포트 제거
- `daemons[type] = None` → 해당 데몬 제거
- `overlayEntries[path] = None` → 해당 path 제거
- 주의: `ports[portNum] = None`(YAML null)은 "키 삭제"이고, `ports[portNum].portType = none`은 "키 유지 + 비할당"이다.

### 2.2 InterfaceBlueprint (네트워크 인터페이스 + 노출)

```text
InterfaceBlueprint
- netId: string                     # 예: "internet", "easy_subnet", "medium_subnet", "hard_subnet"
- hostSuffix?: List<int>            # addressPlan 기준 host 부분 고정
- initiallyExposed: bool            # 이 netId가 Visible이 되는 순간 자동 노출(known)되는지
```

#### 2.2.1 hostSuffix 규칙(필수 구현)

- `hostSuffix`는 **해당 netId의 addressPlan(SubnetBlueprint.addressPlan)** 을 기준으로 “호스트 부분”만 지정한다.
- v0에서 addressPlan은 **옥텟 경계(/8, /16, /24)만 허용**한다. 따라서 hostSuffix 길이는 아래와 같다:

| addressPlan prefix | hostSuffix 길이 | 예시 |
|---|---:|---|
| /24 | 1 | 10.0.0.0/24 + [13] → 10.0.0.13 |
| /16 | 2 | 10.0.0.0/16 + [13,42] → 10.0.13.42 |
| /8  | 3 | 10.0.0.0/8  + [0,13,42] → 10.0.13.42 |

검증/에러 조건:
- hostSuffix 원소가 0..255 범위 밖이면 에러
- hostSuffix 길이가 prefix와 불일치하면 에러
- addressPlan 범위를 벗어나면 에러(실질적으로 길이/범위 체크로 충분)
- 동일 netId 내 IP 중복이면 에러
- 네트워크/브로드캐스트 주소 사용은 금지(권장):
  - /24에서 hostSuffix [0], [255] 금지
  - /16, /8도 동일 개념 적용(호스트 전체가 0 또는 all-ones인 주소 금지)

> `hostSuffix`가 None이면 IP 할당자는 addressPlan 풀에서 자동 할당한다(아래 “월드 생성 파이프라인” 참고).

### 2.3 Scripts (MiniScript Guard 소스)

- `Scripts`는 시나리오에서 재사용 가능한 guard 스크립트 저장소다.
- key는 `scriptId`, value는 MiniScript **함수 body만** 작성한다.
- 엔진은 로드 시 guard 본문을 자동 래핑해 사용한다:

```miniscript
func guard(evt, state)
  <body lines...>
end func
```

- `evt`: 이벤트 payload
- `state`: 읽기 전용 상태 뷰(확장 가능)

---

## 3) SubnetBlueprint (서브넷 토폴로지)

```text
SubnetBlueprint
- addressPlan: string               # 예: "10.0.0.0/24" (/8,/16,/24만)
- hubs: Dictionary<string /*hubId*/, HubBlueprint>
- links: List<{ a: nodeId, b: nodeId }>
```

### 3.1 HubBlueprint

```text
HubBlueprint
- type: ENUM { switch }
- members: List<string /*nodeId*/>
```

### 3.2 토폴로지 해석(권장 구현)

- subnet 소속의 기준은 **서버의 interfaces[].netId == subnetId** 이다.
- `hubs[*].members`와 `links`는 “인접(스캔/이동 가능)” 관계를 생성한다.

권장 인접 계산:
1) 각 hub(type=switch)는 `members`를 **완전 연결(complete graph)** 로 취급  
   - members에 포함된 모든 서버 쌍을 서로 인접 처리
2) `links`는 명시된 서버 쌍을 인접 처리
3) 최종 인접 = (모든 hub 인접) ∪ (모든 link 인접)
4) 어떤 서버가 subnetId 인터페이스를 갖지만 topology에 한번도 나오지 않아도 허용한다.  
   - 이 경우 해당 subnet에서 이웃이 0개가 될 수 있다(게임 디자인상 허용).  
   - 다만 콘텐츠 오류 가능성이 있으므로 경고 후보(아래 “경고/검증” 참고).

---

## 4) EventBlueprint (이벤트)

```text
EventBlueprint
- conditionType: ENUM { privilegeAcquire, fileAcquire }
- conditionArgs: Dictionary<string, Any>
- guardContent?: string
- actions: List<ActionBlueprint>
```

### 4.1 conditionType = privilegeAcquire

```text
conditionArgs (privilegeAcquire)
- nodeId?: string            # None이면 어떤 서버든 통과
- userKey?: string           # None이면 어떤 유저든 통과
- privilege: string          # 예: "execute" | "read" | "write"
```

### 4.2 conditionType = fileAcquire

```text
conditionArgs (fileAcquire)
- nodeId?: string
- fileName: string           # 로컬로 전송된 파일명(절대경로/파일명 규칙은 v0에서 단순화 가능)
```

### 4.3 ActionBlueprint

```text
ActionBlueprint
- actionType: ENUM { print, setFlag }
- actionArgs: Dictionary<string, Any>
```

- print:
  - text: string
- setFlag:
  - key: string
  - value: Any

> v0에서는 “클리어 처리”를 `print`만으로 표현해도 된다(예: “쉬움 시나리오 완료!”).  
> 다만 이후 확장을 위해 `setFlag`를 통해 캠페인/보상/진행 상태를 저장할 수 있게 한다.

### 4.4 guardContent 작성/참조 규약

`guardContent`는 아래 3가지 prefix만 허용한다.

1) Inline 스크립트: multi-line 문자열의 첫 줄이 `script-`  
2) Scripts 참조: 단일 라인 `id-<scriptId>`  
3) 외부 파일: 단일 라인 `path-<relativePath>`

추가 규칙:
- `path-`는 YAML 파일 위치가 아니라 **프로젝트 루트 기준 상대경로**로 해석한다.
- prefix가 다르면 로드 에러로 처리한다.

예시:

```yaml
Scripts:
  hardWinGuard: |-
    return evt.privilege == "execute"

events:
  hardScenarioWinEvent:
    guardContent: "id-hardWinGuard"
```

---

## 5) CampaignBlueprint (캠페인)

알파 최소 정의(확장 여지 유지):

```text
CampaignBlueprint
- campaignId: string
- childCampaigns: List<string /*campaignId*/>
- scenarios: List<string /*scenarioId*/>
- flowMeta?: Dictionary<string, object>   # 알파 game flow 메타 확장 슬롯(미결정 키 허용)
```

비고:
- 알파에서는 캠페인 순서/해금/진행/스테이지 참조 메타를 `flowMeta`에 담을 수 있다.
- 스테이지 상세 내용(플레이 흐름/연출/난이도 설계)은 `15_game_flow_design.md`를 따른다.
  See DOCS_INDEX.md → 15.
- 본 문서(10)의 범위:
  - YAML 작성 위치(`CampaignBlueprint.flowMeta`)와 로더 파싱/초기화 매핑만 정의
  - `flowMeta` 세부 키 의미/검증 규칙은 고정하지 않음(미결정)
- 추후 확장 후보(미결정, optional):
  - `startScenarioId`: 캠페인 시작 시나리오
  - `scenarioOrder`: 선형 진행 순서
  - `unlockRules`: 시나리오/캠페인 해금 조건
  - `stageRefs`: 스테이지 참조 키(상세는 15 문서)
  - `rewards`: 해금 보상/지급 메타
  - `persistentFlags`: 캠페인 진행 플래그 초기값/보존 대상

---

## 6) 런타임 규칙(v0) — 노출/서브넷 가시화(하드코딩 가능)

### 6.1 상태 모델(권장)
- VisibleNets: Set<netId>
- KnownNodes: Dictionary<netId, Set<nodeId>>
- (서버 런타임 캐시) isExposedByNet: Dictionary<netId, bool>
  - 의미: 해당 netId에서 이 서버가 현재 known 상태인지

### 6.2 초기 상태(권장)
- VisibleNets = { "internet" }
- KnownNodes["internet"] 초기화:
  - servers 중 `interfaces`에 netId="internet"이 있고, 해당 인터페이스 `initiallyExposed=true`인 서버의 nodeId를 추가

### 6.3 서브넷 가시화(unlock) 규칙(v0 고정)
- 모든 subnet(netId != "internet")은 시작 시 보이지 않는다.
- 플레이어가 어떤 서버에서 **execute 권한을 획득**하면,
  - 그 서버가 가진 모든 `interfaces[].netId` 중 subnetId를 VisibleNets에 추가한다.
- 어떤 netId가 VisibleNets에 “처음” 추가되는 순간:
  - 해당 netId 인터페이스가 `initiallyExposed=true`인 서버들을 KnownNodes[netId]에 추가한다.
- 이 시점에 각 서버의 `isExposedByNet[netId]`도 함께 갱신한다.

> 이 규칙은 v0에서 고정이며 blueprint로 분리하지 않는다.  
> (향후 다른 unlock 규칙이 필요해지면 SubnetBlueprint에 visibility 섹션을 추가할 수 있다.)

---

## 7) 월드 생성 파이프라인(코드 구현 지침)

1) **Spec 로딩**
- 모든 ServerSpecBlueprint를 specId로 인덱싱

2) **Scenario 로딩**
- ScenarioBlueprint 로딩
- nodeId 유일성 검증

2.5) **동적 생성 Pass (알파)**
- ScenarioBlueprint를 베이스로 Generator Pass를 실행해 런타임 초기 배치를 확정한다.
- 이 Pass에서 다음 항목의 동적 생성/변형을 허용한다:
  - 서버 수/배치(랜덤 서버 노드 추가/제거 포함)
  - subnet topology(hubs/links) 구성
  - 이벤트 초기 집합/트리거 파라미터
  - 초기 데이터(diskOverlay 엔트리/톰브스톤 등)
  - 계정/자격정보(users의 userKey/userId/passwd/authMode/privilege)
- 동적 생성 결과를 3) 이후 파이프라인의 입력으로 사용한다.

3) **서버 인스턴스 생성**
- 각 ServerSpawnBlueprint에 대해:
  - specId로 Spec 조회 → 베이스 복제
  - Spawn override 적용(overlay 규칙)
  - users 처리:
    - userId가 "AUTO:*"면 정책에 따라 실제 userId 생성
    - passwd가 "AUTO:*"면 정책에 따라 실제 passwd 생성
  - OTP 규칙 검증:
    - user.authMode=otp 인 계정이 있으면 OTP daemon 설정이 반드시 존재해야 함(없으면 에러)
    - OTP daemon의 userKey가 users에 존재해야 함(없으면 에러)

4) **네트워크/서브넷 구성**
- netId 수집:
  - "internet"은 예약 netId
  - 그 외 netId는 subnetTopology에 반드시 존재해야 함(없으면 에러)
- IP 할당:
  - 각 subnetId에 대해 addressPlan 파싱 (/8,/16,/24만)
  - hostSuffix가 있으면 고정 IP 계산
  - hostSuffix가 없으면 addressPlan 풀에서 자동 할당(중복/예약 제외)
- 인접 계산:
  - 각 subnetId의 hubs/links로 adjacency 계산
  - 결과는 “서버별, netId별 이웃 목록”을 만들기 권장
  - 런타임 캐시는 `lanNeighbors: List<nodeId>`로 유지
  - `net.scan` API/시스템콜 반환 형식은 `03_game_api_modules.md` / `07_ui_terminal_prototype_godot.md`를 따른다.
    See DOCS_INDEX.md → 03, 07.
  - 서버별 `subnetMembership` / `isExposedByNet` 캐시를 생성

5) **접근 불가(unreachable) 경고(권장)**
- v0 단순 경고 규칙:
  - 어떤 서버가 internet 인터페이스가 없고,
  - 속한 모든 subnetId에 대해 “인터넷에서 진입 가능한 노드(= internet+subnet을 모두 가진 서버)”가 하나도 없으면
    - 해당 서버(또는 해당 subnet 전체)를 **경고**로 보고한다.
- 추가 경고 후보:
  - subnet 인터페이스가 있으나 topology(hub/link)에 한번도 등장하지 않는 서버(고립 가능성)

---

## 8) 런타임 스키마 정합성 참조

런타임 저장 구조의 단일 기준은 `09_server_node_runtime_schema_v0.md`다.  
See DOCS_INDEX.md → 09.

본 문서는 blueprint → runtime **초기화 매핑 규칙**만 유지한다.
- 서버 런타임 컨테이너 키/필드(`serverList`, `ipIndex`, `users`, `interfaces`, `lanNeighbors` 등)의 상세 스키마는 09를 따른다.
- `initiallyExposed`는 초기 노출 계산 입력으로만 사용하고, 런타임 저장 필드 계약은 09를 따른다.
- OTP 참조 키(`userKey`)의 런타임 저장/검증 규칙은 09를 따른다.

---

## 9) 프로토타입 난이도 적용 체크(요약)

- 쉬움: internet에 노출된 easy gateway 1대, easy_subnet 내부에 target 1대(또는 gateway=target)
  - passwd: AUTO:dictionary(= leaked_password.txt 풀에서 선택) 권장

- 중간: medium gateway + medium_subnet에 main(target) + otpGenerator 서버
  - target root: authMode=otp + OTP daemon 설정(otpPairId)
  - otpGenerator 서버는 otpPairId에 해당하는 OTP 출력 프로그램을 제공(프로그램 모델은 별도 문서로 정의 가능)
  - otpGenerator 서버는 초중반엔 `initiallyExposed=true`, 후반엔 false로 난이도 조절

- 어려움: hard gateway + hard_subnet에 N대 + 복잡한 topology(hubs/links)

---

## 10) TODO (명시적으로 남겨둘 것)
- OTP 출력 프로그램(otpGenerator) 스펙/커맨드/API (프로그램 실행과 stdout 모델)
- “leaked_password.txt”의 제공 방식(워크스테이션 기본 탑재 vs 마켓 구매)
- 캠페인 진행/보상/클리어 처리의 데이터화(현재는 print로도 가능)
