# 샌드박스 API 모듈 설계 (프로토타입 v0.2)

이 문서는 유저 MiniScript 프로그램이 접근할 **샌드박스(intrinsic) API 표면**을 정의한다.

스코프(이번 버전):
- 쉬움/중간/어려움(v0) 시나리오를 **클리어 가능**하게 만드는 최소 API 세트
- 현재 노출 모듈: `term`, `fs`, `net`, `ssh`, `ftp`
- 미노출(설계 보류): `time`
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
{ ok: 1, err: null, code: "OK", cost: {...}, trace: {...}, ...payload }
{ ok: 0, err: "message", code: "ERR_*", cost: {...}, trace: {...} }
```

- `ok`: 성공(1) / 실패(0)
- `code`: 안정적인 에러 코드(문자열)
- `err`: 사용자 표시용 메시지(로컬라이즈는 추후)
- `cost`: (권장) 비용/리소스 모델
- `trace`: (권장) 탐지/소음 모델
- `payload` 필드: API별 반환값은 최상위 필드로 직접 노출하며 공통 `data` 래퍼를 사용하지 않는다.

> v0에서는 `cost/trace`를 단순화(0 또는 최소 필드만)해도 된다. 단, 필드 형태는 고정해 둔다.

### 0.2 에러 코드(권장 최소)
- `OK`
- `ERR_UNKNOWN_COMMAND` (명령/프로그램 해석 실패)
- `ERR_INVALID_ARGS` (인자 타입/개수 오류)
- `ERR_NOT_FOUND` (대상 없음)
- `ERR_TOOL_MISSING` (필요한 공식 프로그램/툴이 없음)
- `ERR_PERMISSION_DENIED` (권한 부족)
- `ERR_NOT_TEXT_FILE` (텍스트가 아닌 파일을 text API로 다룸)
- `ERR_ALREADY_EXISTS` (overwrite=false에서 파일 존재)
- `ERR_NOT_DIRECTORY` (dir 기대인데 file)
- `ERR_NOT_EMPTY` (비어있지 않은 디렉토리 삭제 시)
- `ERR_IS_DIRECTORY` (file 기대인데 dir)
- `ERR_PORT_CLOSED` (포트 비할당/서비스 없음)
- `ERR_NET_DENIED` (exposure/라우팅 규칙상 접근 불가)
- `ERR_AUTH_FAILED` (인증 실패)
- `ERR_RATE_LIMITED` (레이트리미터/쿨다운)
- `ERR_TOO_LARGE` (maxBytes 등 상한 초과)
- `ERR_INTERNAL_ERROR` (엔진 내부 실패/실행 컨텍스트 불일치)

### 0.3 비용/탐지 모델(권장)
- `cost.cpu` : 소비한 “연산 포인트” 또는 “시간 포인트”
- `cost.time` : 가상 시간(초) 또는 작업 소요 추정(초)
- `cost.ram` : 일시적 버퍼 사용(문자열/리스트 등)
- `trace.noise` : 소음(로그/IDS에 남는 정도)
- `trace.flags` : 탐지 규칙 트리거(예: `"BRUTE_FORCE"`, `"PORT_SCAN"`)

### 0.4 userId / userKey 경계(필수)
- **플레이어 입력/공개 API 경계에서는 `userId`만 사용한다.**
- `userKey`는 엔진 내부 참조 전용(세션/월드 상태에서만 사용)이며, 플레이어 입력/공개 API 응답에 노출하지 않는다.

### 0.5 Session DTO (ssh.connect 반환)
`ssh.connect` 성공 시 최상위 `session`은 아래 필드를 포함한다.

```text
Session
- kind: "sshSession"
- sessionId: string
- sessionNodeId: string     # 접속한 서버 nodeId
- sourceNodeId: string      # 이 hop을 연 source endpoint nodeId
- sourceUserId: string      # source endpoint의 플레이어 노출 userId
- sourceCwd: string         # source endpoint cwd
- userId: string            # 플레이어 노출 식별자
- hostOrIp: string          # connect 인자로 받은 값(대부분 IP)
- remoteIp: string          # 실제 접속된 IP
```

`source*` 필드는 route/ftp 체인 해석에 필요한 공개 계약 필드다. 누락되면
`ssh.connect(opts.session)`, `ssh.exec(route)`, `ftp.get/put(route)`에서 `ERR_INVALID_ARGS`가 발생할 수 있다.

체인 연결(`opts.session`)을 사용할 때 `ssh.connect`는 최상위 `route`도 함께 반환한다.

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
- 일부 API(`ssh.exec`, `ftp.get`, `ftp.put`, `fs.list/read/write/delete/stat`, `net.interfaces/scan/ports/banner`)는 `sessionOrRoute`를 받아 `Session|SshRoute`를 모두 허용한다.
- 예외: `ftp.get/put`는 `sessionOrRoute` 생략 오버로드를 지원하지 않으며 첫 인자가 필수다.

판정 규칙(v0.2):
- 첫 인자가 맵이고 `kind == "sshSession"`이면 session으로 해석한다.
- 첫 인자가 맵이고 `kind == "sshRoute"`이면 route로 해석한다. (`ssh.exec`, `ftp.get`, `ftp.put`, `fs.list/read/write/delete/stat`, `net.interfaces/scan/ports/banner`)
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
  - `ftp.get(route, ...)`에서는 local 반영 지점이 `route.sessions[0]`의 source endpoint(first endpoint)이다.

### 0.10 스크립트 인자(`argv`/`argc`) 규약(v0.2)
- MiniScript 실행 컨텍스트는 전역 `argv`, `argc`를 제공한다.
- `argv`: `List<string>` (스크립트에 전달된 인자 목록)
- `argc`: `int` (`argv` 길이)
- 프로그램 실행 경로(`miniscript`, `ExecutableScript`)의 해석/실행 계약은 `14_official_programs.md`를 따른다.  
  See DOCS_INDEX.md → 14.
- 본 문서에서는 그 실행 결과로 전달되는 `argv`/`argc` 노출 규약만 정의한다.
- 인자가 없으면 `argv=[]`, `argc=0`이다.

### 0.11 intrinsic 호출 속도 제한(shared 100k)
- 본 문서에서 정의하는 인게임 API intrinsic은 기본적으로 **인터프리터 단위 shared 버킷(초당 100,000회)** 제한 대상이다.
- 현재 버전의 **제외 API group**은 `term`, `time`만이다.
- 따라서 현재 포함 모듈 기준으로는 `fs`, `net`(`interfaces/scan/ports/banner` 포함), `ssh`, `ftp`가 shared 100k 제한 대상이다.
- 추후 확장되는 API group(예: `http/web/db/crypto/proc` 등)도 **별도 제외 선언이 없으면** 동일하게 shared 100k 제한에 포함한다.
- 제외 API group 목록은 향후 늘어날 수 있으나, 본 문서(v0.2) 기준 공식 제외 목록은 `term`, `time`만으로 본다.

### 0.12 API 문서 파생/생성 규약
- 본 문서(`03_game_api_modules.md`)는 intrinsic API 규약의 SSOT다. ResultMap 규약, 에러 코드, 시그니처/인자/반환/부작용 정의는 이 문서에서만 정의한다.
- DocFX 기반 API 설명서와 코드 XML docstring은 본 문서를 기반으로 생성/유지되는 **파생 문서**다. 파생 문서에서 새로운 규약을 정의하지 않는다.
- 파생 문서 계층은 아래처럼 구분한다.
  - Manual Markdown: `docfx_api_document/api/<module>.md` (학습/온보딩/사용 흐름 중심)
  - API Reference: DocFX 자동 생성 레퍼런스(`api/*.yml` 기반 페이지, 시그니처/멤버 중심)
- Manual 문서는 개념/학습 흐름을 우선하고, 세부 규칙(정밀 검증 순서, 전체 에러 매트릭스, 엄격한 전제조건)은 본 SSOT 또는 XML docstring source를 참조로 연결한다.
- Manual 문서에서 신규 규약/신규 에러/신규 부작용을 정의하지 않는다.
- 문서 생성 흐름은 다음 순서를 고정한다.
  - `plans/03` 규약 변경
  - 코드 반영
  - XML docstring 반영
  - DocFX 산출물 생성
- 파생 문서에서 규약 변경 필요가 발견되면 먼저 본 문서를 수정한 뒤 파생 문서를 재생성한다(역정의 금지).
- Manual 제목 규칙:
  - 문서 H1: `<Module Name> Module (<module>.*) - Manual`
  - 함수 섹션 H2: `<Function Title> (<module>.<function>)`
  - 예시: `SSH Module (ssh.*) - Manual`, `SSH Connect (ssh.connect)`
- Manual 앵커 규칙:
  - 모듈 앵커: `<a id="module-<module>"></a>`
  - 함수 앵커: 함수 섹션 H2는 `id="<module><function>"` (소문자, 구분자 없음)으로 고정한다
  - 예시(`ssh`): `module-ssh`, `sshconnect`, `sshdisconnect`, `sshexec`, `sshinspect`
- API intrinsic XML docstring 작성 원칙:
  - `summary`: 함수 목적 1줄
  - `remarks`: MiniScript signature, ResultMap 핵심 키(`ok/code/err/cost/trace`), 주요 전제/부작용
  - `<see href="...">` 링크는 1개만 사용
- 링크 규칙은 `/api/<module>.html#<anchor>`를 표준으로 사용한다.
- DocFX 루트 TOC 운영 규칙:
  - `Manual` 섹션은 Manual Markdown 문서를 노출한다.
  - `API` 섹션은 자동 생성 Reference 트리를 유지한다.

---

## 1) term (터미널 출력/로컬 명령 실행)

### 1.1 `term.print(text)`
- 목적: 프로그램 진행 로그(표준 출력) 1줄 출력
- 인자:
  - `text: string`
- 반환:
  - 성공: `{ ok:1, code:"OK", err:null }`
  - 실패(인자 오류): `{ ok:0, code:"ERR_INVALID_ARGS", err:string }`
  - 현재 구현은 `printed`/`cost`/`trace` payload를 추가하지 않는다.

### 1.2 `term.warn(text)` / `term.error(text)`
- 목적: 경고/에러 로그를 표준 오류로 출력
- 인자:
  - `text: string`
- 출력 규칙:
  - `term.warn`는 `warn: <text>`를 stderr로 출력
  - `term.error`는 `error: <text>`를 stderr로 출력
  - 두 호출은 **로그 출력용**이며, 자체적으로 스크립트 실패를 유발하지 않는다
- stderr 판정 규칙(v0.2 구현):
  - `warn:` 또는 `error:` prefix stderr 라인은 non-fatal로 취급
  - 그 외 stderr 라인만 fatal로 취급
- 반환:
  - 성공: `{ ok:1, code:"OK", err:null }`
  - 실패(인자 오류): `{ ok:0, code:"ERR_INVALID_ARGS", err:string }`
  - 현재 구현은 `printed`/`cost`/`trace` payload를 추가하지 않는다.

### 1.3 `term.exec(cmd, opts?)`
- 목적: 현재 실행 컨텍스트(현재 node/user/cwd)에서 로컬 명령 실행 (`ssh.exec`의 로컬 버전)
- 인자:
  - `cmd: string` (공백-only 금지)
  - `opts?: map`
- `opts`:
  - `maxBytes?: int` (UTF-8 stdout byte 상한)
  - `async?: int` (`0/1`만 허용; `0=false`, `1=true`)
  - 지원하지 않는 key/음수/비정수는 `ERR_INVALID_ARGS`
  - `async=1`일 때 `maxBytes`는 허용되지만 무시한다
- 권한:
  - 별도 우회 없이 기존 터미널 system call 권한 검사(read/write/execute 등)를 그대로 따른다
- 반환:
  - 동기 성공: `{ ok:1, code:"OK", err:null, stdout:string, exitCode:0, jobId:null }`
  - 동기 실패: `{ ok:0, code:"ERR_*", err:string, stdout:string, exitCode:1, jobId:null }`
  - 비동기 스케줄 성공(`opts.async=1`): `{ ok:1, code:"OK", err:null, stdout:null, exitCode:null, jobId:string }`
  - 비동기 즉시 실패(파싱/스케줄 실패): `{ ok:0, code:"ERR_*", err:string, stdout:null, exitCode:null, jobId:null }`
- 해석 규칙:
  - `opts.async=1`일 때 `ok/code`는 "명령 완료"가 아니라 "비동기 작업 스케줄 성공" 의미다
- 실패 코드 예시:
  - `ERR_INVALID_ARGS`, `ERR_PERMISSION_DENIED`, `ERR_TOO_LARGE`, `ERR_NOT_FOUND`, `ERR_UNKNOWN_COMMAND`, `ERR_INTERNAL_ERROR`

---

## 2) time (현재 미노출)

- 현재 구현(v0.2)에서는 `time` 모듈을 인터프리터에 주입하지 않는다.
- 따라서 `time.sleep`, `time.now`는 인게임 intrinsic API로 호출할 수 없다.
- 대기/yield는 MiniScript 기본 intrinsic `wait`를 사용한다(게임 고유 intrinsic 아님).

---

## 3) fs (가상 파일 시스템)

지원 함수(이번 버전): `list/read/write/delete/stat`

> 파일 탐색은 `fs.list`를 조합해 플레이어(또는 스크립트)가 직접 수행한다. (`fs.find` intrinsic은 이번 버전에서 제외)
>
> 주의: 여기서 제외되는 것은 **MiniScript intrinsic `fs.find`** 이다.  
> VFS 내부 구현 보조 함수(`08_vfs_overlay_design_v0.md`의 `find`)와는 별개다.

공통 규칙:
- 경로 문자열/정규화/병합 우선순위는 `08_vfs_overlay_design_v0.md`를 따른다.  
  See DOCS_INDEX.md → 08.

### 3.1 `fs.list([sessionOrRoute], path)`
- 목적: 디렉토리 목록
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
    - 생략하면 현재 실행 컨텍스트 endpoint 기준
  - `path: string` (디렉토리)
- 권한:
  - session 모드: 해당 session 사용자 `read`
  - route 모드: `lastSession` 사용자 `read`
  - 생략 모드: 현재 실행 컨텍스트 사용자 `read`
- 반환(최상위 필드):
  - `entries: List<{ name, entryKind: "File"|"Dir" }>`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_NOT_DIRECTORY`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 3.2 `fs.read([sessionOrRoute], path, opts?)`
- 목적: 텍스트 파일 읽기
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string`
  - `opts.maxBytes?: int` (0 이상 정수)
  - 인자 오버로드:
    - session 생략 시: `fs.read(path, opts?)`
    - session 포함 시: `fs.read(sessionOrRoute, path, opts?)`
- 권한:
  - session 모드: 해당 session 사용자 `read`
  - route 모드: `lastSession` 사용자 `read`
  - 생략 모드: 현재 실행 컨텍스트 사용자 `read`
- 동작:
  - `fileKind == Text`만 읽기 허용
  - `fileKind != Text`면 `ERR_NOT_TEXT_FILE`
  - `maxBytes` 초과 시 `ERR_TOO_LARGE`
  - `opts`는 현재 `maxBytes` 키만 허용(그 외 key는 `ERR_INVALID_ARGS`)
- 반환(최상위 필드):
  - `{ text: string }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_IS_DIRECTORY`, `ERR_NOT_TEXT_FILE`, `ERR_PERMISSION_DENIED`, `ERR_TOO_LARGE`, `ERR_INVALID_ARGS`

### 3.3 `fs.write([sessionOrRoute], path, text, opts?)`
- 목적: 텍스트 파일 쓰기/생성
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session의 서버/계정/cwd 기준
    - `SshRoute`이면 `route.lastSession`의 서버/계정/cwd 기준
  - `path: string`
  - `text: string`
  - `opts.overwrite?: bool-like` (기본 false)
  - `opts.createParents?: bool-like` (기본 false)
  - 인자 오버로드:
    - session 생략 시: `fs.write(path, text, opts?)`
    - session 포함 시: `fs.write(sessionOrRoute, path, text, opts?)`
- 권한:
  - session 모드: 해당 session 사용자 `write`
  - route 모드: `lastSession` 사용자 `write`
  - 생략 모드: 현재 실행 컨텍스트 사용자 `write`
- 동작:
  - `opts`는 현재 `overwrite`, `createParents` 키만 허용(그 외 key는 `ERR_INVALID_ARGS`)
  - 대상이 이미 존재하고 `overwrite=false`면 `ERR_ALREADY_EXISTS`
  - 기존 엔트리가 `Dir`이면 `ERR_IS_DIRECTORY`
  - 부모 디렉토리가 없거나 디렉토리가 아니면:
    - `createParents=false`면 `ERR_NOT_FOUND`/`ERR_NOT_DIRECTORY`
    - `createParents=true`면 필요한 디렉토리를 생성 후 진행
  - 쓰기는 overlay에 반영한다(기본/base는 직접 수정하지 않음)
  - 성공 시(RealWorld 모드) `fileAcquire` 이벤트를 `transferMethod="fs.write"`로 enqueue한다
- 반환(최상위 필드):
  - `{ written: int /*chars*/, path: string }`
- 실패:
  - `ERR_INVALID_ARGS`, `ERR_ALREADY_EXISTS`, `ERR_IS_DIRECTORY`, `ERR_NOT_DIRECTORY`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`

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
  - 생략 모드: 현재 실행 컨텍스트 사용자 `write`
- 동작:
  - 루트 경로(`/`) 삭제는 금지(`ERR_INVALID_ARGS`)
  - base 파일 삭제는 tombstone으로 가림 처리
  - overlay 파일 삭제는 overlay 엔트리 제거
  - 디렉토리 삭제는 **비어 있을 때만** 허용
  - 비어있지 않은 디렉토리 삭제 시 `ERR_NOT_EMPTY` 반환
- 반환(최상위 필드):
  - 성공 시 `{ deleted: 1 }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_NOT_EMPTY`(rmdir 조건 불일치 시), `ERR_INVALID_ARGS`

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
  - 생략 모드: 현재 실행 컨텍스트 사용자 `read`
- 반환(최상위 필드):
  - 파일: `{ entryKind:"File", fileKind:string, size:number }`
  - 디렉토리: `{ entryKind:"Dir", fileKind:null, size:null }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

---

## 4) net (인터페이스/스캔/포트/배너)

지원 함수(이번 버전): `interfaces/scan/ports/banner`

### 4.1 `net.interfaces([sessionOrRoute])`
- 목적: 현재 endpoint 노드의 인터페이스 목록 조회
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session endpoint 기준
    - `SshRoute`이면 `route.lastSession` endpoint 기준
- 권한:
  - 실행 컨텍스트 계정의 `execute=true`가 필요(권장)
    - route 모드에서는 `route.lastSession` 사용자 기준
- 반환(최상위 필드):
  - `{ interfaces: List<{ netId:string, localIp:string }> }`
- 실패:
  - `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`

### 4.2 `net.scan([sessionOrRoute], netId=null)`
- 목적: endpoint 노드의 비-internet 인터페이스 기준 이웃 스캔
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session endpoint 기준
    - `SshRoute`이면 `route.lastSession` endpoint 기준
  - `netId?: string|null`
    - 생략/null이면 endpoint의 모든 비-internet 인터페이스를 스캔
    - 지정하면 해당 netId 인터페이스만 스캔
    - `net.scan("lan")` 호환은 제거(v0.2): `ERR_INVALID_ARGS`
- 동작:
  1) 스캔 대상 인터페이스를 결정한다(비-internet + localIp 보유).
  2) 각 인터페이스별로 `lanNeighbors(nodeId)`를 순회하며 동일 `netId`의 neighbor IP를 수집한다.
  3) 인터페이스별 이웃 목록(`neighbors`)을 구성한다.
  4) 호환 필드로 전체 이웃 IP의 unique/sorted 합집합(`ips`)도 함께 제공한다.
- 권한:
  - 실행 컨텍스트 계정의 `execute=true`가 필요(권장)
    - route 모드에서는 `route.lastSession` 사용자 기준
- 반환(최상위 필드):
  - `{ interfaces: List<{ netId:string, localIp:string, neighbors: List<string> }>, ips: List<string> }`
- 실패:
  - `ERR_PERMISSION_DENIED`, `ERR_INVALID_ARGS`, `ERR_NOT_FOUND`(지정 netId 미존재)

> `scan` 시스템콜 출력 포맷/UX는 터미널 문서가 SSOT다.  
> See DOCS_INDEX.md → 07 (`07_ui_terminal_prototype_godot.md`).

### 4.3 `net.ports([sessionOrRoute], hostOrIp, opts?)`
- 목적: 대상 호스트의 열린 포트(서비스) 조회
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session을 source 컨텍스트로 사용
    - `SshRoute`이면 `route.lastSession`을 source 컨텍스트로 사용
  - `hostOrIp: string` (v0.2에서는 IP 문자열을 권장)
  - `opts?: map` (현재 구현은 옵션 키 미지원; 빈 맵 또는 생략만 허용)
- 동작:
  - 대상 서버의 `ports` 중 `portType != none`인 항목을 반환한다.
  - 네트워크 접근 가능 여부는 `exposure` 규칙으로 판정한다(실행 source 컨텍스트 기준).
    - 접근 불가 포트는 현재 구현에서 에러로 중단하지 않고 결과 목록에서 제외한다.
- 반환(최상위 필드, 권장):
  - `{ ports: List<{ port:int, portType:string, exposure:string }> }`
- 실패:
  - `ERR_NOT_FOUND`, `ERR_INVALID_ARGS`

### 4.4 `net.banner([sessionOrRoute], hostOrIp, port)`
- 목적: 서비스 배너/버전 단서 조회
- 인자:
  - `sessionOrRoute?: Session|SshRoute`
    - `Session`이면 해당 session을 source 컨텍스트로 사용
    - `SshRoute`이면 `route.lastSession`을 source 컨텍스트로 사용
  - `hostOrIp: string`
  - `port: int`
- 조건:
  - `net.ports`와 동일한 네트워크 접근 판정 적용(실행 source 컨텍스트 기준)
  - 포트가 비할당(`portType==none`)이면 `ERR_PORT_CLOSED`
  - `banner` 값은 현재 구현에서 target `ports[port].serviceId`를 trim한 문자열을 사용한다.
- 반환(최상위 필드):
  - `{ banner: string }` (없으면 빈 문자열 허용)
- 실패:
  - `ERR_NOT_FOUND`, `ERR_NET_DENIED`, `ERR_PORT_CLOSED`

---

## 5) ssh (로그인/세션/원격 실행)

지원 함수(이번 버전): `connect/disconnect/exec/inspect`

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
    - `opts` 지원 키는 `session`이며, 다른 키는 `ERR_INVALID_ARGS`
- 사전 조건:
  - target 서버의 `ports[port].portType == ssh` 이어야 한다.
  - `exposure` 판정을 통과해야 한다(0.6 규칙).
- 인증:
  - 서버의 계정 설정(authMode/daemon)에 따라 성공/실패를 결정한다.
  - 이 레이어에서는 “현실 SSH”가 아니라 **가상 인증 모듈**로 취급한다.
  - `authMode=otp` 계정은 daemon(`stepMs/allowedDriftSteps/otpPairId`) 기반 TOTP 검증만 사용한다(발급 토큰 TTL 모델 미사용).
- 반환(최상위 필드):
  - 기본: `{ session: Session, route: null }`
  - 체인(`opts.session` 사용): `{ session: Session, route: SshRoute }`
- 실패:
  - `ERR_INVALID_ARGS`(포트/opts 형식 오류, 포트 타입 불일치 등)
  - `ERR_NOT_FOUND`(host/user/port 미존재, offline 포함)
  - `ERR_PERMISSION_DENIED`(exposure 거부, 인증 실패, connectionRateLimiter 차단)
  - `ERR_INTERNAL_ERROR`
- 부작용(필수):
  - 성공 시, 로그인 계정이 이미 보유한 `read/write/execute` 각각에 대해 `privilegeAcquire` 이벤트를 enqueue(중복 발행 금지).
  - `PrivilegeAcquireDto.via = "ssh.connect"`

### 5.2 `ssh.disconnect(sessionOrRoute)`
- 목적: 세션 해제
- 인자:
  - `sessionOrRoute: Session|SshRoute`
- 반환(최상위 필드):
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
  - `opts.async?: int` (`0/1`만 허용; `0=false`, `1=true`)
- 커맨드 해석(권장):
  - 터미널 명령 파싱/시스템콜/프로그램 fallback 규칙은 `07_ui_terminal_prototype_godot.md` 및 `14_official_programs.md`를 따른다.  
    See DOCS_INDEX.md → 07, 14.
  - 본 API는 해당 실행 결과(`stdout`, `exitCode`)를 route/session 컨텍스트로 래핑해 반환한다.
- 반환:
  - 동기 성공: `{ ok:1, code:"OK", err:null, stdout:string, exitCode:int, jobId:null }`
  - 동기 실패: `{ ok:0, code:"ERR_*", err:string, stdout:string, exitCode:int, jobId:null }`
  - 비동기 스케줄 성공(`opts.async=1`): `{ ok:1, code:"OK", err:null, stdout:null, exitCode:null, jobId:string }`
  - 비동기 즉시 실패(파싱/스케줄 실패): `{ ok:0, code:"ERR_*", err:string, stdout:null, exitCode:null, jobId:null }`
- 추가 규칙:
  - `opts.async=1`일 때 `maxBytes`는 허용되지만 무시한다
  - `opts.async=1`일 때 `ok/code`는 "원격 명령 완료"가 아니라 "비동기 작업 스케줄 성공" 의미다
- 실패:
  - route 구조 검증 실패 시 `ERR_INVALID_ARGS`
  - `ERR_INVALID_ARGS`, `ERR_PERMISSION_DENIED`, `ERR_NOT_FOUND`(프로그램/경로 없음), `ERR_UNKNOWN_COMMAND`, `ERR_TOO_LARGE`, `ERR_INTERNAL_ERROR`


### 5.4 `ssh.inspect(hostOrIp, userId, port=22, opts?)`
- 목적: 공식 제공 프로그램 `inspect`가 정의한 InspectProbe 결과를 intrinsic 형태로 조회한다.
  - 힌트 산출 규약(처리 순서/은닉/스키마 의미), 에러 의미, 실패 로그/부작용, 비용·탐지, 레이트리밋(shared limit) 규칙은 `14_official_programs.md`의 InspectProbe를 source of truth로 한다.
- 인자:
  - `hostOrIp: string`
  - `userId: string` (필수)
  - `port: int` (기본 22)
  - `opts?: map` (현재 구현은 옵션 키 미지원; 키 전달 시 `ERR_INVALID_ARGS`)
- 인자 파싱(허용):
  - `ssh.inspect(hostOrIp, userId, { ...opts })`
  - `ssh.inspect(hostOrIp, userId, port, { ...opts })`
- 추가 사전조건(ssh.inspect 전용): `inspect` 실행 파일 존재
  - `inspect`는 현재 실행 컨텍스트 preflight 규칙으로 찾아져야 한다(MUST).
    - 탐색 순서: `Normalize(cwd, "inspect")` -> 현재 endpoint `/opt/bin/inspect`
    - 워크스테이션 global PATH fallback은 `ssh.inspect` preflight에는 적용하지 않는다.
  - resolve 실패 또는 실행 파일 종류 불일치(`ExecutableHardcode(exec:inspect)`가 아님) → `ERR_TOOL_MISSING`
  - 실행 권한 부족(`read + execute` 필요) → `ERR_PERMISSION_DENIED`
- 반환:
  - `ssh.inspect`도 공통 규약대로 **최상위 직접 필드**를 반환한다(`data` 중첩 사용 안 함).
  - 성공:
    - `ok=1`
    - `code="OK"`
    - `err=null`
    - `hostOrIp`, `port`, `userId`, `banner`, `passwdInfo`
  - 실패:
    - `ok=0`
    - `code="ERR_*"`
    - `err=<string>`
- 실패(code):
  - `ERR_INVALID_ARGS`
  - `ERR_TOOL_MISSING`
  - `ERR_PERMISSION_DENIED`
  - `ERR_NOT_FOUND`
  - `ERR_PORT_CLOSED`
  - `ERR_NET_DENIED`
  - `ERR_AUTH_FAILED`
  - `ERR_RATE_LIMITED`
  - `ERR_INTERNAL_ERROR`


---

## 6) ftp (파일 전송: delegated-auth; SFTP-like 동작 + FTP 포트 게이팅)

이번 프로젝트에서 `ftp`는 다음 규약을 따른다.
- **별도의 `ftp.connect`는 두지 않는다.**
- 파일 전송은 **SSH session을 인증/권한 증명으로 사용**한다(동작 형태는 SFTP 유사).
- 단, 전송 허용 여부는 **target의 `portType=ftp` 포트가 접근 가능해야** 한다.
  - 즉, “SFTP처럼 동작하지만 FTP 포트(기본 21)가 필요하다”는 게임 규약을 채택한다.

지원 함수(이번 버전): `get/put`

공통 route 해석 규칙(v0.2 구현):
- route 입력은 `route.version/hopCount/sessions/lastSession/prefixRoutes` 전체 구조가 유효해야 한다.
- 각 `session`에는 `sourceNodeId/sourceUserId/sourceCwd`가 필요하다.
- 누락/불일치 시 `ERR_INVALID_ARGS`를 반환한다.
- `first endpoint`는 `route.sessions[0]` 자체가 아니라 `route.sessions[0]`의 source metadata로 계산한다.

### 6.1 `ftp.get(sessionOrRoute, remotePath, localPath?, opts?)`
- 목적: 원격 endpoint → local endpoint 다운로드
- 인자:
  - `sessionOrRoute: Session|SshRoute`
    - `Session` 모드:
      - remote endpoint = `session.sessionNodeId`
      - local endpoint = 현재 실행 컨텍스트
    - `SshRoute` 모드:
      - remote endpoint = `route.lastSession`
      - local endpoint = `route.sessions[0]`의 source endpoint (first endpoint)
      - 전송 방향은 `last -> first`로 고정한다.
  - `remotePath: string`
    - `Session` 모드: `session`의 cwd 기준 상대경로 허용
    - `SshRoute` 모드: `lastSession`의 cwd 기준 상대경로 허용
  - `localPath?: string`
    - `Session` 모드: 현재 실행 컨텍스트 cwd 기준(생략 시 `<cwd>/<basename(remotePath)>`)
    - `SshRoute` 모드: first endpoint(source)의 cwd 기준
  - `opts.port?: int` (기본 21)
  - `opts`는 현재 구현에서 `port` 키만 지원한다.
- cwd 기본값(v0.2):
  - 현재 `Session`의 cwd 초기값은 `/`이다.
  - 세션별 cwd 변경 API는 아직 없고, 후속 버전에서 확장 가능하다.
- 필수 조건(게이팅):
  - `Endpoint direct only` 정책:
    - `Session` 모드: `source = 현재 실행 컨텍스트`, `target = session.sessionNodeId`
    - `SshRoute` 모드: `source = first(source endpoint)`, `target = last`
  - `target`의 포트 검증은 `TryValidatePortAccess` 결과를 그대로 따른다.
    - 포트 미존재/미할당: `ERR_NOT_FOUND`
    - 포트 타입 불일치(ftp 아님): `ERR_INVALID_ARGS`
    - `source -> target` exposure 거부: `ERR_PERMISSION_DENIED`
- 권한 조건(권장):
  - `Session` 모드: 원격 read + 로컬 write
  - `SshRoute` 모드: `last(read) + first(write)`
- 완료 처리(필수):
  - local endpoint 반영 지점에서 `fileAcquire` 이벤트를 1회 enqueue
    - `fromNodeId = remote endpoint nodeId` (`SshRoute` 모드에서는 `last.sessionNodeId`)
    - `fileName = basename(remotePath)` (확장자 포함)
    - `transferMethod = "ftp"`
- 반환(최상위 필드):
  - 현재 구현(동기): `{ savedTo: localPath, bytes?: int }`
- 실패:
  - `ERR_INVALID_ARGS`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_NOT_DIRECTORY`, `ERR_IS_DIRECTORY`

### 6.2 `ftp.put(sessionOrRoute, localPath, remotePath?, opts?)`
- 목적: local endpoint → 원격 endpoint 업로드
- 인자:
  - `sessionOrRoute: Session|SshRoute`
    - `Session` 모드:
      - local endpoint = 현재 실행 컨텍스트
      - remote endpoint = `session.sessionNodeId`
    - `SshRoute` 모드:
      - local endpoint = `route.sessions[0]`의 source endpoint (first endpoint)
      - remote endpoint = `route.lastSession`
      - 전송 방향은 `first -> last`로 고정한다.
  - `localPath: string`
    - `Session` 모드: 현재 실행 컨텍스트 cwd 기준
    - `SshRoute` 모드: first endpoint(source)의 cwd 기준
  - `remotePath?: string`
    - `Session` 모드: 생략 시 `session` cwd 기준 `<basename(localPath)>` 권장
    - `SshRoute` 모드: 생략 시 `lastSession` cwd 기준 `<basename(localPath)>` 권장
  - `opts.port?: int` (기본 21)
  - `opts`는 현재 구현에서 `port` 키만 지원한다.
- 필수 조건(게이팅):
  - `Endpoint direct only` 정책:
    - `Session` 모드: `source = 현재 실행 컨텍스트`, `target = session.sessionNodeId`
    - `SshRoute` 모드: `source = first(source endpoint)`, `target = last`
  - `target`의 포트 검증은 `TryValidatePortAccess` 결과를 그대로 따른다.
    - 포트 미존재/미할당: `ERR_NOT_FOUND`
    - 포트 타입 불일치(ftp 아님): `ERR_INVALID_ARGS`
    - `source -> target` exposure 거부: `ERR_PERMISSION_DENIED`
- 권한 조건(권장):
  - `Session` 모드: 로컬 read + 원격 write
  - `SshRoute` 모드: `first(read) + last(write)`
- 완료 처리(필수):
  - `fileAcquire` 이벤트는 발행하지 않는다(기존 정책 유지).
- 반환/실패:
  - `ftp.get`과 동일한 형식을 따른다.
  - 실패 코드는 `ERR_INVALID_ARGS`, `ERR_NOT_FOUND`, `ERR_PERMISSION_DENIED`, `ERR_NOT_DIRECTORY`, `ERR_IS_DIRECTORY`를 사용한다.

---

## 7) 앞으로 확장 가능한 부분(요약)

이번 파일에서 제외했지만, 추후 아래 확장을 고려할 수 있다(상세 스펙은 별도 문서/버전에서 정의).

- `ftp.list/stat/delete/rename/mkdir/rmdir` : FTP 작업 디렉토리/조작 커맨드군
- `fs.find` (MiniScript intrinsic) : 대규모 파일 트리에서 패턴 탐색(현재는 `fs.list` 조합으로 대체)
- `net.traceroute/route` : 라우팅/중계 퍼즐 확장
- `ssh.whoami/session.tokens` : 계정/토큰 가시화(디버깅/퍼즐용)
- hostname/DNS: `hostOrIp`에 hostname 지원 및 해석 규칙 추가
- 고급 quoting/escaping: `ssh.exec` 커맨드 라인 파서 강화
- `job.jobStatus(jobId)` : `term.exec`/`ssh.exec` 비동기 job 상태 조회
- `job.jobRead(jobId, opts?)` : 비동기 job 출력/결과 조회
- `job.jobCancel(jobId)` : 비동기 job 취소
- `term.exec`/`ssh.exec`의 `jobId`는 공용 `job` 네임스페이스로 관리

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
