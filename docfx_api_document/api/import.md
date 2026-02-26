<a id="import"></a>
# import - Manual

`import`는 MiniScript 전역 intrinsic 함수이며, 라이브러리 스크립트를 로드해 모듈 값을 반환하고 호출자 스코프에 바인딩합니다.  
`ssh/fs/net`처럼 모듈 map을 호출하는 방식이 아니라, 함수 호출 자체가 로더 역할을 수행합니다.

## 들어가며

### 핵심 개념

- `import`는 전역 함수입니다. (`import(name, alias=null)`)
- 반환값은 로드된 모듈 값입니다.
  - 모듈에 `return`이 있으면 그 값을 반환합니다.
  - `return`이 없으면 모듈 실행 스코프(`locals`)를 반환합니다.
- 실패 시 ResultMap을 반환하지 않고 **runtime error**를 발생시킵니다.

### 시그니처/반환

```miniscript
m = import(name, alias=null)
```

- `name (string)`: 로드할 라이브러리 이름/경로
- `alias (string|null)`: 바인딩 이름(생략 가능)

반환:

```miniscript
# 성공 시: 모듈 값 (map 또는 모듈 return 값)
m = import("mathUtil")
```

### 바인딩 규칙

- `import("x")`
  - 기본 바인딩명은 **해결된 파일명 stem**입니다.
  - 예: `mathUtil.ms` -> `mathUtil`
- `import("x", "a")`
  - `a`에만 바인딩합니다.
  - 기존 `a`가 있으면 덮어씁니다.

### Resolve 순서 (v1)

1. 현재 실행 중인 스크립트 파일 기준 상대경로 탐색
2. 표준 라이브러리 탐색
   - `res://scenario_content/resources/text/stdlib` 하위 `.ms` 재귀 스캔

동일 stem 충돌 시 `ERR_IMPORT_AMBIGUOUS`로 실패합니다.

### 라이브러리 계약

`import` 대상 파일은 아래 조건을 만족해야 합니다.

- 파일 첫 줄부터 시작하는 최상단 연속 `//` 주석 블록이 있어야 함
- 해당 블록 안에 `@name`이 반드시 존재해야 함

예:

```miniscript
// @name mathUtil
// @desc 수학 유틸
```

위 계약을 만족하지 않으면 `ERR_NOT_A_LIBRARY` runtime error가 발생합니다.

### 실패 표면

`import`는 실패 시 runtime error를 발생시키며, 메시지에 코드 토큰(`ERR_*`)이 포함됩니다.

주요 코드:

- `ERR_INVALID_ARGS`
- `ERR_NOT_FOUND`
- `ERR_NOT_A_LIBRARY`
- `ERR_IMPORT_CYCLE`
- `ERR_IMPORT_AMBIGUOUS`
- `ERR_INTERNAL_ERROR`

### 캐시/순환 감지

- 같은 모듈은 `serverId + ":" + canonicalPath` 키로 캐시됩니다.
- 최초 1회 실행 이후에는 캐시 값을 재사용합니다.
- 로딩 중 재진입 시 `ERR_IMPORT_CYCLE`로 즉시 실패합니다.

### 미지원 항목 (v1)

- `.scripts_registry` 경로 탐색
- CWD 기반 탐색
- `reload=true` / `importReload(...)`

## import (import)

See: [API Reference](import-api.md#import).

### 예제 1) 기본 import

```miniscript
m = import("mathUtil")
print "answer=" + str(m.answer)
print "global=" + str(mathUtil.answer)
```

### 예제 2) alias import

```miniscript
helper = import("mathUtil", "helper")
print helper.clamp(15, 0, 10)
```

### 예제 3) 실패 분기(호출 레벨)

`import` 실패는 runtime error로 중단되므로, 실행 단위를 분리해 실패를 분기 처리할 수 있습니다.

```miniscript
r = term.exec("miniscript /scripts/load_stdlib.ms")
if r.ok != 1 then
  term.error("import failed in child script")
  term.error(r.code + ": " + r.err)
  return
end if
```
