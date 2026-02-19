# 서버 노드 설계 문서 (프로토타입 v0)

목적: **쉬움 / 중간 / 어려움(v0)** 3개 시나리오를 돌리기 위해 필요한 “서버 노드 설정값”을 한 문서로 정리한다.  
이 문서만 보고도 Codex가 서버 노드(클러스터 포함)를 생성하고, 시나리오가 실행 가능해야 한다.

- 엔진: Godot(가상 월드, 로직 C#)
- 접속: **가상 SSH(22/tcp)** 로 통일
- 추적/탐지: **없음** (프로토타입 v0)
- 실세계 시스템 접근: **절대 없음** (전부 가상)

---

## 0) 공통 월드 규칙 (Global)

### 0.1 네트워크 그래프
- 월드는 **서버 노드 그래프**로 구성된다.
- 각 노드는 `lan_neighbors`(직접 연결된 서버 IP 리스트)를 갖는다.
- 프로토타입 스캔 규칙(단순화):
  - `net.scan("lan")` → 현재 접속 노드의 `lan_neighbors` 반환
  - `net.banner(ip, port)` → 해당 노드의 배너 문자열 반환(힌트/필터링용)

### 0.2 SSH(가상) 규칙
- 모든 노드는 필요 시 `22/tcp`에 `ssh` 서비스를 가진다.
- 최소 API/명령:
  - `ssh.login(ip, user, password)` → 성공 시 세션/쉘 진입
  - (선택) `ssh.exec(ip, user, password, cmd)` → 비대화형 실행(스크립트 자동화용)
- 권한:
  - `guest`: 제한(기본 명령/파일 읽기/스캔/스크립트 실행)
  - `root`: 전체(루트 전용 파일/프로그램 접근)
- 프로토타입에서는 **락아웃/레이트리밋/IDS 없음**.

### 0.3 VFS(가상 파일시스템) 규칙
- POSIX 경로.
- 권한은 v0에서 2단계로 단순화:
  - `guest_readable`
  - `root_only`
- 어려움(v0)에서 `fs.find(pattern)`가 중요하다.

### 0.4 “코딩 필수” 강제 장치
- 각 시나리오는 최소 1개 이상을 포함한다:
  - 긴 토큰(수동 입력 곤란)
  - 짧은 TTL(유효시간)
  - 스캔→필터→접속→파싱→로그인 체이닝
  - 다수 노드 반복 처리(그래프 탐색)

---

## 1) 로컬(플레이어) 워크스테이션 (참고)

쉬움 서버는 로컬에 있는 `dictionary.txt`를 읽어 공격하도록 설계되어 있으므로, 로컬 파일은 필수다.

**Node: `player_ws`**
- name: `player@term`
- ip: `10.0.0.2`
- entrypoints:
  - easy: `10.0.0.10`
  - medium: `10.0.1.20`
  - hard: `10.0.2.10`

**Local FS**
- `/home/player/dictionary.txt` (`guest_readable`)
  - 예시 단어(실제론 더 길게 생성 가능):
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
- 목표: 서버의 `root` 계정 탈취(SSH 로그인)
- 방법: 로컬 `dictionary.txt`의 단어를 순회하며 `root` 비번 대입

### 노드 설정

**Node: `easy_target`**
- server_name: `easy-ssh-box`
- ip: `10.0.0.10`
- role: `target_easy`
- lan_neighbors: `[]`

**Ports**
- `22/tcp`:
  - service: `ssh`
  - open: `true`
  - banner: `OpenSSH_8.2p1 easy-box`

**Accounts**
- `root`:
  - password_mode: `static_secret`
  - password: `moonlight` *(DEV SECRET, dictionary.txt에 반드시 포함)*
- `guest`: 없음(또는 비활성)

**(선택) Auth tuning**
- login_fail_delay_ms: `150`

**Initial FS**
- `/root/flag.txt` (`root_only`): `EASY_FLAG{root_access_confirmed}`

**Success**
- root 로그인 성공 + `/root/flag.txt` 읽기 성공

---

## 3) 중간 서버 클러스터 (Medium)

> 중요: **클러스터는 하나의 서버가 아니라 여러 노드**로 구성된다.  
> 구성: `medium_target` + `otp_server` (+ 선택 decoy)

### 시나리오(OTP)
- 목표: `medium_target`의 root 탈취
- 흐름:
  1) `medium_target`에 `guest/guest`로 접속
  2) `net.scan("lan")`으로 OTP 서버 후보 탐지(배너/호스트명)
  3) `otp_server`에 접속(인증 없이 root 쉘)
  4) `passwd_gen` 실행 → 32자 base64 토큰 발급(TTL 3초)
  5) 토큰을 `medium_target` root 비번으로 사용해 즉시 로그인
- 코딩 강제(권장 강화):
  - `use_window_ms = 1000` (발급 후 1초 내 사용 요구)

---

### 3.1 Node: `medium_target`
- server_name: `mid-edge-box`
- ip: `10.0.1.20`
- role: `target_medium`
- lan_neighbors:
  - `10.0.11.5` (otp_server)
  - `10.0.11.21` (printer01, decoy)
  - `10.0.11.22` (nas01, decoy)

**Ports**
- `22/tcp` ssh open: `true`
- banner: `OpenSSH_8.4p1 mid-edge`

**Accounts**
- `guest`:
  - password_mode: `static_public`
  - password: `guest` *(플레이어 시작 정보)*
- `root`:
  - password_mode: `dynamic_from_otp`
  - otp_source_ip: `10.0.11.5`
  - token_ttl_seconds: `3`
  - use_window_ms: `1000` *(선택/권장)*
  - accepted_token_format: `base64_32chars`

**Initial FS**
- `/home/guest/README.txt` (`guest_readable`)
  - `Ops note: internal services live on the 10.0.11.* segment.`
- `/root/flag.txt` (`root_only`): `MID_FLAG{otp_root_login}`

---

### 3.2 Node: `otp_server`
- server_name: `otp-maint-node`
- ip: `10.0.11.5`
- role: `support_otp`
- lan_neighbors: `10.0.1.20`

**Ports**
- `22/tcp`:
  - service: `ssh`
  - open: `true`
  - banner: `OpenSSH_7.9p1 otp-maint`
  - auth_mode: `no_auth_root_shell` *(접속 즉시 root 권한)*

**Accounts**
- `root`:
  - password_mode: `none` *(빈 비번/아무 비번 허용 중 택1)*

**Program: `passwd_gen`**
- path: `/usr/local/bin/passwd_gen`
- token_format: `base64_32chars` (padding 없음)
- ttl_seconds: `3`
- output:
  - `OTP: <TOKEN>`
  - `EXPIRES_IN_MS: 3000`
- side_effect (필수):
  - 월드 레지스트리에 `(target_ip=10.0.1.20, token, issued_at, expires_at)` 저장
  - medium_target의 root 로그인 검증은 이 레지스트리를 참조

**Initial FS**
- `/usr/local/bin/passwd_gen` (`root_only`)
- `/etc/motd` (`root_only`): `MAINTENANCE NODE - DO NOT EXPOSE`

---

### 3.3 (선택) Decoy nodes
스캔 재미용. 구현 부담이 있으면 제거 가능.

**Node: `printer01`**
- ip: `10.0.11.21`
- role: `decoy`
- banner: `HP-PRINTER`

**Node: `nas01`**
- ip: `10.0.11.22`
- role: `decoy`
- banner: `NAS-storage`

---

## 4) 어려움 서버 클러스터 (Hard v0)

> 중요: **클러스터는 하나의 서버가 아니라 여러 노드**로 구성된다.  
> 구성: `jump01` + `term_a~term_f` + `mainframe`

### 시나리오(Hard v0: LLM/DoS 없음)
- 시작 정보:
  - entry IP: `10.0.2.10`
  - 목표 파일명: `OMEGA_CORE.bin`
  - guest creds: `guest/guest`
- 목표:
  1) LAN 그래프 탐색(노드 반복)
  2) 각 노드 root 탈취(로컬 사전 파일 기반)
  3) 메인프레임 탐지: `fs.find("OMEGA_CORE.bin")` 성공하는 노드가 메인프레임
  4) 메인프레임 인접 3노드(term_d/e/f)에서 `keyfrag` 수집(root 필요)
  5) 메인프레임에서 `unlock_root` 실행 → TTL 토큰 발급
  6) 토큰으로 메인프레임 root 로그인 후 `/vault/OMEGA_CORE.bin` 획득

---

### 4.1 Topology 요약(그래프)
- player_ws → `jump01(10.0.2.10)`만 직접 접근 가능
- 내부 LAN(10.0.20.0/24)은 jump01 이후 탐색

연결 요약:
- jump01 ↔ term_a, term_b
- term_a ↔ term_c, term_d
- term_b ↔ term_e
- term_c ↔ term_f
- term_d/e/f ↔ mainframe

---

### 4.2 Node: `jump01`
- server_name: `jump-host`
- ip: `10.0.2.10`
- role: `entry_hard`
- lan_neighbors: `10.0.20.11`, `10.0.20.12`

**Ports**
- `22/tcp` ssh open: `true`
- banner: `OpenSSH_8.2p1 jump-host`

**Accounts**
- `guest`: password `guest` *(시작 정보)*

**Initial FS**
- `/home/guest/README.txt` (`guest_readable`)
  - `Network map is fragmented. Scan from each node.`
  - `Target file name: OMEGA_CORE.bin`

---

### 4.3 Terminal nodes(term_a~term_f) 공통
- guest: `guest/guest`
- root: 각 노드 `/home/guest/dictionary.txt`에서 찾게 설계(DEV SECRET)
- 공통 FS:
  - `/home/guest/dictionary.txt` (`guest_readable`) — 20~50단어 권장
  - `/root/keyfrag.txt` (`root_only`) — term_d/e/f에만 존재
- 공통 Ports:
  - `22/tcp` ssh open: `true`

#### term_a
- ip: `10.0.20.11`
- lan_neighbors: `10.0.2.10`, `10.0.20.13`, `10.0.20.14`
- root_password_secret: `citrus`

#### term_b
- ip: `10.0.20.12`
- lan_neighbors: `10.0.2.10`, `10.0.20.15`
- root_password_secret: `blue_owl`

#### term_c
- ip: `10.0.20.13`
- lan_neighbors: `10.0.20.11`, `10.0.20.16`
- root_password_secret: `vault42`

#### term_d (mainframe 인접 + keyfrag)
- ip: `10.0.20.14`
- lan_neighbors: `10.0.20.11`, `10.0.20.99`
- root_password_secret: `orchid`
- `/root/keyfrag.txt` (`root_only`):
  - `FRAG_A: QkFTRTY0Rk9PQkFS` *(예시)*

#### term_e (mainframe 인접 + keyfrag)
- ip: `10.0.20.15`
- lan_neighbors: `10.0.20.12`, `10.0.20.99`
- root_password_secret: `moonlight`
- `/root/keyfrag.txt`:
  - `FRAG_B: TUVNT1JZUEFSVA` *(예시)*

#### term_f (mainframe 인접 + keyfrag)
- ip: `10.0.20.16`
- lan_neighbors: `10.0.20.13`, `10.0.20.99`
- root_password_secret: `rootme`
- `/root/keyfrag.txt`:
  - `FRAG_C: U0VDUkVUX0ZSVUdfQw` *(예시)*

---

### 4.4 Node: `mainframe`
- server_name: `mainframe-omega`
- ip: `10.0.20.99`
- role: `target_hard_mainframe`
- lan_neighbors: `10.0.20.14`, `10.0.20.15`, `10.0.20.16`

**Ports**
- `22/tcp` ssh open: `true`
- banner: `OpenSSH_9.0p1 mainframe`

**Accounts**
- `guest`:
  - password: `guest`
- `root`:
  - password_mode: `dynamic_unlock_token`
  - token_ttl_seconds: `5`
  - use_window_ms: `1500` *(선택/권장)*
  - accepted_token_format: `base64_32chars`

**Program: `unlock_root`**
- path: `/usr/local/bin/unlock_root`
- required_inputs: `FRAG_A`, `FRAG_B`, `FRAG_C`
- output:
  - `ROOT_TOKEN: <TOKEN>`
  - `EXPIRES_IN_MS: 5000`
- side_effect (필수):
  - 월드 레지스트리에 `(mainframe_ip, token, issued_at, expires_at)` 저장
  - mainframe root 로그인 검증은 이 레지스트리를 참조

**Initial FS**
- `/vault/OMEGA_CORE.bin` (`root_only`): `OMEGA_CORE{prototype_main_objective}`
- (선택) `/root/flag.txt` (`root_only`): `HARD_FLAG{mainframe_root}`
- (선택) `/home/guest/NOTICE.txt` (`guest_readable`): `unlock_root requires 3 fragments.`

---

## 5) 플레이어에게 제공되는 시작 정보(필수)

- Easy: target IP `10.0.0.10`, 로컬 `/home/player/dictionary.txt`
- Medium: target IP `10.0.1.20`, guest creds `guest/guest`, 내부망 힌트(README)
- Hard v0: entry IP `10.0.2.10`, guest creds `guest/guest`, 목표 파일명 `OMEGA_CORE.bin`

---

## 6) Codex 구현 체크리스트(이 문서 기준)

- [ ] `net.scan("lan")` = `lan_neighbors`
- [ ] `net.banner(ip,port)` 구현
- [ ] `ssh.login`이 password_mode 처리:
  - [ ] `static_secret`
  - [ ] `dynamic_from_otp` (otp 레지스트리 참조)
  - [ ] `dynamic_unlock_token` (unlock 레지스트리 참조)
- [ ] `passwd_gen` 실행 시 medium 토큰 레지스트리 갱신
- [ ] `unlock_root` 실행 시 mainframe 토큰 레지스트리 갱신
- [ ] VFS 권한(guest/root) + `fs.find` 구현
- [ ] 터미널에서 `run <script>`로 MiniScript 실행 가능(자동화 필수)
