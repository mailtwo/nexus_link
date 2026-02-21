# 서버 노드 런타임 데이터 스키마 v0.2 (Godot/PC, C#)

목적: 게임 실행 중(World runtime) 각 서버 노드가 유지해야 하는 상태를 **Blueprint v0(10 문서)와 정합되게** 정의한다.  
Codex는 이 문서만 보고 런타임 모델(월드 `serverList`, `ipIndex`, `processList`)과 서버 동작(로그인/프로세스/로그/디스크 오버레이)을 구현할 수 있어야 한다.

범위(v0.2):
- 플랫폼: PC
- 엔진: Godot(로직 C#)
- 실제 OS/네트워크 접근 없음(전부 가상)
- 저장/로드 포맷은 **나중에** 결정(이번 문서는 런타임 메모리 구조 중심)
- `lanNeighbors`는 유지하되 내부 참조 키는 `nodeId`를 사용

---

## 0) 전역 런타임 컨테이너(World Runtime)

월드는 최소 3개의 전역 컨테이너를 유지한다.

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

### 0.4 키 전환 흡수 규칙(필수)
- `serverList` 키를 `ip`에서 `nodeId`로 바꾼 대신, 서버의 IP 정보는 `ServerStruct.primaryIp/interfaces`에 보존한다.
- `users` 키를 `userId`에서 `userKey`로 바꾼 대신, 표시용 계정 식별자는 `UserConfig.userId`에 보존한다.

### 0.5 참조 규칙(필수)
- 내부 참조/검증 키: `nodeId`, `userKey`
- 표시/로그 텍스트: `userId`, `IP 문자열`

---

## 1) 공통 Struct: EntryMeta (VFS 오버레이용)

```text
EntryMeta
- type: ENUM { File, Dir }
- contentId: Optional<string>    # BlobStore id (File일 때)
# (추가 가능) size/perms/owner/mtime...
```

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
- endAt: int                      # 완료 시각(Unix time, ms 권장)
```

### 2.3 프로세스 처리 규칙(v0.2)
- 월드는 주기적으로 `processList`를 순회하며, `now >= endAt`인 `running` 프로세스를 완료 처리한다.
- 완료 처리:
  - 기본은 `state = finished`
  - `processType`에 따라 완료 효과 실행(예: booting이면 서버 online 전환)
- 서버 상태 보호 규칙:
  - 완료 효과 적용 전에 `serverList[hostNodeId]`의 `status/reason`을 확인한다.
  - 서버 `reason`이 `disabled` 또는 `crashed`면 완료 효과를 적용하지 않는다(프로세스는 finished 처리만 수행).

### 2.4 reboot(booting) 프로세스 규칙(v0.2)
- reboot 명령 실행 시:
  1) 서버를 `status=offline`, `reason=reboot`로 전환
  2) 해당 서버가 가진 `server.process`에 포함된 모든 프로세스를 canceled 처리하고 set을 비움
  3) `booting` 타입 프로세스 1개를 생성하여 `processList`/`server.process`에 등록
- booting 완료 시:
  - 서버가 `status=offline`이고 `reason=reboot`일 때만 `status=online`, `reason=OK`로 전환
  - 그 외 상태는 효과 없음

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
- 비고: 내부는 nodeId 캐시를 유지하지만, `net.scan("lan")`의 플레이어 노출 결과는 IP 목록으로 반환한다.

### 5.4 ports
- 타입: `Dictionary<int /*portNum*/, PortConfig>`
- 키: `portNum`

```text
PortConfig
- portType: ENUM { SSH, FTP, HTTP, SQL }
- serviceId?: string
- exposure: ENUM { public, lan, localhost }
```

`exposure` 판정 규칙(v0.2):
- 용어: `source`는 접속을 시도하는 서버, `target`은 해당 포트를 가진 서버
- `public`: 모든 source에서 접근 허용
- `lan`: `source.subnetMembership`과 `target.subnetMembership`이 1개 이상 겹칠 때만 허용
- `localhost`: `source.nodeId == target.nodeId`일 때만 허용

`net.scan("lan")` 권장 동작(v0.2):
1) 현재 접속 노드의 `lanNeighbors(nodeId)`를 읽는다.
2) 각 nodeId를 현재 컨텍스트(netId) 기준 IP로 변환한다.
3) 최종 결과를 IP 문자열 리스트로 반환한다(기존 UX 유지).

---

## 6) Disk: diskOverlay (서버별 VFS 델타)

> 공통 기본 파일시스템(BaseFS)은 전역에 있고, 서버는 **오버레이만** 가진다.  
> Base 파일도 서버별로 삭제 가능해야 하므로 `tombstones`를 사용한다.

```text
diskOverlay
- overlayEntries: Dictionary<string /*path*/, EntryMeta>
- tombstones: HashSet<string /*path*/>
- overlayDir: Dictionary<string /*dirPath*/, DirDelta>
    DirDelta
    - added: HashSet<string /*childName*/>
    - removed: HashSet<string /*childName*/>
```

운영 규칙(v0):
- Resolve 우선순위: tombstone > overlayEntries > baseEntries
- overlayDir는 현재 델타만 유지(added/removed가 모두 비면 제거)

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

OTP 규칙(v0.2):
- OTP 제어 계정 참조는 반드시 `userKey`를 사용한다(`userId` 참조 금지).

connectionRateLimiter 규칙(v0.2):
- 목적: 특정 IP의 과도한 접속 시도를 탐지/차단하되, 데몬 자체 처리량 한계를 모델링한다.
- 접속 시도 처리 단위 시간은 ms 기준으로 계산한다.
- 런타임 권장 상태:
  - `blockedUntilByIp: Dictionary<IP, int /*unixMs*/>`
  - `recentAttemptsByIp: Dictionary<IP, Queue<int /*unixMs*/>>` (monitorMs 윈도우 관리)
  - `rateWindowStartMs: int`, `rateCheckedInWindow: int`
  - `overloadedUntilMs: int`

처리 순서(접속 시도 1건 기준):
1) `now < overloadedUntilMs`면 데몬 비활성 상태로 간주한다.  
   - 이 구간에서는 데몬이 어떤 시도도 block하지 않는다(통과 처리).
2) `blockedUntilByIp[ip] > now`면 즉시 drop한다.  
   - 이 시도는 `rateLimit` 카운트에서 제외한다.
3) `recentAttemptsByIp[ip]`에서 `now - monitorMs`보다 오래된 기록을 제거한다.
4) 만약 이번 시도로 `threshold`를 초과하면:
   - `blockedUntilByIp[ip] = now + blockMs`
   - 이번 시도는 즉시 drop
   - 이번 시도는 `rateLimit` 카운트에서 제외
5) 위 조건에 걸리지 않은 시도만 `rateLimit` 검사 대상으로 본다.  
   - 현재 1초 윈도우(`rateWindowStartMs`)의 `rateCheckedInWindow`가 `rateLimit` 미만이면 검사 성공:
     - `rateCheckedInWindow++`
     - `recentAttemptsByIp[ip]`에 `now` 추가
     - 시도는 통과
   - `rateLimit` 이상이면 과부하 발생:
     - `overloadedUntilMs = now + recoveryMs`
     - 이번 시도는 block하지 않고 통과
     - 이후 `recoveryMs` 동안 모든 시도는 block하지 않음

카운트 규칙(필수):
- 차단된 IP에서 들어온 시도는 `rateLimit` 카운트에서 제외한다.
- `threshold` 초과를 유발한 시도는 즉시 drop하며 `rateLimit` 카운트에서 제외한다.
- 과부하 상태로 넘어간 이후의 시도는 검사 자체를 수행하지 않으므로 `rateLimit` 카운트 증가 대상이 아니다.

---

## 9) Logs (게임플레이용)

- 용도: 플레이어가 로그를 읽고/지우고/변조하는 플레이를 지원
- 로그는 링버퍼로 유지(`logCapacity`)

```text
LogStruct
- id: int
- time: int
- user: string                    # 표시용 userId 텍스트
- remoteIp: IP
- actionType: ENUM { login, logout, read, write, execute }
- action: string
- dirty: bool
- origin: Optional<LogStruct>
```

권장 규칙(v0):
- `dirty=false`면 `origin=null`
- `dirty=true`가 되는 순간에만 원본을 1회 저장

---

## 10) 구현 체크리스트(v0.2)

- [ ] `serverList(nodeId key)`, `ipIndex`, `processList` 전역 컨테이너 구현
- [ ] 서버 생성 시 `primaryIp/interfaces/subnetMembership/isExposedByNet` 초기화
- [ ] `interfaces[].initiallyExposed` 입력으로 초기 `KnownNodes/isExposedByNet`을 계산한 뒤 런타임에는 저장하지 않음
- [ ] `users`를 `userKey` 키로 관리하고 `UserConfig.userId`를 필수값으로 저장
- [ ] 세션/프로세스 내부 참조는 `userKey`, 표시/로그는 `userId` 사용
- [ ] `lanNeighbors`를 nodeId 기반으로 유지하고 `net.scan("lan")`은 IP 목록 반환
- [ ] OTP daemon 참조를 `userKey` 기준으로 검증
- [ ] `connectionRateLimiter` daemon 구현: `monitorMs/threshold/blockMs/rateLimit/recoveryMs` 규칙 반영
- [ ] 프로세스 tick: `now >= endAt` 처리 + 서버 reason 보호 규칙
- [ ] reboot 규칙: 서버 offline(reboot) 전환 + 프로세스/세션 정리 + booting 생성
- [ ] VFS overlay: `diskOverlay` + Resolve 우선순위 + overlayDir 델타 유지
- [ ] Logs: ringbuffer + origin 유지 규칙
