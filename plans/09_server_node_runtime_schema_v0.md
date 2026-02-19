# 서버 노드 런타임 데이터 스키마 v0 (Godot/PC, C#)

목적: 게임 실행 중(World runtime) 각 서버 노드가 유지해야 하는 상태를 **일관된 스키마**로 정의한다.  
Codex는 이 문서만 보고 런타임 모델(월드 `serverList`, `processList`)과 서버 동작(로그인/프로세스/로그/디스크 오버레이)을 구현할 수 있어야 한다.

범위(v0):
- 플랫폼: PC
- 엔진: Godot(로직 C#)
- 실제 OS/네트워크 접근 없음(전부 가상)
- 저장/로드 포맷은 **나중에** 결정(이번 문서는 런타임 메모리 구조 중심)

---

## 0) 전역 런타임 컨테이너(World Runtime)

월드는 최소 2개의 전역 딕셔너리를 유지한다.

### 0.1 `serverList`
- 타입: `Dictionary<IP, ServerStruct>`
- 키: `ip` (게임 전체 유일)

### 0.2 `processList`
- 타입: `Dictionary<int /*processId*/, ProcessStruct>`
- 키: `processId` (게임 전체 유일)

> 서버는 자신이 보유한 프로세스 id만 `server.process` set으로 참조한다.  
> 실제 프로세스 데이터는 항상 `processList`에서 조회한다(단일 진실).

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
- name: string                 # 프로세스 이름(디버그/표시)
- host: IP                     # 실행된 서버의 IP
- userId: string              # 실행시킨 유저 id (자동 실행이면 "system")
- state: ENUM { running, finished, canceled }
- path: string                 # 프로세스/프로그램 경로(예: /usr/local/bin/passwdGen)
- processType: ENUM           # 완료 시 수행할 행동 타입(예: booting, ftpSend, fileWrite...)
- processArgs: Dictionary<string, Any>   # 행동 실행에 필요한 파라미터
- endAt: int                  # 완료 시각(Unix time, ms 권장)
```

### 2.3 프로세스 처리 규칙(v0)
- 월드는 주기적으로 `processList`를 순회하며, `now >= endAt`인 `running` 프로세스를 완료 처리한다.
- 완료 처리:
  - 기본은 `state = finished`
  - `processType`에 따라 “완료 효과” 실행(예: booting이면 서버 online 전환)
- 서버 상태 보호 규칙:
  - 완료 효과를 적용하기 전에 서버(`serverList[host]`)의 `status/reason`을 확인한다.
  - 서버 `reason`이 `disabled` 또는 `crashed`인 경우: **완료 효과를 적용하지 않는다**(프로세스는 finished로만 처리).

### 2.4 reboot(booting) 프로세스 규칙(v0)
- reboot 명령 실행 시:
  1) 서버를 `status=offline`, `reason=reboot`로 전환
  2) 해당 서버가 가진 `server.process`에 포함된 모든 프로세스를:
     - `processList`에서 찾아 `state=canceled`로 전환하거나 제거
     - `server.process` set도 비움
  3) `booting` 타입 프로세스 1개를 생성하여 `processList`에 등록하고 `server.process`에 추가
- booting 완료 시(프로세스 완료 처리):
  - 서버가 `status=offline`이고 `reason=reboot`인 경우에만:
    - `status=online`, `reason=OK`로 전환
  - 그 외(이미 online이거나 disabled/crashed 등)는 효과 없음
- reboot이 여러 번 요청되는 경우(v0 정책):
  - 첫 reboot 이후 online 전환만 의미 있음.
  - 이후 들어온 reboot 프로세스는 효과를 주지 않도록 구현(또는 아예 생성하지 않음).

---

## 3) Server Struct (월드 전역)

### 3.1 `serverList`
- 타입: `Dictionary<IP, ServerStruct>`
- 키: `ip` (유일)

### 3.2 ServerStruct 필드
```text
ServerStruct
- name: string
- role: ENUM { terminal, otpGenerator, mainframe, tracer }
- status: ENUM { online, offline }               # 접속 가능 여부(논리 상태)
- reason: ENUM { OK, reboot, disabled, crashed } # 상태 설명
    - invariant: status=online일 때만 reason=OK
    - status=offline일 때 reason ∈ {reboot, disabled, crashed}
- ip: IP
```

---

## 4) Users / Sessions

### 4.1 users
- 타입: `Dictionary<string /*userId*/, UserConfig>`

```text
UserConfig
- userPasswd: Optional<string>          # static이면 실제 값, 자동이면 None
- authMode: ENUM { none, static, otp, 기타 }
- privilege: PrivilegeConfig
    - read: bool     # 파일 읽기/다운로드, 로그 읽기
    - write: bool    # 파일 쓰기/수정/업로드, 로그 작성/삭제
    - execute: bool  # 프로그램 실행, reboot, 서버 바운스
- info: List<string>
```

> execute 권한을 얻은 계정은 “bounce 노드로 활용 가능” (LAN 이동/피벗 규칙에 사용)

### 4.2 sessions
- 타입: `Dictionary<int /*sessionId*/, SessionConfig>`
- 키: `sessionId` (서버 단위 유일)

```text
SessionConfig
- userId: string
- remoteIp: IP
- cwd: string   # 현재 디렉토리(절대경로)
```

권장 규칙(v0):
- 서버 reboot 시작 시: `sessions`를 모두 종료(비우기)
- `cwd`는 VFS의 Normalize 규칙을 따름(심볼릭 링크 없음)

---

## 5) Network: lanNeighbors / ports

### 5.1 lanNeighbors
- 타입: `List<IP>`
- 의미: 같은 LAN 클러스터에서 인접한 서버 목록(직접 스캔/이동 가능)

### 5.2 ports
- 타입: `Dictionary<int /*portNum*/, PortConfig>`
- 키: `portNum`

```text
PortConfig
- portType: ENUM { SSH, FTP, HTTP, SQL }
- serviceId?: string                 # 이 포트가 제공하는 행동/핸들(예: "sshDefault", "sshNoauthRoot")
- exposure: ENUM { public, lan, localhost }
    - public: 외부(플레이어)에서 직접 접근 가능
    - lan:  LAN 내부에서만 접근 가능
    - localhost: 로컬에서만(디버그/연출용)
```

---

## 6) Disk: diskOverlay (서버별 VFS 델타)

> 공통 기본 파일시스템(BaseFS)은 전역에 있고, 서버는 **오버레이만** 가진다.  
> Base의 파일도 서버별로 삭제 가능해야 하므로 `tombstones`를 사용한다.

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
- overlayDir는 “현재 델타만” 유지하고, added/removed가 모두 비면 해당 dirPath 엔트리를 제거한다.

---

## 7) Server ↔ Process 연결

각 서버는 “현재 실행 중인 프로세스 id”만 별도로 가진다.

```text
process: HashSet<int /*processId*/>
```

규칙:
- 서버는 `process` set을 통해 자신의 프로세스를 추적한다.
- 실제 프로세스 내용은 `processList[processId]`에서 조회한다.
- reboot 시 정책:
  - 서버의 `process`에 포함된 모든 프로세스를 canceled 처리하고 set을 비운다.

---

## 8) Daemons (방어/행동 힌트용)

- 타입: `Dictionary<daemonType, DaemonStruct>`
- 키: daemonType

```text
DaemonStruct
- daemonType: ENUM { OTP, firewall, accessMonitor }
- daemonArgs: Dictionary<string, Any>
    OTP:
      - userId: string       # OTP로 제어되는 계정 id
      - ip: IP                # OTP 서버 주소
      - ttlMs: int           # PW 발급/사용 가능 시간(=1초 권장: 1000)
      - format: ENUM          # 예: "base64_32chars"
    firewall: { }             # v0에서는 비워둠
    accessMonitor:
      - ip: IP                # tracer 서버 주소(인접 LAN에 있어야 하고 tracer role 권장)
```

> OTP를 daemon으로 유지하는 이유: “이 서버 공략에 OTP 서버가 필요”함을 데이터만 보고도 알 수 있게 하기 위함(v0 UX).

---

## 9) Logs (게임플레이용)

- 용도: 플레이어가 로그를 읽고/지우고/변조하는 플레이를 지원
- 로그는 링버퍼로 유지(최대 `logCapacity`)

### 9.1 logCapacity
- 타입: int

### 9.2 logs (Ringbuffer)
로그 항목 스키마:

```text
LogStruct
- id: int                         # 로그 id(보통 index/sequence)
- time: int                       # Unix time (ms 권장)
- user: string                    # action 주체 userId
- remoteIp: IP                   # 접속한 서버 IP (로컬은 127.0.0.1)
- actionType: ENUM { login, logout, read, write, execute }
- action: string                  # 구체 행동(예: "ssh login", "cat /etc/x", "run passwdGen")
- dirty: bool                     # 수정 여부
- origin: Optional<LogStruct>     # 최초 원본(1회 저장), 추가 수정은 origin을 덮어쓰지 않음
```

권장 규칙(v0):
- `dirty=false`이면 `origin=null`
- `dirty=true`가 되는 순간에만 `origin`에 원본을 1회 저장(이후 덮어쓰지 않음)

---

## 10) 구현 체크리스트(v0)

- [ ] `serverList`, `processList` 전역 컨테이너 구현
- [ ] 프로세스 tick: `now >= endAt` 처리 + server reason 보호 규칙
- [ ] reboot 규칙: 서버 offline(reboot) 전환 + 프로세스/세션 정리 + booting 프로세스 생성
- [ ] SSH 로그인/세션: `sessions` 생성/삭제, privilege 적용
- [ ] VFS overlay: `diskOverlay` + Resolve 우선순위 + overlayDir 델타 유지
- [ ] Daemon: OTP/accessMonitor 최소 파싱(행동 힌트 및 규칙 적용)
- [ ] Logs: ringbuffer + 변조(origin 유지) 규칙

