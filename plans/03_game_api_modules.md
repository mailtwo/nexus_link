# 샌드박스 API 모듈 설계 (프로토타입 v0.2)

이 문서는 유저 MiniScript 프로그램이 접근할 **샌드박스(intrinsic) API 표면**을 정의한다.

스코프(이번 버전):
- 쉬움/중간/어려움(v0) 시나리오를 **클리어 가능**하게 만드는 최소 API 세트
- 포함 모듈: `term`, `time`, `fs`, `net`, `ssh`, `ftp`
- 의도적으로 제외(이번 파일에서 다루지 않음): `http/web/db/crypto/proc/...` 등

핵심 원칙:
- 유저는 **실제 OS/네트워크/디스크에 절대 접근하지 못한다.** 모든 API는 “가상 월드”만 조작한다.
- API는 “실제 해킹 페이로드”가 아니라, **전략·추론·리소스 관리**에 맞춘 추상화 계층이다.
- 모든 API는 **권한 검사 + 비용(cost) + 탐지(trace) + (필요 시) 이벤트 enqueue** 규약을 따른다.

---

## 0) 공통 규약

### 0.1 ResultMap (모든 intrinsic 공통 반환)
모든 intrinsic 함수는 예외를 던지지 않고 아래 형태의 맵을 반환한다.

```miniscript
{ ok: 1, data: <Any>, err: null, code: "OK", cost: {...}, trace: {...} }
{ ok: 0, data: null, err: "message", code: "ERR_*", cost: {...}, trace: {...} }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 안정적인 에러 코드(문자열)
- `err`: 사용자 표시용 메시지(로컬라이즈는 추후)
- `cost`: (권장) 비용/리소스 모델
- `trace`: (권장) 탐지/소음 모델

> v0에서는 `cost/trace`를 단순화(0 또는 최소 필드만)해도 된다. 단, 필드 형태는 고정해 둔다.

### 0.2 에러 코드(권장 최소)
- `OK`
- `ERR_INVALID_ARGS` (인자 타입/개수 오류)
- `ERR_NOT_FOUND` (대상 없음)
- `ERR_PERMISSION_DENIED` (권한 부족)
- `ERR_NOT_TEXT_FILE` (텍스트가 아닌 파일을 text API로 다룸)
- `ERR_ALREADY_EXISTS` (overwrite=false에서 파일 존재)
- `ERR_NOT_DIRECTORY` (dir 기대인데 file)
- `ERR_IS_DIRECTORY` (file 기대인데 dir)
- `ERR_PORT_CLOSED` (포트 비할당/서비스 없음)
- `ERR_NET_DENIED` (exposure/라우팅 규칙상 접근 불가)
- `ERR_AUTH_FAILED` (인증 실패)
- `ERR_RATE_LIMITED` (레이트리미터/쿨다운)
- `ERR_TOO_LARGE` (maxBytes 등 상한 초과)

### 0.3 비용/탐지 모델(권장)
- `cost.cpu` : 소비한 “연산 포인트” 또는 “시간 포인트”
- `cost.time` : 가상 시간(초) 또는 작업 소요 추정(초)
- `cost.ram` : 일시적 버퍼 사용(문자열/리스트 등)
- `trace.noise` : 소음(로그/IDS에 남는 정도)
- `trace.flags` : 탐지 규칙 트리거(예: `"BRUTE_FORCE"`, `"PORT_SCAN"`)

### 0.4 userId / userKey 경계(필수)
- **플레이어 입력/공개 API 경계에서는 `userId`만 사용한다.**
- `userKey`는 엔진 내부 참조 전용(세션/월드 상태에서만 사용)이며, 플레이어 입력/공개 API 응답에 노출하지 않는다.

### 0.5 SessionHandler DTO (ssh.connect 반환)
`ssh.connect` 성공 시 `data.session`은 아래 필드를 포함한다.

```text
Session
- kind: "sshSession"
- sessionId: string
- sessionNodeId: string     # 접속한 서버 nodeId
- userId: string            # 플레이어 노출 식별자
- hostOrIp: string          # connect 인자로 받은 값(대부분 IP)
- remoteIp: string          # 실제 접속된 IP
```

체인 연결(`opts.session`)을 사용할 때 `ssh.connect`는 `data.route`도 함께 반환한다.

```text
SshRoute
- kind: "sshRoute"
- version: 1
- sessions: List<Session>          # hop 순서(A->B, B->C, C->D ...)
- prefixRoutes: List<SshRoute>     # 자기 자신 제외 prefix route 목록
- lastSession: Session             # sessions 마지막 요소
- hopCount: int                    # sessions 길이
```

`prefixRoutes` 규칙(v0.2):
- 각 prefix route의 `prefixRoutes`는 빈 리스트여야 한다(재귀 중첩 금지).
- prefix route의 `sessions` 요소는 원 route `sessions`의 동일 요소를 재사용한다.
- 최대 hop 수는 `8`로 제한한다.

### 0.6 네트워크 접근(exposure) 판정 규칙(v0.2)
PortConfig(`ports[portNum]`)의 `exposure`는 아래 규칙으로 평가한다.
- 용어: `source`는 접속/요청을 시도하는 서버, `target`은 해당 포트를 가진 서버
- `public`: 모든 source에서 접근 허용
- `lan`: `source.subnetMembership`과 `target.subnetMembership`이 1개 이상 겹칠 때만 허용
- `localhost`: `source.nodeId == target.nodeId`일 때만 허용

포트가 “열려 있음”의 최소 정의(v0.2):
- `ports[port]`가 존재하고 `portType != none`이면 서비스가 존재한다.
- `portType == none`이면 비할당 포트로 간주하며 `exposure`는 무시한다.

### 0.7 [sessionOrRoute] 오버로드 규칙(권장)
여러 API는 **첫 인자로 session/sessionOrRoute를 선택적으로 받을 수 있다.**
- `foo(path, ...)`  : 현재 실행 컨텍스트(현재 host/user/cwd)에서 수행
- `foo(session, path, ...)` : 해당 session의 원격 host/user 컨텍스트에서 수행
- 일부 API(`ssh.exec`, `ftp.get`, `ftp.put`, `fs.list/read/write/delete/stat`)는 `sessionOrRoute`를 받아 `Session|SshRoute`를 모두 허용한다.

판정 규칙(v0.2):
- 첫 인자가 맵이고 `kind == "sshSession"`이면 session으로 해석한다.
- 첫 인자가 맵이고 `kind == "sshRoute"`이면 route로 해석한다. (`ssh.exec`, `ftp.get`, `ftp.put`, `fs.list/read/write/delete/stat`)
- 그 외에는 session 생략으로 간주한다.

> 이 규칙은 MiniScript에서 오버로딩을 흉내 내기 위한 “인자 타입 기반 디스패치”이다.
>
> route endpoint 규칙(v0.2):
> - `first = route.sessions[0]`
> - `last = route.lastSession` (`route.sessions[-1]`와 동일)
> - 별도 `firstSession` 필드는 추가하지 않는다.
>
> fs route 검증 규칙(v0.2):
> - `fs.*(route, ...)`는 `route.kind == "sshRoute"` 및 `route.lastSession` 해석 가능 여부만 필수로 본다.
> - `route.sessions/prefixRoutes/hopCount/version`은 fs 실행 전 필수 검증 대상에서 제외한다.
> - 실행 컨텍스트와 권한 판정은 항상 `route.lastSession` 기준이다.

### 0.8 비동기 작업(프로세스) 규약(권장)
- 다운로드/업로드 같은 시간 작업은 **프로세스(processList)** 로 모델링할 수 있다.
- MVP에서는 구현 난이도에 따라 동기(즉시 완료)로 시작해도 되지만,
  장기적으로는 `processFinished` 이벤트와 연결되는 형태를 권장한다.

### 0.9 이벤트 연동(필수 포인트)
- `ssh.connect` 성공 시: 로그인 계정이 보유한 `read/write/execute` 각각에 대해 `privilegeAcquire` 이벤트를 enqueue(중복 발행 금지).
- 파일 전송 완료로 “해당 API가 정의한 local 반영 지점에서 파일이 획득 가능해지는 순간” : `fileAcquire` 이벤트를 enqueue(transferMethod=`"ftp"`).
  - `ftp.get(route, ...)`에서는 local 반영 지점이 `route.sessions[0]`(first endpoint)이다.

---

## 1) term (터미널 출력)

### 1.1 `term.print(text)`
- 목적: 프로그램 진행 로그(표준 출력)
- 인자:
  - `text: string`
- 반환: `ResultMap` (`data=null`)

### 1.2 `term.warn(text)` / `term.error(text)`
- 목적: 경고/에러 로그(표준 오류 유사)
- 인자:
  - `text: string`
- 반환: `ResultMap` (`data=null`)

---

## 2) time (대기)

> 이번 버전에서는 `time.now()`를 **노출하지 않는다**. (OTP window 계산 등 플레이어 수학을 요구하지 않기 위함)

### 2.1 `time.sleep(seconds)`
- 목적:
  - 스크립트가 대기/재시도(backoff)하면서도 프레임을 멈추지 않게 “yield” 제공
- 인자:
  - `seconds: number` (0 이상)
- 조건:
  - 내부 시간 기준은 고정 스텝 WorldTick(60Hz) 누적이다.
- 반환:
  - 성공: `{ ok:1, data:{ slept: seconds }, ... }`
  - 실패: `ERR_INVALID_ARGS`

---

## 3) fs (가상 파일 시스템)

지원 함수(이번 버전): `list/read/write/delete/stat`

> 파일 탐색은 `fs.list`를 조합해 플레이어(또는 스크립트)가 직접 수행한다. (`fs.find`는 이번 버전에서 제외)

공통 규칙:
- 경로는 POSIX 스타일 문자열(`/` 시작 절대경로 권장)
- 경로는 정규화한다: `.` 제거, `..` pop, 중복 `/` 제거
- VFS 병합 우선순위: `tombstone > overlay > base`

### 3.1 `fs.list([sessionOrRoute], path)`
- 목적: 디렉토리 목록
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string` (디렉토리)
- 권한:
  - session 모드: 해당 session 사용자 `read`
  - route 모드: `lastSession` 사용자 `read`
- 반환 `data`:
  - `entries: List<{ name, entryKind: "File"|"Dir" }>`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_NOT_DIRECTORY`, `ERR_PERMISSION_DENIED`

### 3.2 `fs.read([sessionOrRoute], path, opts?)`
- 목적: 텍스트 파일 읽기
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string`
  - `opts.maxBytes?: int` (기본 상한 권장)
- 권한:
  - session 모드: 해당 session 사용자 `read`
  - route 모드: `lastSession` 사용자 `read`
- 동작:
  - `fileKind == Text`만 읽기 허용
  - `fileKind != Text`면 `ERR_NOT_TEXT_FILE`
  - `maxBytes` 초과 시 `ERR_TOO_LARGE`
- 반환 `data`:
  - `{ text: string }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_IS_DIRECTORY`, `ERR_NOT_TEXT_FILE`, `ERR_PERMISSION_DENIED`, `ERR_TOO_LARGE`

### 3.3 `fs.write([sessionOrRoute], path, text, opts?)`
- 목적: 텍스트 파일 쓰기/생성
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string`
  - `text: string`
  - `opts.overwrite?: bool` (기본 false 권장)
  - `opts.createParents?: bool` (기본 false 권장)
- 권한:
  - session 모드: 해당 session 사용자 `write`
  - route 모드: `lastSession` 사용자 `write`
- 동작:
  - 대상이 이미 존재하고 `overwrite=false`면 `ERR_ALREADY_EXISTS`
  - 기존 엔트리가 `Dir`이면 `ERR_IS_DIRECTORY`
  - 부모 디렉토리가 없거나 디렉토리가 아니면:
    - `createParents=false`면 `ERR_NOT_FOUND`/`ERR_NOT_DIRECTORY`
    - `createParents=true`면 필요한 디렉토리를 생성 후 진행
  - 쓰기는 overlay에 반영한다(기본/base는 직접 수정하지 않음)
- 반환 `data`:
  - `{ written: int /*chars*/, path: string }`
- 실패:
  - `ERR_INVALID_ARGS`, `ERR_ALREADY_EXISTS`, `ERR_IS_DIRECTORY`, `ERR_NOT_DIRECTORY`, `ERR_PERMISSION_DENIED`

### 3.4 `fs.delete([sessionOrRoute], path)`
- 목적: 파일/디렉토리 삭제
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string`
- 권한:
  - session 모드: 해당 session 사용자 `write`
  - route 모드: `lastSession` 사용자 `write`
- 동작:
  - base 파일 삭제는 tombstone으로 가림 처리
  - overlay 파일 삭제는 overlay 엔트리 제거
  - 디렉토리 삭제는 **비어 있을 때만** 허용(MVP 권장)
- 반환 `data`:
  - `{ deleted: 1 }` 또는 `{ deleted: 0 }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_NOT_DIRECTORY`(rmdir 조건 불일치 시), `ERR_INVALID_ARGS`

### 3.5 `fs.stat([sessionOrRoute], path)`
- 목적: 메타 조회
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string`
- 권한:
  - session 모드: 해당 session 사용자 `read`
  - route 모드: `lastSession` 사용자 `read`
- 반환 `data`(권장 최소):
  - `{ entryKind, fileKind?, size? }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`

---

## 4) net (스캔/포트/배너)

지원 함수(이번 버전): `scan/ports/banner`

### 4.1 `net.scan([session], subnetOrHost)`
- 목적: 네트워크 탐색(주로 LAN 이웃 탐색)
- 인자:
  - `subnetOrHost: string`
    - v0.2에서는 `"lan"`만 필수 지원
- 동작(`net.scan("lan")` 고정 규칙):
  1) 현재 컨텍스트 서버의 `lanNeighbors(nodeId)`를 읽는다.
  2) 각 이웃 nodeId를 “현재 컨텍스트 netId” 기준 IP로 변환한다.
  3) 최종 반환은 IP 문자열 리스트다.
- 권한:
  - 현재 컨텍스트 계정의 `execute=true`가 필요(권장)
- 반환 `data`:
  - `{ ips: List<string> }`
- 실패:
  - `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 4.2 `net.ports([session], hostOrIp, opts?)`
- 목적: 대상 호스트의 열린 포트(서비스) 조회
- 인자:
  - `hostOrIp: string` (v0.2에서는 IP 문자열을 권장)
- 동작:
  - 대상 서버의 `ports` 중 `portType != none`인 항목을 반환한다.
  - 네트워크 접근 가능 여부는 `exposure` 규칙으로 판정한다.
    - 접근 불가이면: v0에서는 전체 실패(`ERR_NET_DENIED`) 또는 결과 숨김 중 하나를 선택(일관되게 고정).
- 반환 `data`(권장):
  - `{ ports: List<{ port:int, portType:string, exposure:string }> }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_NET_DENIED`

### 4.3 `net.banner([session], hostOrIp, port)`
- 목적: 서비스 배너/버전 단서 조회
- 인자:
  - `hostOrIp: string`
  - `port: int`
- 조건:
  - `net.ports`와 동일한 네트워크 접근 판정 적용
  - 포트가 비할당(`portType==none`)이면 `ERR_PORT_CLOSED`
- 반환 `data`:
  - `{ banner: string }` (없으면 빈 문자열 허용)
- 실패:
  - `ERR_NOT_FOUND`, `ERR_NET_DENIED`, `ERR_PORT_CLOSED`

---

## 5) ssh (로그인/세션/원격 실행)

지원 함수(이번 버전): `connect/disconnect/exec`

### 5.1 `ssh.connect(hostOrIp, userId, password, port=22, opts?)`
- 목적: 가상 SSH 로그인(세션 생성)
- 인자:
  - `hostOrIp: string` (권장: IP)
  - `userId: string` (플레이어 노출 식별자)
  - `password: string`
  - `port: int` (기본 22)
  - `opts.session?: Session|SshRoute`
    - `Session`이면 해당 세션의 `sessionNodeId`를 source로 사용해 다음 hop을 연다.
    - `SshRoute`이면 `lastSession`을 source로 사용해 route 끝에 hop을 append 한다.
  - 인자 파싱 규칙:
    - `ssh.connect(host,user,pw,{session:x})` 허용 (4번째 인자 맵이면 `opts`로 해석)
    - `ssh.connect(host,user,pw,22,{session:x})` 허용
- 사전 조건:
  - target 서버의 `ports[port].portType == ssh` 이어야 한다.
  - `exposure` 판정을 통과해야 한다(0.6 규칙).
- 인증:
  - 서버의 계정 설정(authMode/daemon)에 따라 성공/실패를 결정한다.
  - 이 레이어에서는 “현실 SSH”가 아니라 **가상 인증 모듈**로 취급한다.
- 반환 `data`:
  - 기본: `{ session: Session, route: null }`
  - 체인(`opts.session` 사용): `{ session: Session, route: SshRoute }`
- 실패:
  - `ERR_NOT_FOUND`(대상 없음), `ERR_PORT_CLOSED`, `ERR_NET_DENIED`, `ERR_AUTH_FAILED`, `ERR_RATE_LIMITED`
- 부작용(필수):
  - 성공 시, 로그인 계정이 이미 보유한 `read/write/execute` 각각에 대해 `privilegeAcquire` 이벤트를 enqueue(중복 발행 금지).
  - `PrivilegeAcquireDto.via = "ssh.connect"`

### 5.2 `ssh.disconnect(sessionOrRoute)`
- 목적: 세션 해제
- 인자:
  - `sessionOrRoute: Session|SshRoute`
- 반환 `data`:
  - 공통: `{ disconnected: 1|0 }` (idempotent)
  - route 입력일 때 summary 포함:
    - `summary.requested`: dedupe 후 종료 시도한 hop 수
    - `summary.closed`: 실제 종료된 hop 수
    - `summary.alreadyClosed`: 이미 닫혀 있어 종료되지 않은 hop 수
    - `summary.invalid`: 잘못된 hop 수
- route 해제 규칙(v0.2):
  - route `sessions`를 끝 hop부터 역순으로 처리한다.
  - `(sessionNodeId, sessionId)` 기준 dedupe 후 1회만 종료 시도한다.
  - 공유 hop이라도 route에 포함되면 종료 시도한다.
  - 구조가 유효하면 일부 hop 실패가 있어도 `ok=1` + `summary`로 보고한다(best-effort).

### 5.3 `ssh.exec(sessionOrRoute, cmd, opts?)`
- 목적: 원격 호스트에서 커맨드 실행 + stdout 수집
- 인자:
  - `sessionOrRoute: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd에서 실행한다.
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd에서 실행한다.
  - `cmd: string` (단일 커맨드 라인)
  - `opts.maxBytes?: int` (stdout 상한 권장)
- 커맨드 해석(권장):
  - 터미널 명령 실행 규칙과 동일하게 처리한다.
    1) 시스템콜 registry 조회
    2) 미일치 시 프로그램 탐색(PATH=`/opt/bin`, 상대/절대 경로 규칙)
    3) 최종 미해결 시 `unknown command`
  - v0.2에서는 토큰화는 단순 공백 split을 허용(따옴표/이스케이프는 추후).
- 반환 `data`(권장):
  - `{ stdout: string, exitCode: int }`
- 실패:
  - route 구조 검증 실패 시 `ERR_INVALID_ARGS`
  - `ERR_INVALID_ARGS`, `ERR_PERMISSION_DENIED`, `ERR_NOT_FOUND`(프로그램/경로 없음), `ERR_TOO_LARGE`

---

## 6) ftp (파일 전송: delegated-auth; SFTP-like 동작 + FTP 포트 게이팅)

이번 프로젝트에서 `ftp`는 다음 규약을 따른다.
- **별도의 `ftp.connect`는 두지 않는다.**
- 파일 전송은 **SSH session을 인증/권한 증명으로 사용**한다(동작 형태는 SFTP 유사).
- 단, 전송 허용 여부는 **target의 `portType=ftp` 포트가 접근 가능해야** 한다.
  - 즉, “SFTP처럼 동작하지만 FTP 포트(기본 21)가 필요하다”는 게임 규약을 채택한다.

지원 함수(이번 버전): `get/put`

### 6.1 `ftp.get(sessionOrRoute, remotePath, localPath?, opts?)`
- 목적: 원격 endpoint → local endpoint 다운로드
- 인자:
  - `sessionOrRoute: Session|SshRoute`
    - `Session` 모드:
      - remote endpoint = `session.sessionNodeId`
      - local endpoint = 현재 실행 컨텍스트
    - `SshRoute` 모드:
      - remote endpoint = `route.lastSession`
      - local endpoint = `route.sessions[0]` (first endpoint)
      - 전송 방향은 `last -> first`로 고정한다.
  - `remotePath: string`
    - `Session` 모드: `session`의 cwd 기준 상대경로 허용
    - `SshRoute` 모드: `lastSession`의 cwd 기준 상대경로 허용
  - `localPath?: string`
    - `Session` 모드: 현재 실행 컨텍스트 cwd 기준(생략 시 `/home/player/<basename(remotePath)>` 권장)
    - `SshRoute` 모드: first endpoint session의 cwd 기준
  - `opts.port?: int` (기본 21)
  - `opts.overwrite?: bool` (기본 false 권장)
  - `opts.maxBytes?: int` (다운로드 상한; 권장)
- cwd 기본값(v0.2):
  - 현재 `Session`의 cwd 초기값은 `/`이다.
  - 세션별 cwd 변경 API는 아직 없고, 후속 버전에서 확장 가능하다.
- 필수 조건(게이팅):
  - `Endpoint direct only` 정책:
    - `Session` 모드: `source = 현재 실행 컨텍스트`, `target = session.sessionNodeId`
    - `SshRoute` 모드: `source = first`, `target = last`
  - `target`의 `ports[opts.port].portType == ftp` 이어야 함. 아니면 `ERR_PORT_CLOSED`
  - `source -> target`에 대해 `exposure` 판정 통과(0.6 규칙). 실패 시 `ERR_NET_DENIED`
- 권한 조건(권장):
  - `Session` 모드: 원격 read + 로컬 write
  - `SshRoute` 모드: `last(read) + first(write)`
- 완료 처리(필수):
  - local endpoint 반영 지점에서 `fileAcquire` 이벤트를 1회 enqueue
    - `fromNodeId = remote endpoint nodeId` (`SshRoute` 모드에서는 `last.sessionNodeId`)
    - `fileName = basename(remotePath)` (확장자 포함)
    - `transferMethod = "ftp"`
- 반환 `data`:
  - MVP(동기) 권장: `{ savedTo: localPath, bytes?: int }`
  - 비동기(권장)로 갈 경우: `{ processId: int }` 형태를 추가해도 된다.
- 실패:
  - `ERR_INVALID_ARGS`, `ERR_PORT_CLOSED`, `ERR_NET_DENIED`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_ALREADY_EXISTS`, `ERR_TOO_LARGE`

### 6.2 `ftp.put(sessionOrRoute, localPath, remotePath?, opts?)`
- 목적: local endpoint → 원격 endpoint 업로드
- 인자:
  - `sessionOrRoute: Session|SshRoute`
    - `Session` 모드:
      - local endpoint = 현재 실행 컨텍스트
      - remote endpoint = `session.sessionNodeId`
    - `SshRoute` 모드:
      - local endpoint = `route.sessions[0]` (first endpoint)
      - remote endpoint = `route.lastSession`
      - 전송 방향은 `first -> last`로 고정한다.
  - `localPath: string`
    - `Session` 모드: 현재 실행 컨텍스트 cwd 기준
    - `SshRoute` 모드: first endpoint session의 cwd 기준
  - `remotePath?: string`
    - `Session` 모드: 생략 시 `session` cwd 기준 `<basename(localPath)>` 권장
    - `SshRoute` 모드: 생략 시 `lastSession` cwd 기준 `<basename(localPath)>` 권장
  - `opts.port?: int` (기본 21)
  - `opts.overwrite?: bool` (기본 false 권장)
  - `opts.maxBytes?: int`
- 필수 조건(게이팅):
  - `Endpoint direct only` 정책:
    - `Session` 모드: `source = 현재 실행 컨텍스트`, `target = session.sessionNodeId`
    - `SshRoute` 모드: `source = first`, `target = last`
  - `target`의 ftp 포트 타입/노출 판정을 `source -> target` 기준으로 검사한다.
- 권한 조건(권장):
  - `Session` 모드: 로컬 read + 원격 write
  - `SshRoute` 모드: `first(read) + last(write)`
- 완료 처리(필수):
  - `fileAcquire` 이벤트는 발행하지 않는다(기존 정책 유지).
- 반환/실패:
  - `ftp.get`과 동일한 형식/정책을 따른다.

---

## 7) 앞으로 확장 가능한 부분(요약)

이번 파일에서 제외했지만, 추후 아래 확장을 고려할 수 있다(상세 스펙은 별도 문서/버전에서 정의).

- `ftp.list/stat/delete/rename/mkdir/rmdir` : FTP 작업 디렉토리/조작 커맨드군
- `fs.find` : 대규모 파일 트리에서 패턴 탐색(현재는 `fs.list` 조합으로 대체)
- `net.traceroute/route` : 라우팅/중계 퍼즐 확장
- `ssh.whoami/session.tokens` : 계정/토큰 가시화(디버깅/퍼즐용)
- hostname/DNS: `hostOrIp`에 hostname 지원 및 해석 규칙 추가
- 고급 quoting/escaping: `ssh.exec` 커맨드 라인 파서 강화

---

## 8) 레거시 정보

### 1) 웹/DB/앱 계층 모듈

#### 1.1 http (가상 HTTP 클라이언트)
- `http.get(url, opts)`
- `http.post(url, data, opts)`
- `http.upload(url, fileRef, opts)`
- `http.cookies.get(domain)` / `http.cookies.set(...)` (선택)

#### 1.2 web (웹앱 추상 진단/공격 지원)
(실제 페이로드 입력 대신 “의도 토큰” 방식 권장)
- `web.probe(url, kind)`  
  - kind 예: `"idor"`, `"sqli"`, `"xss"`, `"upload"`, `"ssrf"`
- `web.exploit(url, kind, params)`  
  - 성공/실패는 서버의 취약점 토글과 유저가 수집한 단서(권한/토큰/버전)에 의해 결정

#### 1.3 db (가상 DB)
- `db.query(conn, queryToken, params)`  
- `db.dump(conn, table, opts)` (권한/취약점에 따라 허용)

**설계 메모**
- SQLi 같은 건 “쿼리 문자열을 직접 입력”시키기보다
  - `web.exploit(..., "sqli", {goal:"auth_bypass"})` 같이 추상화하면 안전하고 UX도 좋아짐.

---

### 2) 메시징/사회공학 모듈

#### 2.1 mail (사내 메일/메신저)
- `mail.inbox(user)` → message list
- `mail.open(messageId)`
- `mail.send(from, to, subject, body, opts)`  
  - opts: `spoofAs`(취약점 토글이 켜진 서버에서만 가능), attachments

**설계 메모**
- “현실 기관 사칭” 대신 게임 세계관 내부 조직/도메인을 사용.
- 성공 조건은 SPF/DKIM 같은 실전 디테일 대신 “서버 설정 토글 + 신뢰 체인”으로 처리.

---

### 3) LLM/에이전트 모듈(근미래 루트)

#### 3.1 agent (문서 읽기 + 툴 호출)
- `agent.run(agentId, task, refs)` → job
- `agent.feed(agentId, docRef)` (RAG/문서 기반)
- `agent.tools(agentId)` → 사용 가능한 툴 목록
- `agent.approve(agentId, actionId)` (선택: 승인 단계)

**공격 루트 연결**
- 간접 프롬프트 인젝션, insecure output handling, toolchain 취약점, LLM DoS

#### 3.2 pipeline (자동 적용 파이프라인)
- `pipeline.preview(changeSet)` → diff
- `pipeline.apply(changeSet)` → (취약 시 자동 승인/자동 반영)

---

### 4) 로깅/모니터링(방어) 모듈

#### 4.1 monitor (탐지/경보/트레이스)
- `monitor.trace()` → 현재 추적 게이지
- `monitor.alerts()` → 활성 경보
- `monitor.logs(host, filter)` → (권한 있을 때만) 로그 열람

**설계 메모**
- 플레이어는 “로그가 곧 적”이기도 하고, 때로는 침투 후 “로그 삭제/변조” 같은 미션으로도 사용 가능(다만 지나친 현실 재현은 피하고 추상화).

---
