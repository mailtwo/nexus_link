# 서버 노드 런타임 데이터 스키마 v0.2 (Godot/PC, C#)

목적: 게임 실행 중(World runtime) 각 서버 노드가 유지해야 하는 **저장 상태 필드/구조**를 `10` 문서와 정합되게 정의한다.  
Codex는 이 문서를 기준으로 런타임 데이터 컨테이너(월드 `serverList`, `ipIndex`, `processList`)의 **스키마**를 구현할 수 있어야 한다.

범위(v0.2):
- 플랫폼: PC
- 엔진: Godot(로직 C#)
- 실제 OS/네트워크 접근 없음(전부 가상)
- 저장/로드 범위/포맷/재구축 경계는 `12_save_load_persistence_spec_v0_1.md`를 따른다(See DOCS_INDEX.md → 12).
- 이번 문서는 런타임 메모리 **스키마 구조**만 정의한다.
- `lanNeighbors`는 유지하되 내부 참조 키는 `nodeId`를 사용

---

## 0) 전역 런타임 컨테이너(World Runtime)

월드는 최소 3개의 전역 컨테이너와 `worldSeed`를 유지한다.

### 0.1 `serverList`
- 타입: `Dictionary<string /*nodeId*/, ServerStruct>`
- 키: `nodeId` (게임 전체 유일)

### 0.2 `ipIndex`
- 타입: `Dictionary<IP, string /*nodeId*/>`
- 키: `ip` (게임 전체 유일)
- 의미: IP/host 입력 기반 연결(`ssh.connect(hostOrIp, ...)`)을 `nodeId`로 역참조

### 0.3 `processList`
- 타입: `Dictionary<int /*processId*/, ProcessStruct>`
- 키: `processId` (게임 전체 유일)

> 서버는 자신이 보유한 프로세스 id만 `server.process` set으로 참조한다.  
> 실제 프로세스 데이터는 항상 `processList`에서 조회한다(단일 진실).

### 0.4 `worldSeed`
- 타입: `int`
- 의미: 월드 초기화 시 생성되는 AUTO 값의 단일 결정 seed
- 규칙:
  - 초기화 전에 반드시 외부에서 주입되어야 하며, `0`은 허용하지 않는다.
  - 초기 생성값(`AUTO:userId`, `AUTO:passwd`)은 `worldSeed + 고정 입력값`만으로 seed를 구성한다.
  - seed 구성에는 현재 시각, 런타임 난수, 환경 상태값 같은 비결정 입력을 사용하지 않는다.
  - 동일 `worldSeed`와 동일 입력이면 동일 월드 생성 결과를 보장해야 한다.

### 0.5 키 전환 흡수 규칙(필수)
- `serverList` 키를 `ip`에서 `nodeId`로 바꾼 대신, 서버의 IP 정보는 `ServerStruct.primaryIp/interfaces`에 보존한다.
- `users` 키를 `userId`에서 `userKey`로 바꾼 대신, 표시용 계정 식별자는 `UserConfig.userId`에 보존한다.

### 0.6 참조 규칙(필수)
- 내부 참조/검증 키: `nodeId`, `userKey`
- 표시/로그 텍스트: `userId`, `IP 문자열`

### 0.7 공개 API 경계 규칙(필수)
- 플레이어 입력, 터미널 시스템콜 요청, Godot 공개 메서드 요청/응답에서는 **`userId`만 사용**한다.
- `userKey`는 내부 런타임(세션/이벤트/권한 검사/데이터 참조) 전용으로 유지하며 외부에 노출하지 않는다.
- 공개 응답 컨텍스트 전환 필드는 `nextUserId`를 사용하고 `nextUserKey`는 사용하지 않는다.
- `connect` 명령의 `<user>` 인자는 `userId` 기준으로 계정을 식별한다.
- 서버 단위로 `userId`는 유일해야 하며, 중복이면 월드 로딩을 실패시켜야 한다.
- MiniScript `ssh.connect`/`ssh.disconnect(session)`도 동일한 `userId` 외부 규칙을 따른다.
- `ssh.connect`의 SessionHandler DTO에는 `userKey`를 포함하지 않는다.

---

## 1) 공통 Struct: EntryMeta (VFS 오버레이용)

- `EntryMeta`의 정의/해석은 `08_vfs_overlay_design_v0.md`를 따른다.  
  See DOCS_INDEX.md → 08.

---

## 2) Process Struct (월드 전역)

### 2.1 `processList`
- 타입: `Dictionary<int, ProcessStruct>`
- 키: `processId` (유일)

### 2.2 ProcessStruct
```text
ProcessStruct
- name: string                    # 프로세스 이름(디버그/표시)
- hostNodeId: string              # 실행된 서버 nodeId
- userKey: string                 # 실행시킨 유저 key (자동 실행이면 "system")
- state: ENUM { running, finished, canceled }
- path: string                    # 프로세스/프로그램 경로(예: /usr/local/bin/passwdGen)
- processType: ENUM               # 완료 시 수행할 행동 타입(예: booting, ftpSend, fileWrite...)
- processArgs: Dictionary<string, Any>
- endAt: int                      # 완료 시각(worldTimeMs 기준, ms)
```

### 2.3 프로세스 처리 로직 참조
- `endAt` 판정, 스케줄링, 완료 효과, reboot 처리 순서는 `11_event_handler_spec_v0_1.md`를 따른다.  
  See DOCS_INDEX.md → 11.

---

## 3) Server Struct (월드 전역)

### 3.1 `serverList`
- 타입: `Dictionary<string /*nodeId*/, ServerStruct>`
- 키: `nodeId` (게임 전체에 대해 유일)

### 3.2 ServerStruct 필드
```text
ServerStruct
- name: string
- role: ENUM { terminal, otpGenerator, mainframe, tracer, gateway }
- status: ENUM { online, offline }
- reason: ENUM { OK, reboot, disabled, crashed }
    - invariant: status=online일 때만 reason=OK
    - status=offline일 때 reason ∈ {reboot, disabled, crashed}

- primaryIp: Optional<IP>
    - 규칙: interfaces 중 netId="internet"이 있으면 그 ip
    - 없으면 `None`

- interfaces: List<InterfaceRuntime>
    InterfaceRuntime
    - netId: string
    - ip: IP

- subnetMembership: HashSet<string /*netId*/>
- isExposedByNet: Dictionary<string /*netId*/, bool>
    - 의미: 해당 netId에서 이 서버가 현재 플레이어에게 노출(known) 상태인지
    - 초기 노출 시드(`interfaces[].initiallyExposed`)는 시나리오 로딩 시 계산에만 사용하고,
      런타임 `interfaces`에는 저장하지 않는다.
- lanNeighbors: List<string /*nodeId*/>
    - 의미: 현재 서버 기준 직접 이동 가능한 이웃 서버의 내부 참조 목록
```

---

## 4) Users / Sessions

### 4.1 users
- 타입: `Dictionary<string /*userKey*/, UserConfig>`

```text
UserConfig
- userId: string                     # 표시/로그인 텍스트용 id (AUTO 결과 포함)
- userPasswd: Optional<string>       # static이면 실제 값, none이면 생략 가능
- authMode: ENUM { none, static, otp, 기타 }
    - none: password를 검사하지 않음
    - static: userPasswd와 일치 검사
    - otp: OTP 검증(아래 OTP 데몬 설정과 결합)
- privilege: PrivilegeConfig
    - read: bool
    - write: bool
    - execute: bool
- info: List<string>
```

### 4.2 sessions
- 타입: `Dictionary<int /*sessionId*/, SessionConfig>`
- 키: `sessionId` (서버 단위 유일)

```text
SessionConfig
- userKey: string
- remoteIp: IP
- cwd: string
```

권장 규칙(v0.2):
- 내부 권한/비즈니스 로직은 `userKey` 기준으로 처리한다.
- 화면 표시/로그 출력은 `users[userKey].userId`를 사용한다.
- 서버 reboot 시작 시 `sessions`를 모두 종료(비움)

---

## 5) Network: ServerStruct 네트워크 필드 상세(subnetMembership / isExposedByNet / lanNeighbors / ports)

> 이 섹션의 항목은 전역 필드가 아니라, `3.2 ServerStruct`에 포함되는 네트워크 필드의 상세 규칙이다.

### 5.1 subnetMembership
- 타입: `HashSet<string /*netId*/>`
- 의미: 이 서버가 소속된 netId 집합(interfaces에서 파생된 캐시)

### 5.2 isExposedByNet
- 타입: `Dictionary<string /*netId*/, bool>`
- 의미: netId별 현재 노출 상태 캐시

### 5.3 lanNeighbors
- 타입: `List<string /*nodeId*/>`
- 의미: 현재 서버 기준 직접 이동 가능한 이웃 서버의 내부 참조 목록
- 비고: API/시스템콜 노출 형식(예: `net.scan` 반환 형식)은 `03_game_api_modules.md` / `07_ui_terminal_prototype_godot.md`를 따른다.  
  See DOCS_INDEX.md → 03, 07.

### 5.4 ports
- 타입: `Dictionary<int /*portNum*/, PortConfig>`
- 키: `portNum`

```text
PortConfig
- portType: ENUM { NONE, SSH, FTP, HTTP, SQL }
- serviceId?: string
- exposure: ENUM { public, lan, localhost }
- banner?: string
```

포트 시드/판정/API 의미/시스템콜 출력 규칙은 본 문서 범위 밖이며 다음 SSOT를 따른다.
- intrinsic/API 의미: `03_game_api_modules.md` (See DOCS_INDEX.md → 03)
- 시스템콜 출력/UX: `07_ui_terminal_prototype_godot.md` (See DOCS_INDEX.md → 07)
- 블루프린트 초기 시드/overlay 적용: `10_blueprint_schema_v0.md` (See DOCS_INDEX.md → 10)
- 이벤트/프로세스 처리 로직: `11_event_handler_spec_v0_1.md` (See DOCS_INDEX.md → 11)

---

## 6) Disk: diskOverlay (서버별 VFS 델타)

> 공통 기본 파일시스템(BaseFS)은 전역에 있고, 서버는 **오버레이만** 가진다.  
> Base 파일도 서버별로 삭제 가능해야 하므로 `tombstones`를 사용한다.

```text
diskOverlay
- overlayEntries: Dictionary<string /*path*/, EntryMeta>
- tombstones: HashSet<string /*path*/>
- dirDelta: Dictionary<string /*dirPath*/, DirDelta>
    DirDelta
    - added: HashSet<string /*childName*/>
    - removed: HashSet<string /*childName*/>
```

운영 의미(Resolve 우선순위/dirDelta 유지 규칙)는 `08_vfs_overlay_design_v0.md`를 따른다.  
See DOCS_INDEX.md → 08.

---

## 7) Server ↔ Process 연결

각 서버는 현재 실행 중인 프로세스 id만 별도로 가진다.

```text
process: HashSet<int /*processId*/>
```

규칙:
- 서버는 `process` set으로 자신의 프로세스를 추적한다.
- 실제 프로세스 내용은 `processList[processId]`에서 조회한다.

---

## 8) Daemons (방어/행동 힌트용)

- 타입: `Dictionary<daemonType, DaemonStruct>`
- 키: daemonType

```text
DaemonStruct
- daemonType: ENUM { OTP, firewall, connectionRateLimiter }
- daemonArgs: Dictionary<string, Any>
    OTP:
      - userKey: string
      - stepMs: int
      - allowedDriftSteps: int
      - otpPairId: string
    firewall: { }
    connectionRateLimiter:
      - monitorMs: int      # IP별 접속 시도 집계 윈도우(ms)
      - threshold: int      # monitorMs 윈도우 내 허용 시도 횟수(초과 시 차단)
      - blockMs: int        # threshold 초과 IP 차단 시간(ms)
      - rateLimit: int      # 1초당 처리 가능한 검사 수(비차단 IP 시도만 대상)
      - recoveryMs: int     # rateLimit 초과 과부하 발생 시 비활성 시간(ms)
```

OTP/레이트리미터의 동작 규칙(검증/판정/처리 순서)은 본 문서 범위 밖이다.
- intrinsic/API 관점 의미: `03_game_api_modules.md` (See DOCS_INDEX.md → 03)
- 런타임 처리 순서/스케줄링: `11_event_handler_spec_v0_1.md` (See DOCS_INDEX.md → 11)

---

## 9) Logs (게임플레이용)

- 용도: 플레이어가 로그를 읽고/지우고/변조하는 플레이를 지원
- 로그는 링버퍼로 유지(`logCapacity`)

```text
LogStruct
- id: int                          # 서버 로컬 단조 증가 로그 id
- time: int                        # worldTimeMs 기준 기록 시각(ms)
- user: string                     # 표시용 userId 텍스트
- sourceNodeId: string             # 로그를 남긴 접속 주체 서버 nodeId (A->B에서 A), 필수
- remoteIp: IP                     # 현재 서버(B)가 관측한 source endpoint IP (A->B에서 A의 접속 IP, 표시용)
- actionType: ENUM { login, logout, read, write, execute }   # 로그 카테고리
- action: string                   # 사람이 읽는 상세 이벤트 메시지
- dirty: bool                      # 로그가 생성 후 변조되었는지 여부
- origin: Optional<LogStruct>      # 최초 변조 전 원본 스냅샷
```

권장 규칙(v0):
- `sourceNodeId`는 항상 채운다(빈 문자열 금지).
- `sourceNodeId`는 **런타임 내부 추적/역산용 필드**이며 UI/플레이어 출력에 직접 노출하지 않는다.
- Trace/Forensic 경로 계산, edge 겹침, 가속, 단절 판정은 `sourceNodeId`(+ 로그 소유 서버 nodeId) 기준으로 수행한다.
- `remoteIp`는 UI/플레이어 출력용 관측값이며, 추적 판정 키로 사용하지 않는다.
- 로컬 액션(자기 서버 내부 발생)은 `sourceNodeId = 현재 서버 nodeId`, `remoteIp = 127.0.0.1`로 기록한다.
- `dirty=false`면 `origin=null`
- `dirty=true`가 되는 순간에만 원본을 1회 저장

---

## 10) 구현 체크리스트(v0.2)

- [ ] `serverList(nodeId key)`, `ipIndex`, `processList` 전역 컨테이너 구현
- [ ] 월드 초기화 전에 `worldSeed` 필수 검증(`0` 금지) 구현
- [ ] `AUTO userId/passwd` 결정 seed에 `worldSeed` 포함
- [ ] 초기화 seed에 현재 시각/난수/환경 상태값 같은 비결정 입력 사용 금지
- [ ] 서버 생성 시 `primaryIp/interfaces/subnetMembership/isExposedByNet` 초기화
- [ ] `interfaces[].initiallyExposed` 입력으로 초기 `KnownNodes/isExposedByNet`을 계산한 뒤 런타임에는 저장하지 않음
- [ ] `users`를 `userKey` 키로 관리하고 `UserConfig.userId`를 필수값으로 저장
- [ ] 세션/프로세스 내부 참조는 `userKey`, 표시/로그는 `userId` 사용
- [ ] `lanNeighbors`를 nodeId 기반 캐시로 유지
- [ ] `ports` 스키마(`portType/serviceId/exposure/banner`) 필드 유지
- [ ] `daemons` 스키마(`OTP/firewall/connectionRateLimiter`) 필드 유지
- [ ] VFS overlay 스키마: `overlayEntries` / `tombstones` / `dirDelta` 필드 유지
- [ ] Logs: ringbuffer + `sourceNodeId`(필수/비노출, 추적 판정 키) + `remoteIp`(표시 전용) + origin 유지 규칙
