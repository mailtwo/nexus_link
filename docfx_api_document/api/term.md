<a id="module-term"></a>
# TERM Module (term.*) - Manual

`term` 모듈은 스크립트 로그 출력과 로컬 명령 실행을 담당하는 플레이어 API입니다.  
`term.print/warn/error`는 메시지 출력에, `term.exec`는 현재 실행 컨텍스트에서 터미널 명령을 실행할 때 사용합니다.

## 들어가며

### 핵심 개념

- 출력 채널:
  - `term.print`는 표준 출력(stdout) 용도입니다.
  - `term.warn`, `term.error`는 stderr 로그 용도이며 호출 자체가 즉시 스크립트를 실패시키지는 않습니다.
- 로컬 실행:
  - `term.exec`는 `ssh.exec`의 로컬 버전입니다.
  - 현재 실행 컨텍스트(node/user/cwd)에서 명령 파싱/실행을 수행합니다.
- 동기/비동기:
  - 기본은 동기 실행(`stdout`, `exitCode` 반환)입니다.
  - `opts.async`를 사용하면 비동기 작업 스케줄 결과(`jobId`)를 받습니다.
- 속도 제한:
  - 현재 v0.2 규약에서 `term`은 shared 100k 제한 제외 그룹입니다.

### ResultMap 읽는 법

모든 호출은 공통 ResultMap 형태를 따릅니다.

```miniscript
{ ok: 1, code: "OK", err: null, cost: {...}, trace: {...}, ...payload }
{ ok: 0, code: "ERR_*", err: "message", cost: {...}, trace: {...} }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 분기 처리용 안정 코드
- `err`: 사용자 표시용 메시지
- `payload`: 함수별 결과 필드 (`printed`, `stdout`, `exitCode`, `jobId`)

### Quickstart

```miniscript
term.print("start local check")

r = term.exec("ls /home")
if r.ok != 1 then
  term.error("exec failed: " + r.code + " / " + r.err)
  return
end if

term.print(r.stdout)
term.warn("script completed")
```

### 실패 처리 기본 패턴

```miniscript
r = term.exec(cmd, {maxBytes: 4096})
if r.ok != 1 then
  if r.code == "ERR_TOO_LARGE" then
    term.warn("stdout too large; lower output or raise maxBytes")
  else if r.code == "ERR_UNKNOWN_COMMAND" then
    term.warn("command not found")
  else
    term.error("term.exec failed: " + r.code + " / " + r.err)
  end if
  return
end if
```

<h2 id="termprint">Term Print (term.print)</h2>

See: [API Reference](term-api.md#termprint).

### Signature

```miniscript
r = term.print(text)
```

### 목적

스크립트 진행 메시지를 stdout으로 1줄 출력합니다.

### 주요 파라미터

- `text (string)`: 출력할 메시지

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null }
```

- 대표 실패 코드: `ERR_INVALID_ARGS`

### 예제

```miniscript
r = term.print("connected")
if r.ok != 1 then
  return
end if
```

<h2 id="termwarn">Term Warn (term.warn)</h2>

See: [API Reference](term-api.md#termwarn).

### Signature

```miniscript
r = term.warn(text)
```

### 목적

경고 메시지를 stderr로 출력합니다. 출력 포맷은 `warn: <text>`입니다.

### 주요 파라미터

- `text (string)`: 경고 메시지

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null }
```

- 대표 실패 코드: `ERR_INVALID_ARGS`

### 예제

```miniscript
term.warn("retrying with different path")
```

<h2 id="termerror">Term Error (term.error)</h2>

See: [API Reference](term-api.md#termerror).

### Signature

```miniscript
r = term.error(text)
```

### 목적

오류 메시지를 stderr로 출력합니다. 출력 포맷은 `error: <text>`입니다.

### 주요 파라미터

- `text (string)`: 오류 메시지

### 반환 핵심

- 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null }
```

- 대표 실패 코드: `ERR_INVALID_ARGS`

### 예제

```miniscript
term.error("cannot continue")
return
```

<h2 id="termexec">Term Exec (term.exec)</h2>

See: [API Reference](term-api.md#termexec).

### Signature

```miniscript
r = term.exec(cmd, opts?)
```

### 목적

현재 실행 컨텍스트에서 로컬 명령을 실행하고 결과를 수집합니다.  
명령 해석/시스템 호출 규칙은 게임의 터미널 동작 정책을 따릅니다.

### 주요 파라미터

- `cmd (string)`: 실행할 단일 명령 라인 (공백-only 금지)
- `opts.maxBytes (int, optional)`: 동기 실행 시 stdout 바이트 상한
- `opts.async (bool/0|1, optional)`: 비동기 스케줄 모드

### 반환 핵심

- 동기 성공 예시:

```miniscript
{ ok: 1, code: "OK", err: null, stdout: "...", exitCode: 0, jobId: null }
```

- 동기 실패 예시:

```miniscript
{ ok: 0, code: "ERR_*", err: "message", stdout: "...", exitCode: 1, jobId: null }
```

- 비동기 성공 예시(`opts.async=1`):

```miniscript
{ ok: 1, code: "OK", err: null, stdout: null, exitCode: null, jobId: "job-..." }
```

- 비동기 실패 예시:

```miniscript
{ ok: 0, code: "ERR_*", err: "message", stdout: null, exitCode: null, jobId: null }
```

### 해석 팁

- `opts.async=1`의 `ok=1`은 명령 완료가 아니라 "작업 스케줄 성공" 의미입니다.
- `opts.async=1`일 때 `maxBytes`는 허용되지만 무시됩니다.
- `opts`에 지원되지 않는 키를 넣으면 `ERR_INVALID_ARGS`가 반환됩니다.

### 예제 (동기)

```miniscript
r = term.exec("cat /home/player/readme.txt", {maxBytes: 8192})
if r.ok == 1 then
  term.print(r.stdout)
else
  term.error(r.code + ": " + r.err)
end if
```

### 예제 (비동기)

```miniscript
j = term.exec("scan --deep", {async: 1})
if j.ok != 1 then
  term.error("schedule failed: " + j.code)
  return
end if

term.print("job queued: " + j.jobId)
```

## 함께 보면 좋은 API

- `ssh.exec`: 원격 endpoint 실행 버전
- `time.sleep`: 재시도/backoff 루프에 결합할 때 사용
- `net.scan`, `net.ports`: 실행 전 대상 탐색 단계에서 자주 함께 사용
