<a id="module-ftp"></a>
# FTP Module (ftp.*) - Manual

`ftp` 모듈은 가상 월드에서 파일을 전송할 때 사용하는 플레이어 API입니다.  
이 프로젝트의 `ftp`는 별도 `ftp.connect` 없이 SSH `session/route`를 인증 근거로 쓰고, 실제 전송 허용은 대상의 FTP 포트(기본 21) 접근 가능 여부로 판정합니다.

## 핵심 개념

- `sessionOrRoute`: `ftp` 함수의 첫 인자입니다. 단일 세션 또는 체인 라우트를 전달합니다.
- `remote endpoint` / `local endpoint`:
  - `Session` 모드: remote=`session.sessionNodeId`, local=현재 실행 컨텍스트
  - `SshRoute` 모드: remote=`route.lastSession`, local=`route.sessions[0]`
- 전송 방향:
  - `ftp.get`: `remote -> local`
  - `ftp.put`: `local -> remote`
- 포트 게이팅: 대상 포트가 `ftp` 타입이고 노출(exposure) 규칙을 통과해야 전송이 진행됩니다.

## ResultMap 읽는 법

모든 호출은 공통 ResultMap 패턴을 따릅니다.

```miniscript
{ ok: 1, code: "OK", err: null, ...payload }
{ ok: 0, code: "ERR_*", err: "message" }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 분기 처리용 안정 코드
- `err`: 사용자 표시용 메시지
- `payload`: 함수별 결과(`savedTo`, `bytes` 등)

## Quickstart

```miniscript
login = ssh.connect("10.0.20.9", "ops", "pw2")
if login.ok != 1 then
  term.error("connect failed: " + login.code)
  return
end if

down = ftp.get(login.session, "/home/ops/loot.txt", "/home/player/loot.txt")
if down.ok != 1 then
  term.error("download failed: " + down.code + " / " + down.err)
  ssh.disconnect(login.session)
  return
end if

term.print("saved: " + down.savedTo)
ssh.disconnect(login.session)
```

## 실패 처리 기본 패턴

```miniscript
r = ftp.get(sessOrRoute, remotePath)
if r.ok != 1 then
  if r.code == "ERR_PORT_CLOSED" then
    term.warn("target ftp port is closed")
  else if r.code == "ERR_NET_DENIED" then
    term.warn("network exposure denied")
  else if r.code == "ERR_PERMISSION_DENIED" then
    term.warn("check read/write privileges")
  else
    term.error("ftp failed: " + r.code + " / " + r.err)
  end if
  return
end if
```

<h2 id="ftpget">FTP Get (ftp.get)</h2>

See: [API Reference](ftp-api.md#ftpget).

### Signature

```miniscript
r = ftp.get(sessionOrRoute, remotePath, localPath?, opts?)
```

### 목적

원격 endpoint의 파일을 local endpoint로 다운로드합니다.  
실행 결과는 저장 경로(`savedTo`) 중심으로 확인하면 됩니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute)`: 전송 컨텍스트
- `remotePath (string)`: 원격에서 읽을 파일 경로
- `localPath (string, optional)`: 로컬 저장 경로 (생략 시 기본 경로 규칙 적용)
- `opts.port (int, optional)`: FTP 포트 (기본 21)

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, savedTo: "/path/to/file", bytes: 1234 }
```

- 대표 실패 코드: `ERR_PORT_CLOSED`, `ERR_NET_DENIED`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`

### 부작용

- 다운로드가 local endpoint에 반영되는 시점에 `fileAcquire` 이벤트가 enqueue 됩니다(transferMethod=`"ftp"`).

### 예제

```miniscript
r = ftp.get(route, "/opt/data/report.txt", "/home/player/report.txt")
if r.ok == 1 then
  term.print("downloaded to " + r.savedTo)
else
  term.error("ftp.get failed: " + r.code)
end if
```

<h2 id="ftpput">FTP Put (ftp.put)</h2>

See: [API Reference](ftp-api.md#ftpput).

### Signature

```miniscript
r = ftp.put(sessionOrRoute, localPath, remotePath?, opts?)
```

### 목적

local endpoint의 파일을 원격 endpoint로 업로드합니다.  
원격 반영 위치(`savedTo`)를 기준으로 후속 작업(원격 실행, 읽기 검증 등)을 이어갈 수 있습니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute)`: 전송 컨텍스트
- `localPath (string)`: 로컬에서 읽을 파일 경로
- `remotePath (string, optional)`: 원격 저장 경로 (생략 시 기본 경로 규칙 적용)
- `opts.port (int, optional)`: FTP 포트 (기본 21)

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, savedTo: "/remote/path", bytes: 1234 }
```

- 대표 실패 코드: `ERR_PORT_CLOSED`, `ERR_NET_DENIED`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`

### 부작용

- `ftp.put` 완료 시에는 `fileAcquire` 이벤트를 발행하지 않습니다.

### 예제

```miniscript
u = ftp.put(sess, "/home/player/tool.ms", "/tmp/tool.ms")
if u.ok != 1 then
  term.error("upload failed: " + u.code + " / " + u.err)
  return
end if

run = ssh.exec(sess, "miniscript /tmp/tool.ms")
if run.ok == 1 then
  term.print(run.stdout)
end if
```

## 함께 보면 좋은 API

- `ssh.connect`: 전송에 사용할 session/route를 준비할 때 사용
- `ssh.exec`: 업로드한 파일을 원격에서 실행/검증할 때 사용
- `fs.read` / `fs.write`: 전송 전후 파일 내용 확인 및 준비
- `net.ports`: FTP 포트 접근 가능 여부를 사전 확인할 때 사용
