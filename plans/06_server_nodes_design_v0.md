# 서버 노드 설계 문서 (프로토타입 v0.2)

목적: **쉬움 / 중간 / 어려움(v0)** 3개 시나리오를 실행하기 위한 서버 노드 데이터를 정리한다.  
이 문서는 Blueprint(10) + Runtime(09) 정합성 기준으로 작성된 **시나리오 데이터 예시**다.

- 엔진: Godot(가상 월드, 로직 C#)
- 접속: **가상 SSH(22/tcp)** 로 통일
- 추적/탐지: **없음** (프로토타입 v0)
- 실세계 시스템 접근: **절대 없음** (전부 가상)

---

## 0) 공통 월드 규칙 (Global)

### 0.1 네트워크 그래프
- 월드는 서버 노드 그래프로 구성된다.
- 각 노드는 아래 2개를 함께 가진다.
  - `interfaces`: `{ netId, ip, initiallyExposed }` 목록
  - `lanNeighbors`: 직접 연결된 이웃의 **nodeId 목록**
- 플레이어 명령 `net.scan("lan")`은 내부 `lanNeighbors(nodeId)`를 기준으로 계산하지만, 최종 출력은 IP 리스트를 반환한다.
- `primaryIp` 규칙:
  - `interfaces` 중 `netId="internet"`이 있으면 해당 IP
  - 없으면 `None`

### 0.2 SSH(가상) 규칙
- 모든 노드는 필요 시 `22/tcp`에 `ssh` 서비스를 가진다.
- 최소 API/명령:
  - `ssh.login(ip, user, password)`
  - (선택) `ssh.exec(ip, user, password, cmd)`
- 권한:
  - `guest`: 제한
  - `root`: 전체

### 0.3 VFS(가상 파일시스템) 규칙
- POSIX 경로
- 권한 2단계
  - `guestReadable`
  - `rootOnly`

---

## 1) 로컬(플레이어) 워크스테이션 (참고)

**Node: `player_ws`**
- nodeId: `player_ws`
- serverName: `player@term`
- role: `terminal`
- interfaces:
  - `{ netId: "internet", ip: "10.0.0.2", initiallyExposed: true }`
- primaryIp: `10.0.0.2`
- lanNeighbors: `[]`
- entrypoints(표시용 IP):
  - easy: `10.0.0.10`
  - medium: `10.0.1.20`
  - hard: `10.0.2.10`

**Local FS**
- `/home/player/dictionary.txt` (`guestReadable`)
  - `orchid`
  - `blue_owl`
  - `vault42`
  - `moonlight`
  - `admin123`
  - `rootme`
  - `citrus`

---

## 2) 쉬움 서버 (Easy)

### 시나리오
- 목표: `easy_target`의 root 계정 탈취(SSH 로그인)
- 방법: 로컬 `dictionary.txt` 단어를 순회하며 root 비번 대입

### 노드 설정

**Node: `easy_target`**
- nodeId: `easy_target`
- serverName: `easy-ssh-box`
- role: `targetEasy`
- interfaces:
  - `{ netId: "internet", ip: "10.0.0.10", initiallyExposed: true }`
- primaryIp: `10.0.0.10`
- lanNeighbors: `[]`

**Ports**
- `22/tcp`
  - service: `ssh`
  - open: `true`
  - banner: `OpenSSH_8.2p1 easy-box`

**Accounts**
- `root`
  - passwordMode: `staticSecret`
  - password: `moonlight` *(DEV SECRET, dictionary.txt에 반드시 포함)*
- `guest`: 없음(또는 비활성)

**Initial FS**
- `/root/flag.txt` (`rootOnly`): `EASY_FLAG{root_access_confirmed}`

---

## 3) 중간 서버 클러스터 (Medium)

> 클러스터 구성: `medium_target` + `otp_server` (+ 선택 decoy)

### 시나리오(OTP)
- 목표: `medium_target` root 탈취
- 흐름:
  1) `medium_target`에 `guest/guest` 접속
  2) `net.scan("lan")`으로 OTP 서버 후보 탐지
  3) `otp_server` 접속
  4) `passwd_gen` 실행 → 32자 base64 토큰 발급(TTL 3초)
  5) 토큰으로 `medium_target` root 즉시 로그인

### 3.1 Node: `medium_target`
- nodeId: `medium_target`
- serverName: `mid-edge-box`
- role: `targetMedium`
- interfaces:
  - `{ netId: "internet", ip: "10.0.1.20", initiallyExposed: true }`
  - `{ netId: "medium_subnet", ip: "10.0.11.20", initiallyExposed: false }`
- primaryIp: `10.0.1.20`
- lanNeighbors:
  - `otp_server`
  - `printer01`
  - `nas01`

**표시용 IP 참고**
- otp_server: `10.0.11.5`
- printer01: `10.0.11.21`
- nas01: `10.0.11.22`

**Ports**
- `22/tcp` ssh open: `true`
- banner: `OpenSSH_8.4p1 mid-edge`

**Accounts**
- `guest`
  - passwordMode: `staticPublic`
  - password: `guest`
- `root`
  - passwordMode: `dynamicFromOtp`
  - otpSourceNodeId: `otp_server`
  - tokenTtlSeconds: `3`
  - useWindowMs: `1000`
  - acceptedTokenFormat: `base64_32chars`

**Initial FS**
- `/home/guest/README.txt` (`guestReadable`)
  - `Ops note: internal services live on the 10.0.11.* segment.`
- `/root/flag.txt` (`rootOnly`): `MID_FLAG{otp_root_login}`

### 3.2 Node: `otp_server`
- nodeId: `otp_server`
- serverName: `otp-maint-node`
- role: `otpGenerator`
- interfaces:
  - `{ netId: "medium_subnet", ip: "10.0.11.5", initiallyExposed: true }`
- primaryIp: `None`
- lanNeighbors:
  - `medium_target`

**Ports**
- `22/tcp`
  - service: `ssh`
  - open: `true`
  - banner: `OpenSSH_7.9p1 otp-maint`
  - authMode: `noAuthRootShell`

**Accounts**
- `root`
  - passwordMode: `none`

**Program: `passwd_gen`**
- path: `/usr/local/bin/passwd_gen`
- tokenFormat: `base64_32chars`
- ttlSeconds: `3`
- output:
  - `OTP: <TOKEN>`
  - `EXPIRES_IN_MS: 3000`
- sideEffect:
  - 월드 레지스트리에 `(targetNodeId=medium_target, token, issuedAt, expiresAt)` 저장

### 3.3 선택 Decoy

**Node: `printer01`**
- nodeId: `printer01`
- role: `decoy`
- interfaces:
  - `{ netId: "medium_subnet", ip: "10.0.11.21", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors:
  - `medium_target`
- banner: `HP-PRINTER`

**Node: `nas01`**
- nodeId: `nas01`
- role: `decoy`
- interfaces:
  - `{ netId: "medium_subnet", ip: "10.0.11.22", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors:
  - `medium_target`
- banner: `NAS-storage`

---

## 4) 어려움 서버 클러스터 (Hard v0)

> 클러스터 구성: `jump01` + `term_a~term_f` + `mainframe`

### 시나리오 요약
- entry IP(표시용): `10.0.2.10`
- 목표 파일명: `OMEGA_CORE.bin`
- 핵심: 그래프 탐색 + 다중 노드 root 획득 + keyfrag 3개 + unlock 토큰

### 4.1 Topology 요약

**표시용 IP 연결 요약**
- jump01(`10.0.2.10`) ↔ term_a(`10.0.20.11`), term_b(`10.0.20.12`)
- term_a ↔ term_c(`10.0.20.13`), term_d(`10.0.20.14`)
- term_b ↔ term_e(`10.0.20.15`)
- term_c ↔ term_f(`10.0.20.16`)
- term_d/e/f ↔ mainframe(`10.0.20.99`)

**실제 참조 키(nodeId) 연결 요약**
- jump01 ↔ term_a, term_b
- term_a ↔ term_c, term_d
- term_b ↔ term_e
- term_c ↔ term_f
- term_d ↔ mainframe
- term_e ↔ mainframe
- term_f ↔ mainframe

### 4.2 Node: `jump01`
- nodeId: `jump01`
- serverName: `jump-host`
- role: `gateway`
- interfaces:
  - `{ netId: "internet", ip: "10.0.2.10", initiallyExposed: true }`
  - `{ netId: "hard_subnet", ip: "10.0.20.10", initiallyExposed: true }`
- primaryIp: `10.0.2.10`
- lanNeighbors:
  - `term_a`
  - `term_b`

### 4.3 Terminal nodes (term_a~term_f)

#### term_a
- nodeId: `term_a`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.11", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors: `jump01`, `term_c`, `term_d`
- rootPasswordSecret: `citrus`

#### term_b
- nodeId: `term_b`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.12", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors: `jump01`, `term_e`
- rootPasswordSecret: `blue_owl`

#### term_c
- nodeId: `term_c`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.13", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors: `term_a`, `term_f`
- rootPasswordSecret: `vault42`

#### term_d
- nodeId: `term_d`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.14", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors: `term_a`, `mainframe`
- rootPasswordSecret: `orchid`
- `/root/keyfrag.txt` (`rootOnly`): `FRAG_A: QkFTRTY0Rk9PQkFS`

#### term_e
- nodeId: `term_e`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.15", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors: `term_b`, `mainframe`
- rootPasswordSecret: `moonlight`
- `/root/keyfrag.txt` (`rootOnly`): `FRAG_B: TUVNT1JZUEFSVA`

#### term_f
- nodeId: `term_f`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.16", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors: `term_c`, `mainframe`
- rootPasswordSecret: `rootme`
- `/root/keyfrag.txt` (`rootOnly`): `FRAG_C: U0VDUkVUX0ZSVUdfQw`

### 4.4 Node: `mainframe`
- nodeId: `mainframe`
- serverName: `mainframe-omega`
- role: `mainframe`
- interfaces:
  - `{ netId: "hard_subnet", ip: "10.0.20.99", initiallyExposed: false }`
- primaryIp: `None`
- lanNeighbors:
  - `term_d`
  - `term_e`
  - `term_f`

**Accounts**
- `guest`: password `guest`
- `root`
  - passwordMode: `dynamicUnlockToken`
  - tokenTtlSeconds: `5`
  - useWindowMs: `1500`
  - acceptedTokenFormat: `base64_32chars`

**Program: `unlock_root`**
- path: `/usr/local/bin/unlock_root`
- requiredInputs: `FRAG_A`, `FRAG_B`, `FRAG_C`
- output:
  - `ROOT_TOKEN: <TOKEN>`
  - `EXPIRES_IN_MS: 5000`
- sideEffect:
  - 월드 레지스트리에 `(targetNodeId=mainframe, token, issuedAt, expiresAt)` 저장

---

## 5) 플레이어 시작 정보(표시용)

- Easy: target IP `10.0.0.10`, 로컬 `/home/player/dictionary.txt`
- Medium: target IP `10.0.1.20`, guest creds `guest/guest`
- Hard: entry IP `10.0.2.10`, guest creds `guest/guest`, 목표 파일명 `OMEGA_CORE.bin`

---

## 6) Codex 구현 체크리스트(이 문서 기준)

- [ ] 모든 노드에 `nodeId`, `interfaces`, `primaryIp`, `lanNeighbors(nodeId)`를 정의
- [ ] `net.scan("lan")`은 내부 nodeId 그래프를 쓰되 결과는 IP 리스트로 반환
- [ ] `ssh.login(ip, ...)` 입력은 `ipIndex`로 `nodeId` 역참조
- [ ] OTP/unlock 레지스트리는 `targetNodeId` 기준으로 저장/조회
- [ ] VFS 권한(guest/root) + `fs.find` 구현
- [ ] 터미널에서 `miniscript <script>`로 MiniScript 실행 가능 (`miniscript`는 시스템콜이 아니라 실행 파일 이름)
- [ ] 시스템콜 미일치 시 `cwd`/`PATH(/opt/bin)` 순서로 프로그램 탐색 실행
- [ ] 상대경로 실행(`../prog`, `./dir/prog`) 동작 확인
- [ ] 프로젝트 `DEBUG` 옵션 ON 시에만 `DEBUG_miniscript <script>` 시스템콜 활성화(개발/검증 전용)
