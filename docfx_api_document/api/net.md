<a id="module-net"></a>
# NET Module (net.*) - Manual

`net` 모듈은 가상 월드에서 네트워크 인터페이스 조회, 이웃 스캔, 포트 열람, 서비스 배너 조회를 수행할 때 사용하는 플레이어 API입니다.

## 들어가며

### 핵심 개념

- `sessionOrRoute`:
  - 생략 시 현재 실행 컨텍스트를 기준으로 동작합니다.
  - `Session` 또는 `SshRoute`를 주면 해당 endpoint 기준으로 동작합니다.
- `source` / `target`:
  - `source`는 요청을 시작한 쪽(endpoint 컨텍스트)입니다.
  - `target`은 조회하려는 대상 호스트입니다.
- 노출(exposure) 판정:
  - 포트 관련 API는 `source -> target` 네트워크 접근 규칙을 통과한 정보만 반환합니다.
- 권한:
  - `net.interfaces`, `net.scan`은 실행 컨텍스트 계정의 `execute` 권한이 필요합니다.

### ResultMap 읽는 법

모든 호출은 공통 ResultMap 형태를 따릅니다.

```miniscript
{ ok: 1, code: "OK", err: null, cost: {...}, trace: {...}, ...payload }
{ ok: 0, code: "ERR_*", err: "message", cost: {...}, trace: {...} }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 분기 처리용 안정 코드
- `err`: 사용자 표시용 메시지
- `payload`: 함수별 결과 필드 (`interfaces`, `ips`, `ports`, `banner`)

### Quickstart

```miniscript
ifs = net.interfaces()
if ifs.ok != 1 then
  term.error("interfaces failed: " + ifs.code)
  return
end if

scan = net.scan()
if scan.ok != 1 then
  term.error("scan failed: " + scan.code)
  return
end if

if len(scan.ips) == 0 then
  term.warn("no neighbor found")
  return
end if

targetIp = scan.ips[0]
p = net.ports(targetIp)
if p.ok != 1 then
  term.error("ports failed: " + p.code)
  return
end if

b = net.banner(targetIp, 22)
if b.ok == 1 then
  term.print("banner: " + b.banner)
end if
```

### 실패 처리 기본 패턴

```miniscript
r = net.ports(targetIp)
if r.ok != 1 then
  if r.code == "ERR_NOT_FOUND" then
    term.warn("target host/ip not found or offline")
  else if r.code == "ERR_NET_DENIED" then
    term.warn("network exposure denied from current endpoint")
  else
    term.error("net failed: " + r.code + " / " + r.err)
  end if
  return
end if
```

<h2 id="netinterfaces">Net Interfaces (net.interfaces)</h2>

See: [API Reference](net-api.md#netinterfaces).

### Signature

```miniscript
r = net.interfaces([sessionOrRoute])
```

### 목적

현재 endpoint 노드의 인터페이스 목록을 조회합니다.  
스캔/포트 조회 전에 "내가 어떤 네트워크에 붙어 있는지" 확인할 때 사용합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 조회 기준 endpoint

### 반환 핵심

- 성공 예시:

```miniscript
{
  ok: 1,
  code: "OK",
  err: null,
  interfaces: [{ netId: "corp-lan", localIp: "10.0.20.7" }]
}
```

- 대표 실패 코드: `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 예제

```miniscript
r = net.interfaces(sess)
if r.ok == 1 then
  for i in r.interfaces
    term.print(i.netId + " -> " + i.localIp)
  end for
end if
```

<h2 id="netscan">Net Scan (net.scan)</h2>

See: [API Reference](net-api.md#netscan).

### Signature

```miniscript
r = net.scan([sessionOrRoute], netId=null)
```

### 목적

endpoint 기준으로 이웃 노드 IP를 스캔합니다.  
라우트 개척 전에 탐색 후보를 빠르게 수집할 때 유용합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 스캔 기준 endpoint
- `netId (string|null, optional)`: 특정 인터페이스만 스캔할 때 지정

### 반환 핵심

- 성공 예시:

```miniscript
{
  ok: 1,
  code: "OK",
  err: null,
  interfaces: [{ netId: "corp-lan", localIp: "10.0.20.7", neighbors: ["10.0.20.9"] }],
  ips: ["10.0.20.9"]
}
```

- 대표 실패 코드: `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`, `ERR_NOT_FOUND`

### 실전 팁

- `netId`를 주지 않으면 endpoint의 비-internet 인터페이스를 모두 스캔합니다.
- 구버전 방식인 `net.scan("lan")`은 v0.2에서 지원되지 않습니다.

### 예제

```miniscript
r = net.scan(route, "corp-lan")
if r.ok != 1 then
  term.error("scan failed: " + r.code)
  return
end if

for ip in r.ips
  term.print("neighbor: " + ip)
end for
```

<h2 id="netports">Net Ports (net.ports)</h2>

See: [API Reference](net-api.md#netports).

### Signature

```miniscript
r = net.ports([sessionOrRoute], hostOrIp, opts?)
```

### 목적

대상 호스트의 서비스 포트를 조회합니다.  
어떤 프로토콜 경로(ssh/ftp/http 등)가 열려 있는지 판단할 때 사용합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: source endpoint 컨텍스트
- `hostOrIp (string)`: 포트를 조회할 대상
- `opts (map, optional)`: 확장용 옵션 (현재는 예약 성격)

### 반환 핵심

- 성공 예시:

```miniscript
{
  ok: 1,
  code: "OK",
  err: null,
  ports: [{ port: 22, portType: "ssh", exposure: "lan" }]
}
```

- 대표 실패 코드: `ERR_NOT_FOUND`, `ERR_NET_DENIED`, `ERR_INVALID_ARGS`

### 예제

```miniscript
r = net.ports("10.0.20.9")
if r.ok == 1 then
  for p in r.ports
    term.print(p.port + " / " + p.portType + " / " + p.exposure)
  end for
end if
```

<h2 id="netbanner">Net Banner (net.banner)</h2>

See: [API Reference](net-api.md#netbanner).

### Signature

```miniscript
r = net.banner([sessionOrRoute], hostOrIp, port)
```

### 목적

특정 포트의 배너/서비스 단서를 조회합니다.  
인증 전 수집 단계에서 버전/서비스 힌트를 얻을 때 사용합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: source endpoint 컨텍스트
- `hostOrIp (string)`: 조사 대상
- `port (int)`: 배너를 조회할 포트

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, banner: "OpenSSH_8.9p1" }
```

- 대표 실패 코드: `ERR_NOT_FOUND`, `ERR_NET_DENIED`, `ERR_PORT_CLOSED`, `ERR_INVALID_ARGS`

### 예제

```miniscript
b = net.banner(sess, "10.0.20.9", 22)
if b.ok == 1 then
  term.print("ssh hint: " + b.banner)
else
  term.warn("banner failed: " + b.code)
end if
```

## 함께 보면 좋은 API

- `ssh.connect`: 스캔 결과를 실제 접속 시도로 이어갈 때 사용
- `ssh.inspect`: SSH 계정 단서 수집 시 사용
- `ftp.get` / `ftp.put`: FTP 포트 확인 후 파일 전송 시 사용
- `time.sleep`: 재시도(backoff) 루프와 함께 사용할 때 유용
