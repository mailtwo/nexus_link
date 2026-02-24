# 공식 제공 프로그램 (ExecutableHardcode) v0.2

이 문서는 게임이 **공식 제공**하는 실행 파일(프로그램)과, 그 중 일부가 제공하는 “공식 툴/힌트 계약(Contract)”을 정의합니다.

- 프로그램은 VFS 상의 실행 파일이며, `ExecutableHardcode(exec:<id>)` 또는 `ExecutableScript(MiniScript)` 형태로 제공됩니다.
- 본 문서의 **Inspect Password Hint Contract(InspectProbe)** 는 `inspect` 프로그램과 `ssh.inspect(...)` intrinsic이 **공유**하는 동작의 source of truth입니다.
  - `03_game_api_modules.md`(API 문서)는 `ssh.inspect`의 **시그니처/리턴 형식/추가 사전조건(툴 존재)** 만 정의하고, 그 외 의미는 본 문서를 참조합니다.

---

## 0) 프로그램 실행/해석 규칙(요약)

### 0.1 실행 파일 종류
- `ExecutableHardcode`
  - 파일 내용: `exec:<executableId>`
  - 실행 시 dispatcher가 `<executableId>`에 매핑된 C# 하드코드 동작을 수행합니다.
- `ExecutableScript`
  - 파일 내용: MiniScript 소스 텍스트
  - 파일명을 직접 실행하면 내장 MiniScript 인터프리터로 실행합니다.

### 0.2 명령 해석(시스템콜 + 프로그램 fallback)
터미널 입력은 아래 순서로 해석됩니다.
1) 시스템콜 registry 조회  
2) 미일치 시 프로그램 탐색(PATH 고정)  
3) 최종 미해결 시 `unknown command`

프로그램 탐색 규칙(요약):
- `PATH = ["/opt/bin"]`
- command에 `/`가 포함되면 `Normalize(cwd, command)`만 시도
- `/`가 없으면:
  1) `Normalize(cwd, command)`
  2) `/opt/bin/<command>`
- 실행은 `read + execute` 권한이 모두 필요합니다.

### 0.3 `ExecutableHardcode` 디스패처 오류 처리
- `exec:<executableId>` 형식이 아니거나, `<executableId>`가 등록되지 않은 경우 실행 실패로 처리한다.
- 터미널 사용자 메시지는 `unknown command`로 통일한다.
- 디버그 빌드에서만 내부 원인(파싱 실패/미등록 id)을 로그로 남길 수 있다.

---

## 1) Inspect Password Hint Contract (InspectProbe) v0.2

### 1.1 목적/적용 범위
InspectProbe는 대상 SSH 서비스/계정에 대해 “비밀번호/인증 타입 힌트”를 산출하는 공통 계약입니다.

- 적용 대상:
  - 공식 프로그램 `/opt/bin/inspect`
  - intrinsic `ssh.inspect(hostOrIp, userId, port=22, opts?)`
- 주의(필수):
  - InspectProbe는 **실제 로그인/세션 생성**을 하지 않습니다(MUST).
  - `ssh.connect`를 호출하지 않으며, `SSH_LOGIN` UI 트리거 및 `privilegeAcquire` 이벤트 enqueue를 유발하지 않습니다(MUST).

### 1.2 입력
- `hostOrIp: string`
- `userId: string` (필수)
- `port: int` (기본 22)
- `opts?: map` (v0.2 예약)

### 1.3 처리 순서(우선순위)
아래 순서는 **의미/밸런스**와 **오류 은닉**을 위해 고정합니다(MUST).

1) 인자 검증 → 실패 시 `ERR_INVALID_ARGS`
2) (intrinsic 전용 preflight) `inspect` 실행 파일 존재/권한 확인
   - `ssh.inspect`는 현재 실행 컨텍스트에서 `inspect` 실행 파일을 **터미널과 동일한 resolve 규칙**으로 찾을 수 있어야 합니다(MUST).
   - 미존재/종류 불일치 시 `ERR_TOOL_MISSING`
   - 존재하나 실행 권한 부족 시 `ERR_PERMISSION_DENIED`
3) `hostOrIp` → `nodeId` 역참조 실패 시 `ERR_NOT_FOUND`
4) 포트 판정 실패 시 `ERR_PORT_CLOSED`
   - `ports[port]`가 없거나 `portType==none`
   - `ports[port].portType != ssh`
5) 네트워크 접근(exposure) 판정 실패 시 `ERR_NET_DENIED`
6) 계정 probe 실패 시 `ERR_AUTH_FAILED`
   - 대상 노드에 `userId`가 존재하지 않는 경우도 **항상** `ERR_AUTH_FAILED`(계정 열거 방지)
7) 위 조건을 모두 통과하면 `OK`와 함께 `InspectResult` 산출

> 참고: 2번 단계는 프로그램 `inspect`에는 해당되지 않으며, `ssh.inspect` intrinsic의 추가 사전조건입니다.

### 1.4 출력 스키마

#### `InspectResult`
- `hostOrIp: string`
- `port: int`
- `userId: string`
- `banner?: string` (있으면 문자열, 없으면 생략 또는 빈 문자열)
- `passwdInfo: PasswdInspectInfo`

#### `PasswdInspectInfo` (Union)
`passwdInfo.kind`로 분기합니다.

- 공통:
  - `kind: "none" | "policy" | "dictionary" | "otp"`

##### (A) `kind == "none"`
- `{ kind: "none" }`
- 의미: 해당 계정은 비밀번호 검사가 없음(`authMode=none`).

##### (B) `kind == "dictionary"`
- `{ kind: "dictionary" }`
- 의미: 비밀번호가 “사전(딕셔너리) 기반”이며, 힌트는 타입만 제공됩니다.
- 금지(MUST): 길이를 추론할 수 있는 모든 정보(`length`, `mask`, `alphabet`, `policyId` 등)를 포함하지 않습니다.

##### (C) `kind == "policy"`
고정 길이 + 문자 집합 기반 정책(`c<LEN>_base64`, `c<LEN>_numspecial`)을 표현합니다.

- `{ kind:"policy", policyId:string, length:int, alphabetId:string, alphabet:string, mask?: string|null }`
- `length`: **비밀번호 길이(확정값)** 입니다.
- `alphabetId`: `"base64"` 또는 `"numspecial"` (v0.2)
- `alphabet`:
  - brute-force 도구가 그대로 사용할 수 있는 “허용 문자 집합”을 나열한 문자열입니다.
  - `alphabet`은 **항상 제공**되어야 합니다(MUST).
  - `alphabet`의 나열 순서는 의미가 없습니다(자유 나열).

##### (D) `kind == "otp"`
- `{ kind:"otp", length:int, alphabetId:string, alphabet:string }`
- `length`: **OTP 토큰 길이(확정값)** 입니다.
- `alphabetId`: `"base64"` (v0.2)
- `alphabet`:
  - brute-force/검증 도구가 그대로 사용할 수 있는 “허용 문자 집합”을 나열한 문자열입니다.
  - `alphabet`은 **항상 제공**되어야 합니다(MUST).
  - `alphabet`의 나열 순서는 의미가 없습니다(자유 나열).
- TTL/윈도우 등 시간 파라미터는 InspectProbe 결과에 포함하지 않습니다(MUST).

### 1.5 Mask 표현 규칙(v0.2)
Mask는 비밀번호의 일부 위치를 “확정 문자”로 노출해 unknown part를 줄이는 용도입니다.

- `mask`는 `passwdInfo.kind == "policy"`에서만 허용합니다.
- `mask`는 선택 필드이며, 없으면 `null` 또는 생략입니다.
- `mask`가 존재하면:
  - `mask.length == length` 이어야 합니다(MUST).
  - 각 문자는 `?`(unknown) 또는 확정 문자(known)만 허용합니다(MUST).
    - 예: `??A?x`
- 적용 가능/금지 타입:
  - `policy`: 허용
  - `dictionary`: **금지(MUST)** (길이 누설 방지)
  - `otp`: **금지(MUST)** (v0.2에서는 OTP에 mask를 사용하지 않음)
  - `none`: 해당 없음
- 유효성:
  - `policy`에서 확정 문자는 해당 `alphabet`(또는 `alphabetId`가 가리키는 문자 집합)에 포함되어야 합니다(SHOULD).

### 1.6 정책 식별자(policyId) 및 기본 alphabet(v0.2)
InspectProbe는 아래 정책을 최소 지원합니다.

- `policyId` 문법:
  - `c<LEN>_base64`
  - `c<LEN>_numspecial`
  - 여기서 `<LEN>`은 1 이상의 정수

- 기본 alphabet:
  - `base64` (64 chars):
    - `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/`
  - `numspecial` (base20 / 20 chars):
    - `0123456789!@#$%^&*()`

---

## 2) 실패/에러 처리(코드/로그) v0.2

### 2.1 공통 에러 코드(InspectProbe 관련)
- `ERR_INVALID_ARGS`: 인자 타입/개수/port 범위 등 입력 오류
- `ERR_TOOL_MISSING`: (`ssh.inspect` 전용) `inspect` 실행 파일이 resolve되지 않거나 종류가 다름
- `ERR_PERMISSION_DENIED`: (`ssh.inspect` 전용) `inspect`는 있으나 실행 권한 부족(`read+execute` 필요)
- `ERR_NOT_FOUND`: `hostOrIp`에 대응하는 노드가 없음
- `ERR_PORT_CLOSED`: 포트가 닫힘 또는 SSH가 아님
- `ERR_NET_DENIED`: exposure 규칙으로 접근 불가
- `ERR_AUTH_FAILED`: 계정이 없거나, probe 실패(계정 열거 방지 목적)
- `ERR_RATE_LIMITED`: shared API limit(0.11; 100k 버킷) 초과로 실행 거부  
  - `inspect` 프로그램과 `ssh.inspect` intrinsic 모두 동일하게 적용됩니다(MUST).

### 2.2 실패 로그(프로그램 `inspect`)
`inspect` 프로그램은 실패 시 stderr에 아래 형식으로 출력하는 것을 권장합니다.

- 1행: `error: <humanMessage>`
- 2행(선택): `code: <ERR_...>`

권장 humanMessage:
- `ERR_PORT_CLOSED` → `port is closed`
- `ERR_NET_DENIED` → `network access denied`
- `ERR_AUTH_FAILED` → `authentication failed`
- `ERR_NOT_FOUND` → `host not found`
- `ERR_INVALID_ARGS` → `invalid args`
- `ERR_RATE_LIMITED` → `rate limited`

프로그램 종료 코드는 다음을 권장합니다.
- 성공: `0`
- 실패: `1` (세부 구분은 v0.3에서 확장 가능)

### 2.3 실패 반환( `ssh.inspect` )
`ssh.inspect`는 ResultMap으로 실패를 반환합니다.
- `ok=0`, `code="ERR_*"`, `err=<string>`, `data=null`
- ResultMap의 세부 필드 규칙은 API 공통 규약을 따릅니다.

---

## 3) 부작용/비용·탐지/레이트리밋 v0.2

### 3.1 부작용(필수)
InspectProbe는 다음을 보장합니다.
- SSH 세션을 생성하지 않습니다(MUST).
- `ssh.connect` 및 `connect` 시스템콜을 호출하지 않습니다(MUST).
- `SSH_LOGIN` UI를 트리거하지 않습니다(MUST).
- `privilegeAcquire` 이벤트를 enqueue하지 않습니다(MUST).
- 서버의 `connectionRateLimiter` 데몬은 InspectProbe에 적용하지 않습니다(MUST).
  - 따라서 `connectionRateLimiter`의 카운터/누적치도 증가시키지 않습니다(MUST).

### 3.2 비용(cost)
- `cost`는 v0.2에서 항상 0으로 간주합니다.
  - `{ cpu:0, time:0, ram:0 }`

### 3.3 레이트리밋(shared API limit)
InspectProbe는 “API 요청”으로 취급되며, shared API limit(0.11; 100k 버킷)을 소비합니다(MUST).

- 적용 범위(필수):
  - `inspect` 프로그램 실행
  - `ssh.inspect` intrinsic 호출
- 우회 방지(필수):
  - `term.exec("/opt/bin/inspect ...")`처럼 `term` intrinsic을 통해 실행하더라도, **InspectProbe 자체가 shared limit을 소비**하므로 우회가 불가능해야 합니다(MUST).
  - `term` intrinsic이 shared limit에서 제외되는 것과 무관하게, InspectProbe는 별도로 카운팅됩니다(MUST).
- 제한 초과 시:
  - `inspect`는 `ERR_RATE_LIMITED`를 출력하고 실패합니다.
  - `ssh.inspect`는 `ERR_RATE_LIMITED`를 반환하고 실패합니다.

### 3.4 탐지(trace) 및 로그 카테고리
- InspectProbe(`inspect` 및 `ssh.inspect` 모두)는 공격/정찰로 취급되어 trace에 기록됩니다(MUST).
- 로그 카테고리:
  - 이벤트 카테고리는 `ssh.connect`의 “접속 시도”와 동일 계열로 기록합니다(MUST).
  - 단, `INSPECT_PROBE` 플래그로 구분해야 합니다(MUST).
  - 이 기록은 3.1에 따라 `connectionRateLimiter` 카운터/누적치를 증가시키지 않습니다(MUST).
- 권장 trace(v0.2):
  - `noise: 1`
  - `flags: ["INSPECT_PROBE", "PORT_SCAN", "AUTH_PROBE"]`
- 네트워크 접근 전 실패(`ERR_INVALID_ARGS`, `ERR_TOOL_MISSING`, `ERR_PERMISSION_DENIED`)는 trace를 남기지 않는 것을 권장합니다(SHOULD).

---

## 4) 공식 프로그램 목록

### 4.1 `inspect` (SSH 비밀번호/인증 힌트 조회기)
- 권장 위치: `/opt/bin/inspect`
- fileKind: `ExecutableHardcode`
- 내용: `exec:inspect`

#### 사용법
```text
inspect [(-p|--port) <port>] <host|ip> <userId>
```

#### 출력(권장)
성공 시, key-value 형태 출력(예시):
```text
ssh: open
host: 10.0.1.20
port: 22
user: root
passwd.kind: policy
passwd.policyId: c10_numspecial
passwd.length: 10
passwd.alphabetId: numspecial
passwd.alphabet: 0123456789!@#$%^&*()
passwd.mask: ??A?x
```

- `passwd.*` 필드는 **InspectProbe 출력 스키마(1.4)** 와 동일 의미를 가집니다.
- `dictionary` 타입은 `passwd.kind: dictionary`만 출력해야 합니다(MUST).
- `none` 타입은 `passwd.kind: none`만 출력합니다.

실패 시, `2.2 실패 로그` 규칙을 따릅니다.

### 4.2 `miniscript` (MiniScript 실행기)
`miniscript`는 **시스템콜이 아니라 VFS 실행 파일**입니다(권장 위치: `/opt/bin/miniscript`).

```text
miniscript <scriptPath> [args...]
```

- `<scriptPath>`는 `fileKind == Text` 파일이면 확장자와 무관하게 실행을 시도할 수 있습니다.
- 인자 전달:
  - `argv`: `<scriptPath>` 뒤의 인자 목록
  - `argc`: `argv` 길이
- 스크립트 문법/런타임 조건을 만족하지 않으면 “인터프리터 오류”로 실패합니다.

---

## 5) 추후 추가 예정

### 5.1 `password_breaker`
- SSH 비밀번호를 대상으로 한 “가상 brute-force” 도구
- v0.2에서는 **언급만** 하고, 상세 동작/입력/출력/비용 모델은 별도 버전에서 정의합니다.
