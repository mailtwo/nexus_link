<a id="module-ssh"></a>
# SSH Module (ssh.*) - Manual

`ssh` 모듈은 가상 월드에서 SSH 로그인, 세션 유지/해제, 원격 명령 실행, 계정 단서 조회를 수행할 때 사용하는 플레이어 API입니다.

## 들어가며

### 핵심 개념

- `Session`: 단일 SSH 로그인 결과입니다. 원격 실행의 기본 단위입니다.
- `Route`: 체인 접속(`opts.session`)으로 쌓인 hop 집합입니다. route API는 보통 마지막 hop(`lastSession`)을 실행 endpoint로 봅니다.
- `source` / `endpoint`:
  - `source`는 접속/요청을 시작한 쪽입니다.
  - `endpoint`는 실제 동작이 수행되는 목적지입니다.
- 권한/접근:
  - SSH 자체는 포트/노출(exposure)/인증 조건을 통과해야 합니다.
  - 로그인 후 API 실행은 계정 권한(`read/write/execute`)과 실행 컨텍스트에 영향을 받습니다.

### ResultMap 읽는 법

모든 호출은 공통 ResultMap을 반환합니다.

```miniscript
{ ok: 1, code: "OK", err: null, cost: {...}, trace: {...}, ...payload }
{ ok: 0, code: "ERR_*", err: "message", cost: {...}, trace: {...} }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 분기 처리용 안정 코드
- `err`: 사용자 표시용 메시지
- `payload`: 함수별 결과 필드 (`data` 래퍼 없음)

### Quickstart

```miniscript
login = ssh.connect("10.255.0.14", "root", "black_security_pw2")
if login.ok != 1 then
  term.error("connect failed: " + login.code + " / " + login.err)
  return
end if

run = ssh.exec(login.session, "ls /home")
if run.ok == 1 then
  term.print(run.stdout)
else
  term.warn("exec failed: " + run.code)
end if

ssh.disconnect(login.session)
```

### 실패 처리 기본 패턴

```miniscript
r = ssh.connect(targetIp, userId, password)
if r.ok != 1 then
  if r.code == "ERR_NET_DENIED" then
    term.warn("network path blocked; check scan/ports first")
  else if r.code == "ERR_RATE_LIMITED" then
    time.sleep(1)
  else
    term.error("ssh failed: " + r.code + " / " + r.err)
  end if
  return
end if
```

<h2 id="sshconnect">SSH Connect (ssh.connect)</h2>

See: [API Reference](ssh-api.md#sshconnect).

### Signature

```miniscript
r = ssh.connect(hostOrIp, userId, password, port=22, opts?)
```

### 목적

대상 서버에 SSH 로그인을 시도해 `session`을 생성합니다.  
`opts.session`을 주면 기존 세션/라우트 끝에서 다음 hop으로 접속해 `route`를 함께 반환할 수 있습니다.

### 주요 파라미터

- `hostOrIp (string)`: 접속 대상 호스트/IP
- `userId (string)`: 로그인할 계정 ID
- `password (string)`: 인증 문자열
- `port (int, default=22)`: SSH 포트
- `opts.session (Session|SshRoute, optional)`: 체인 접속 기준 세션/라우트

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, session: sshSession, route: sshRoute|null }
```

- 실패 예시:

```miniscript
{ ok: 0, code: "ERR_*", err: "message", session: null, route: null }
```

- 대표 실패 코드: `ERR_PORT_CLOSED`, `ERR_NET_DENIED`, `ERR_AUTH_FAILED`, `ERR_RATE_LIMITED`

### 예제

```miniscript
first = ssh.connect("10.0.10.5", "admin", "pw1")
if first.ok != 1 then return

second = ssh.connect("10.0.20.9", "ops", "pw2", {session:first.session})
if second.ok == 1 then
  term.print("hop count ready")
end if
```

<h2 id="sshdisconnect">SSH Disconnect (ssh.disconnect)</h2>

See: [API Reference](ssh-api.md#sshdisconnect).

### Signature

```miniscript
r = ssh.disconnect(sessionOrRoute)
```

### 목적

세션 또는 라우트를 해제합니다.  
route 입력 시 hop을 정리하며, 결과는 요약(summary)로 확인합니다.

### 반환 핵심

- 반환 예시:

```miniscript
{
  ok: 1,
  code: "OK",
  err: null,
  disconnected: 0|1,
  summary: { requested, closed, alreadyClosed, invalid }
}
```

### 예제

```miniscript
out = ssh.disconnect(routeOrSession)
if out.ok == 1 then
  term.print("closed: " + out.summary.closed)
end if
```

<h2 id="sshexec">SSH Exec (ssh.exec)</h2>

See: [API Reference](ssh-api.md#sshexec).

### Signature

```miniscript
r = ssh.exec(sessionOrRoute, cmd, opts?)
```

### 목적

원격 endpoint에서 명령을 실행하고 결과를 수집합니다.  
동기 호출은 `stdout/exitCode`, 비동기 호출은 `jobId` 중심으로 처리합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute)`: 실행 컨텍스트
- `cmd (string)`: 실행할 단일 명령 라인
- `opts.maxBytes (int, optional)`: 동기 출력 상한
- `opts.async (bool, optional)`: `true`면 스케줄 결과만 즉시 반환

### 반환 핵심

- 동기 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, stdout: "...", exitCode: 0, jobId: null }
```

- 비동기 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, stdout: null, exitCode: null, jobId: "job-..." }
```

- 대표 실패 코드: `ERR_PERMISSION_DENIED`, `ERR_UNKNOWN_COMMAND`, `ERR_TOO_LARGE`, `ERR_INVALID_ARGS`

### 예제

```miniscript
r = ssh.exec(sess, "cat /etc/banner.txt")
if r.ok == 1 then
  term.print(r.stdout)
else
  term.error(r.code)
end if
```

<h2 id="sshinspect">SSH Inspect (ssh.inspect)</h2>

See: [API Reference](ssh-api.md#sshinspect).

### Signature

```miniscript
r = ssh.inspect(hostOrIp, userId, port=22, opts?)
```

### 목적

공식 프로그램 `inspect`가 제공하는 InspectProbe 정보를 intrinsic 형태로 조회합니다.  
실행 전 현재 컨텍스트에서 `inspect` 실행 파일을 찾고 권한을 확인합니다.

### 주요 파라미터

- `hostOrIp (string)`: 조사 대상
- `userId (string)`: 조회할 계정
- `port (int, default=22)`: 검사 대상 SSH 포트

### 반환 핵심

- 성공 예시:

```miniscript
{
  ok: 1,
  code: "OK",
  err: null,
  hostOrIp: "...",
  port: 22,
  userId: "...",
  banner: "...",
  passwdInfo: { kind: "...", ... }
}
```

- 대표 실패 코드: `ERR_TOOL_MISSING`, `ERR_PERMISSION_DENIED`, `ERR_PORT_CLOSED`, `ERR_NET_DENIED`, `ERR_RATE_LIMITED`

### 예제

```miniscript
probe = ssh.inspect("10.0.20.9", "ops")
if probe.ok != 1 then
  term.error("inspect failed: " + probe.code)
  return
end if

term.print("kind=" + probe.passwdInfo.kind)
```

## 다음 단계

- 접속 경로를 먼저 확보하려면 `net.interfaces`, `net.scan`, `net.ports`를 조합하세요.
- 원격 실행 이후 파일 작업이 필요하면 `fs.*`, 전송이 필요하면 `ftp.get/ftp.put`를 사용하세요.
