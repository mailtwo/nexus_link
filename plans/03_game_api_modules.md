# 샌드박스 API 모듈 설계(초안)

이 문서는 “가상 OS/가상 네트워크가 이미 구현되어 있다”는 가정 위에서,  
유저 MiniScript 프로그램이 접근할 **샌드박스 API 표면**을 정리한다.

핵심 원칙:
- 유저는 **실제 시스템에 절대 접근하지 못한다**. 모든 API는 “가상 월드”만 조작.
- API는 “실제 해킹 페이로드”가 아니라, **전략·추론·리소스 관리**에 집중하도록 추상화.
- 모든 API 호출은 **권한 검사 + 비용(시간/CPU/RAM/Trace) + 로그 이벤트**를 발생시킨다.

---

## 0) 공통 규약(모든 모듈 공통)

### 0.1 리턴 타입(권장)
- 모든 호출은 아래 형태의 맵을 반환:
```miniscript
{ ok: 1, data: ..., err: null, cost: {...}, trace: {...} }
{ ok: 0, data: null, err: "message", cost: {...}, trace: {...} }
```

### 0.2 비용/탐지 모델(권장)
- `cost.cpu` : 소비한 “연산 포인트” 또는 “시간 포인트”
- `cost.time` : 가상 시간(초)
- `cost.ram` : 일시적 버퍼 사용(예약 RAM 외 추가를 허용/금지 선택)
- `trace.noise` : 소음(로그/IDS에 남는 정도)
- `trace.flags` : 탐지 규칙 트리거(예: "BRUTE_FORCE", "PORT_SCAN")

### 0.3 권한 모델(권장)
- 세션/토큰에는 역할(role)과 capability가 있다.
- API는 capability 기반으로 허용/거부.
- 예:
  - `CAP_NET_SCAN`, `CAP_HTTP_REQUEST`, `CAP_FS_READ`, `CAP_ADMIN_ROUTER`, `CAP_PKG_PUBLISH`

---

## 1) 기본 시스템 모듈

### 1.1 term (터미널 출력/입력)
- `term.print(text)`
- `term.warn(text)` / `term.error(text)`
- `term.prompt(label)` → (선택) 유저 입력

### 1.2 time (시간)
- `time.now()` → 가상 시간
- `time.sleep(seconds)` → `wait` 래핑(혹은 엔진에 위임)

### 1.3 rand (랜덤/시드)
- `rand.seed(x)`
- `rand.int(a,b)`

---

## 2) OS/파일/프로세스 모듈

### 2.1 fs (가상 파일 시스템)
- `fs.list(host, path)`
- `fs.read(host, path, opts)` (opts: maxBytes 등)
- `fs.write(host, path, data, opts)`
- `fs.delete(host, path)`
- `fs.stat(host, path)` (size, owner, perms, mtime)
- `fs.find(host, pattern, opts)` (로그/백업/설정 탐색에 사용)

**설계 메모**
- “민감정보 평문 저장”은 fs에 텍스트/백업/로그로 배치.
- RAM/성능 밸런스를 위해 `read`/`find`에 최대 크기 상한을 둔다.

### 2.2 proc (가상 프로세스/작업)
- `proc.run(host, programId, args)` → job handle
- `proc.ps(host)` → 실행 중 작업 목록
- `proc.kill(host, pid)`

**설계 메모**
- “프로그램(툴)” 자체도 가상 프로세스로 실행되며 RAM 예약을 점유.
- 네트워크 크랙/다운로드/패치 적용 같은 시간 작업은 job 형태로 표현하면 좋다.

---

## 3) 네트워크/장비 모듈

### 3.1 net (스캔/연결/정보수집)
- `net.scan(subnetOrHost)` → host IP list
- `net.ports(host)` → 열린 포트/서비스 목록(권한/탐지에 따라 가시성 차등)
- `net.banner(host, port)` → 버전/배너(구형 컴포넌트 단서)
- `net.route()` / `net.traceroute(host)` (선택)

`net.scan("lan")` 구현 규칙(v0.2):
- 내부 계산: `lanNeighbors: List<nodeId>` 기반
- 외부 반환: 플레이어 UX 유지를 위해 IP 문자열 리스트 반환
- 변환: nodeId -> (현재 netId 컨텍스트의 ip)

터미널 명령 매핑(v0.2):
- `known`
  - 의미: 현재 플레이어가 획득한 known 노드 중 `netId="internet"`에 해당하는 public IP만 출력
  - 출력: `hostname` + `IP` 2열 테이블(한 줄에 1호스트)
- `scan`
  - 의미: 현재 접속 서버의 `lanNeighbors`를 IP 목록으로 출력
  - 권한: 현재 접속 계정의 `execute=true` 필요
  - 출력:
    - 첫 줄: `현재IP - 연결IP1`
    - 다음 줄들: `연결IPn`만 표시하되 첫 줄의 `-` 뒤 컬럼에 정렬
  - 권한 부족 시: 리눅스 스타일에 가깝게 `scan: permission denied` 에러 출력
  - 예외: 현재 서버가 player workstation이거나 subnet 미연결(또는 이웃 없음)이면
    "인접 서버를 찾을 수 없음" 안내 문장 1줄 출력

### 3.2 fw (방화벽/ACL)
- `fw.list(device)`
- `fw.addRule(device, rule)` / `fw.removeRule(device, ruleId)` (관리 권한 필요)
- `fw.test(device, src, dst, port)` (디버깅용)

### 3.3 router / nat (라우터 관리면/포워딩)
- `router.info(device)`
- `router.nat.list(device)`
- `router.nat.add(device, mapping)` (관리 권한)
- `router.dns.poison(device, entry)` (선택: “취약점 토글”로만)

**설계 메모**
- “보안 설정 오류/관리면 노출” 루트는 이 모듈과 web 모듈이 같이 필요.

---

## 4) 인증/크리덴셜/암호 모듈

### 4.1 ssh (로그인/세션)
- `ssh.connect(hostOrIp, user, password, port=22)` → `SessionHandler`
- `session.disconnect()` (현재 세션 연결 종료)
- `session.whoami()` (선택)
- `session.tokens()` (선택: 토큰 스코프 확인)

식별자 경계 규칙(v0.2):
- `ssh.connect(..., user, ...)`의 `user` 인자는 항상 **userId(플레이어 노출 식별자)** 로 해석한다.
- `userKey`는 엔진 내부 참조 전용 키이며, 플레이어 입력/시스템콜 요청/공개 API 응답에 노출하지 않는다.
- 내부 런타임은 `userId -> userKey` 매핑 후 권한/세션 로직을 수행한다.

로그인 host 해석 규칙(v0.2):
- `ssh.connect(hostOrIp, ...)`의 `hostOrIp`는 host 문자열 또는 IP를 받는다.
- 내부에서는 `ipIndex`로 `nodeId`를 역참조해 실제 서버 런타임을 조회한다.

통합 규칙:
- 서버 로그인 진입점은 `ssh.connect(...)`로 통일한다.
- 기존 로그인 진입 API 경로는 사용하지 않는다.

**공격 루트 연결**
- 약한 비번/기본 비번/credential stuffing/스프레이

### 4.2 crypto (해시/검증/크랙: ‘추상화’ 중심)
- `crypto.identify(hashStr)` → {type, salted?}
- `crypto.verify(hashRec, guess)` → 1/0
- `cracker.run(hashDump, wordlistId, opts)` → job handle (시간/CPU 비용 큼)

**설계 메모**
- 실제 해시 크래킹 알고리즘을 구현하기보다, “해시 유형/솔트/작업량”을 퍼즐/자원관리로 표현.

---

## 5) 웹/DB/앱 계층 모듈

### 5.1 http (가상 HTTP 클라이언트)
- `http.get(url, opts)`
- `http.post(url, data, opts)`
- `http.upload(url, fileRef, opts)`
- `http.cookies.get(domain)` / `http.cookies.set(...)` (선택)

### 5.2 web (웹앱 추상 진단/공격 지원)
(실제 페이로드 입력 대신 “의도 토큰” 방식 권장)
- `web.probe(url, kind)`  
  - kind 예: `"idor"`, `"sqli"`, `"xss"`, `"upload"`, `"ssrf"`
- `web.exploit(url, kind, params)`  
  - 성공/실패는 서버의 취약점 토글과 유저가 수집한 단서(권한/토큰/버전)에 의해 결정

### 5.3 db (가상 DB)
- `db.query(conn, queryToken, params)`  
- `db.dump(conn, table, opts)` (권한/취약점에 따라 허용)

**설계 메모**
- SQLi 같은 건 “쿼리 문자열을 직접 입력”시키기보다
  - `web.exploit(..., "sqli", {goal:"auth_bypass"})` 같이 추상화하면 안전하고 UX도 좋아짐.

---

## 6) 메시징/사회공학 모듈

### 6.1 mail (사내 메일/메신저)
- `mail.inbox(user)` → message list
- `mail.open(messageId)`
- `mail.send(from, to, subject, body, opts)`  
  - opts: `spoofAs`(취약점 토글이 켜진 서버에서만 가능), attachments

**설계 메모**
- “현실 기관 사칭” 대신 게임 세계관 내부 조직/도메인을 사용.
- 성공 조건은 SPF/DKIM 같은 실전 디테일 대신 “서버 설정 토글 + 신뢰 체인”으로 처리.

---

## 7) 패키지/업데이트/무결성(공급망) 모듈

### 7.1 pkg (레지스트리/플러그인/업데이트)
- `pkg.search(registry, name)`
- `pkg.install(host, name, version)`
- `pkg.update(host, name)`
- `pkg.publish(registry, pkgMeta)` (권한 필요)
- `pkg.verify(host, name)` → {signed:1/0, keyId, status}

**설계 메모**
- “무결성 검증 실패”는
  - verify가 꺼져 있거나, 서명키가 유출됐거나, 레지스트리가 손상된 경우로 연출.

---

## 8) LLM/에이전트 모듈(근미래 루트)

### 8.1 agent (문서 읽기 + 툴 호출)
- `agent.run(agentId, task, refs)` → job
- `agent.feed(agentId, docRef)` (RAG/문서 기반)
- `agent.tools(agentId)` → 사용 가능한 툴 목록
- `agent.approve(agentId, actionId)` (선택: 승인 단계)

**공격 루트 연결**
- 간접 프롬프트 인젝션, insecure output handling, toolchain 취약점, LLM DoS

### 8.2 pipeline (자동 적용 파이프라인)
- `pipeline.preview(changeSet)` → diff
- `pipeline.apply(changeSet)` → (취약 시 자동 승인/자동 반영)

---

## 9) 로깅/모니터링(방어) 모듈

### 9.1 monitor (탐지/경보/트레이스)
- `monitor.trace()` → 현재 추적 게이지
- `monitor.alerts()` → 활성 경보
- `monitor.logs(host, filter)` → (권한 있을 때만) 로그 열람

**설계 메모**
- 플레이어는 “로그가 곧 적”이기도 하고, 때로는 침투 후 “로그 삭제/변조” 같은 미션으로도 사용 가능(다만 지나친 현실 재현은 피하고 추상화).

---

## 10) 콘텐츠 제작을 위한 “취약점 토글”(서버 데이터 스키마)

서버/서비스 객체에 아래처럼 취약점/방어를 데이터로 넣으면, 미션 제작이 빠르다.

- `vuln.defaultCreds = true`
- `vuln.passwordPolicy = weak`
- `vuln.hashStorage = {salted:0, slowHash:0}`
- `vuln.accessControl = "idor"`
- `vuln.webInjection = ["sqli","xss"]`
- `vuln.ssrf = true`
- `vuln.upload = "weakValidation"`
- `vuln.pkgVerify = false`
- `vuln.agentTrust = "broken"`
- `defense.logging = "strong"|"weak"`
- `defense.rateLimit = {enabled:1, threshold:...}`

---

## 참고 링크(원문)
```text
OWASP Top 10:2021 (Injection/AccessControl/Misconfig/Outdated/Logging/SSRF 등): https://owasp.org/Top10/2021/
OWASP Top 10 for LLM Apps (Prompt Injection/Output Handling/Model DoS/Supply Chain 등): https://owasp.org/www-project-top-10-for-large-language-model-applications/
```
