<a id="module-fs"></a>
# FS Module (fs.*) - Manual

`fs` 모듈은 가상 파일 시스템의 디렉터리 조회, 텍스트 읽기/쓰기, 삭제, 메타 조회를 제공합니다.
모든 함수는 선택적으로 `sessionOrRoute`를 받아 현재 컨텍스트 대신 원격 endpoint(`session` 또는 `route.lastSession`) 기준으로 동작할 수 있습니다.

## 들어가며
### 핵심 개념

- `sessionOrRoute` 오버로드:
  - 생략: 현재 실행 컨텍스트(node/user/cwd) 기준
  - `Session`: 해당 세션의 endpoint/cwd 기준
  - `SshRoute`: `route.lastSession`의 endpoint/cwd 기준
- 권한:
  - `fs.list`, `fs.read`, `fs.stat`은 `read` 권한 필요
  - `fs.write`, `fs.delete`는 `write` 권한 필요
- 경로 해석:
  - `path`는 endpoint의 `cwd` 기준으로 정규화됩니다.
- opts 규약:
  - `fs.read`는 `opts.maxBytes`만 허용
  - `fs.write`는 `opts.overwrite`, `opts.createParents`만 허용
  - 지원하지 않는 opts 키는 `ERR_INVALID_ARGS`

### ResultMap 읽는 법
모든 호출은 공통 ResultMap을 반환합니다.

```miniscript
{ ok: 1, code: "OK", err: null, ...payload }
{ ok: 0, code: "ERR_*", err: "message" }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 분기 처리용 안정 코드
- `err`: 사용자 표시용 메시지
- `payload`: 함수별 결과 필드 (`entries`, `text`, `written`, `path`, `deleted`, `entryKind` 등)

### Quickstart

```miniscript
items = fs.list("/home")
if items.ok != 1 then
  term.error("list failed: " + items.code + " / " + items.err)
  return
end if

r = fs.read("/home/player/readme.txt", {maxBytes: 4096})
if r.ok == 1 then
  term.print(r.text)
end if

w = fs.write("/home/player/note.txt", "hello", {overwrite: 1})
if w.ok == 1 then
  term.print("saved: " + w.path)
end if
```

### 실패 처리 기본 패턴

```miniscript
r = fs.read(path, {maxBytes: 4096})
if r.ok != 1 then
  if r.code == "ERR_NOT_FOUND" then
    term.warn("path not found")
  else if r.code == "ERR_NOT_TEXT_FILE" then
    term.warn("binary file cannot be read as text")
  else if r.code == "ERR_TOO_LARGE" then
    term.warn("file is larger than opts.maxBytes")
  else
    term.error("fs.read failed: " + r.code + " / " + r.err)
  end if
  return
end if
```

<h2 id="fslist">FS List (fs.list)</h2>

See: [API Reference](fs-api.md#fslist).

### Signature

```miniscript
r = fs.list([sessionOrRoute], path)
```

### 목적

디렉터리의 자식 엔트리 목록을 조회합니다.
각 항목은 이름과 엔트리 종류(`File`/`Dir`)를 제공합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 조회 기준 endpoint
- `path (string)`: 조회할 디렉터리 경로

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, entries: [{name: "readme.txt", entryKind: "File"}] }
```

- 실패 코드 예시: `ERR_NOT_FOUND`, `ERR_NOT_DIRECTORY`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 예제

```miniscript
r = fs.list(route, "/opt")
if r.ok == 1 then
  for e in r.entries
    term.print(e.entryKind + " " + e.name)
  end for
end if
```

<h2 id="fsread">FS Read (fs.read)</h2>

See: [API Reference](fs-api.md#fsread).

### Signature

```miniscript
r = fs.read([sessionOrRoute], path, opts?)
```

### 목적

텍스트 파일 내용을 읽어 `text`로 반환합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 읽기 기준 endpoint
- `path (string)`: 읽을 파일 경로
- `opts.maxBytes (int, optional)`: 허용 최대 바이트 수(UTF-8 기준). 초과 시 실패

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, text: "..." }
```

- 실패 코드 예시: `ERR_NOT_FOUND`, `ERR_IS_DIRECTORY`, `ERR_NOT_TEXT_FILE`, `ERR_TOO_LARGE`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 노트

- `opts`는 현재 `maxBytes` 키만 허용합니다.
- `maxBytes`는 0 이상 정수만 허용합니다.

### 예제

```miniscript
r = fs.read(sess, "/etc/banner.txt", {maxBytes: 8192})
if r.ok == 1 then
  term.print(r.text)
end if
```

<h2 id="fswrite">FS Write (fs.write)</h2>

See: [API Reference](fs-api.md#fswrite).

### Signature

```miniscript
r = fs.write([sessionOrRoute], path, text, opts?)
```

### 목적

텍스트 파일을 생성/갱신하고 실제 저장 경로를 반환합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 쓰기 기준 endpoint
- `path (string)`: 대상 파일 경로
- `text (string)`: 저장할 텍스트
- `opts.overwrite (bool-like, optional)`: 기존 파일 덮어쓰기 허용
- `opts.createParents (bool-like, optional)`: 누락된 부모 디렉터리 생성 허용

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, written: 5, path: "/home/player/note.txt" }
```

- 실패 코드 예시: `ERR_ALREADY_EXISTS`, `ERR_IS_DIRECTORY`, `ERR_NOT_DIRECTORY`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 노트

- `opts`는 현재 `overwrite`, `createParents`만 허용합니다.
- 성공 시 파일 획득 이벤트(`fileAcquire`)가 `transferMethod="fs.write"`로 발생할 수 있습니다.

### 예제

```miniscript
r = fs.write(route, "/tmp/notes/todo.txt", "scan targets", {createParents: 1, overwrite: 1})
if r.ok == 1 then
  term.print("written=" + r.written)
end if
```

<h2 id="fsdelete">FS Delete (fs.delete)</h2>

See: [API Reference](fs-api.md#fsdelete).

### Signature

```miniscript
r = fs.delete([sessionOrRoute], path)
```

### 목적

파일 또는 빈 디렉터리를 삭제합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 삭제 기준 endpoint
- `path (string)`: 삭제 대상 경로

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, deleted: 1 }
```

- 실패 코드 예시: `ERR_NOT_FOUND`, `ERR_NOT_DIRECTORY`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 노트

- 루트 경로(`/`) 삭제는 허용되지 않습니다.
- 비어있지 않은 디렉터리 삭제 시 `ERR_NOT_DIRECTORY`로 실패합니다.

### 예제

```miniscript
r = fs.delete("/home/player/old.log")
if r.ok != 1 then
  term.warn("delete failed: " + r.code)
end if
```

<h2 id="fsstat">FS Stat (fs.stat)</h2>

See: [API Reference](fs-api.md#fsstat).

### Signature

```miniscript
r = fs.stat([sessionOrRoute], path)
```

### 목적

대상 경로의 엔트리 종류/파일 종류/크기 메타를 조회합니다.

### 주요 파라미터

- `sessionOrRoute (Session|SshRoute, optional)`: 조회 기준 endpoint
- `path (string)`: 메타를 확인할 경로

### 반환 핵심

- 파일 예시:

```miniscript
{ ok: 1, code: "OK", err: null, entryKind: "File", fileKind: "Text", size: 120 }
```

- 디렉터리 예시:

```miniscript
{ ok: 1, code: "OK", err: null, entryKind: "Dir", fileKind: null, size: null }
```

- 실패 코드 예시: `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 예제

```miniscript
s = fs.stat(sess, "/home/player")
if s.ok == 1 then
  term.print("kind=" + s.entryKind)
end if
```

## 함께 보면 좋은 API

- `ssh.connect`, `ssh.exec`: 원격 endpoint 컨텍스트를 확보/활용할 때 사용
- `ftp.get`, `ftp.put`: endpoint 간 파일 전송이 필요할 때 사용
- `net.scan`, `net.ports`: 접근 경로/포트 조건을 먼저 확인할 때 사용
