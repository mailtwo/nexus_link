# 멀티 윈도우 시스템 & SSH 로그인 창(MVP) — 엔진 계약서

- 문서 버전: v1.0-rc
- 대상 엔진: **Godot 4.6**
- 타겟 플랫폼: **PC / Windows**
- 문서 목적: Codex가 그대로 구현 가능한 수준의 **규칙/데이터/흐름**을 명문화합니다.
- 구현 범위:
  - ✅ 멀티 윈도우 시스템(네이티브 OS 윈도우 모드 + 가상 데스크톱 모드)
  - ✅ SSH 로그인 창(MVP)
  - ⛔ 기타 창(트레이싱/토폴로지/전송큐/웹뷰 등)은 “추후 목록”만 기록

---

## 0. RFC 용어

- **MUST**: 반드시 지켜야 함
- **SHOULD**: 가능한 지켜야 함(환경/플랫폼 제약으로 예외 가능)
- **MAY**: 선택 사항

---

## 1. 용어 정의

- **메인 창(Main Window)**: 게임의 주 UI. 본 게임에서는 **터미널 창**.
- **서브 윈도우(Sub Window)**: 메인 창과 별개로 띄워지는 모든 앱/패널/창.
- **윈도우 종류(WindowKind)**: 서브 윈도우의 타입(예: `SSH_LOGIN`). 각 Kind는 “단일 인스턴스/지오메트리/리사이즈 정책”의 단위.
- **지오메트리(Geometry)**: 위치(Position) + 크기(Size) + (선택) 최대화 상태.
- **네이티브 OS 윈도우 모드(NATIVE_OS)**: 서브 윈도우가 **Windows OS 윈도우**로 생성되는 모드(= OS 프레임 유지).
- **가상 데스크톱 모드(VIRTUAL_DESKTOP)**: 메인 창 내부에 **에뮬레이티드 배경(가상 데스크톱)** 을 만들고, 그 위에 서브 윈도우를 **임베디드(가상) 윈도우**로 띄우는 모드.

---

## 2. 최상위 목표 및 제약

### 2.1 전역 목표
- “영화처럼 여러 창이 동시에 살아있는 느낌”의 멀티 윈도우 UX를 제공한다.
- 단, MVP에서는 **SSH 로그인 창**을 1차 목표로 한다.

### 2.2 전역 제약 (MUST)

0) 엔진/플랫폼  
- Godot 4.6, Windows PC만 지원한다.

1) 모드 2종 동시 지원  
- 모든 서브 윈도우는 `NATIVE_OS` / `VIRTUAL_DESKTOP` 두 모드를 모두 지원한다.

2) 단일 인스턴스  
- WindowKind마다 **동시에 1개만** 존재한다.
- 동일 Kind에 대한 “열기”가 다시 요청되면: 기존 창을 전면/포커스 처리하고 내용을 갱신한다.

3) 지오메트리 기억  
- 각 WindowKind는 모드별로 지오메트리를 저장/복원한다.
- 저장은 게임 재실행 후에도 유지된다.

**NATIVE_OS 모드 저장 항목 (MUST):**
- 위치(Position), 크기(Size), 최대화 여부
- 해당 창이 위치한 **모니터 식별 정보** (모니터 인덱스 + 해상도 + DPI 배율의 조합)
  - 모니터 인덱스만으로는 모니터 연결 순서가 바뀔 수 있으므로, 해상도와 DPI 배율을 함께 저장하여 동일 모니터를 식별하는 데 사용한다.
- 저장 좌표는 해당 모니터의 **물리 픽셀(Physical Pixel) 기준 스크린 절대 좌표**로 한다.

**복원 절차 (MUST):**
1. 저장된 모니터 식별 정보와 일치하는 모니터가 현재 연결되어 있는지 확인한다.
2. **일치하는 모니터가 존재하는 경우:**
   - 해당 모니터의 현재 DPI 배율이 저장 시점과 다르면, 저장된 위치/크기에 `(현재 DPI 배율 / 저장 시 DPI 배율)` 비율을 곱하여 보정한다.
   - 보정된 좌표가 해당 모니터의 `screen_get_usable_rect` (작업표시줄 제외) 영역 내부에 있는지 검증한다.
   - 영역을 벗어나면 해당 모니터의 usable rect 내부로 클램핑(Clamping)한다.
3. **일치하는 모니터가 존재하지 않는 경우 (Fallback):**
   - 주 모니터(Primary Monitor)의 usable rect 중앙에 배치한다.
   - 이때 주 모니터의 DPI 배율에 맞게 크기를 재보정한다 (`default_size × 주 모니터 DPI 배율 / 100`).
   - 재보정된 크기가 주 모니터 usable rect보다 크면 usable rect에 맞게 축소한다.

**VIRTUAL_DESKTOP 모드:**
- VirtualDesktopRoot 내부 좌표계를 사용하므로, 모니터 식별 및 DPI 보정은 적용하지 않는다.
- 단, §5.4의 클램핑 규칙은 그대로 적용한다.

4) 리사이즈 정책  
- WindowKind별로 `resizable=true/false`를 정의한다.
- `resizable=false`인 창은 사용자가 크기 변경을 할 수 없어야 한다.

5) 버튼 정책(기능 기준)  
- 최소화 기능은 제공하지 않는다(= 버튼 비활성화 또는 숨김 처리).
  - NATIVE_OS 모드에선 버튼 비활성화 처리를 한다.
  - VIRTUAL_DESKTOP 모드에선 버튼을 그리지 않는 것으로 숨김 처리를 한다.
- 최대화 기능은 `resizable=true`인 창에서만 제공한다.
- 닫기 기능은 모든 창에서 제공한다.

6) 자동 포커싱 정책 (Auto Focus)
- WindowKind 별로 `autoFocus=true/false`를 정의한다.
- `autoFocus=false`인 창은 새로 열렸을 때 해당 창으로 포커스가 이동하지 않는다.
- `autoFocus=true`인 창은 새로 열렸을 때 해당 창으로 포커스가 이동한다.
- `autoFocus=false` 구현은 Best effort로 하고 “열기 직후 다음 프레임에 메인 창으로 포커스 복귀” 등의 Fail-safe 처리를 한다.
- Passthrough 창은 autoFocus 설정과 무관하게 포커스를 갖지 않는다 (autoFocus는 Exclusive 창에만 적용)

7) 포커스 입력 전달 정책 (Focus Mode)
- 모든 서브 창은 아래 두 포커스 모드 중 하나를 가질 수 있다.
- Exclusive: 서브 창이 포커스를 가지면 키보드 입력을 해당 창이 독점하고, 메인 창에는 전달하지 않음.
- Passthrough: 서브 창이 화면에 떠 있어도 키보드 입력은 서브 창에 전달되지 않고 항상 메인 창에만 전달됨.
- SSH Login 서브 창은 Passthrough 포커스 모드를 가진다.
- Passthrough 창에서 클립보드 복사가 필요한 경우, 키보드 단축키가 아닌 UI 요소(버튼, 컨텍스트 메뉴 등)를 통해 제공한다 (MAY)

---

## 3. 모드 정의 및 화면 모드 제한

### 3.1 WindowingMode
- `WindowingMode.NATIVE_OS`
- `WindowingMode.VIRTUAL_DESKTOP`

### 3.2 메인 창 화면 모드(Display Mode) 제한 (MUST)

#### A) NATIVE_OS 모드
- 메인 창은 **창모드만 허용**한다.
  - 허용: `WINDOW_MODE_WINDOWED`, `WINDOW_MODE_MAXIMIZED` (둘 다 “창모드”로 간주)
  - 금지: `WINDOW_MODE_FULLSCREEN`, `WINDOW_MODE_EXCLUSIVE_FULLSCREEN`
- 메인 창 및 모든 서브 윈도우는 **OS 기본 프레임(타이틀바/테두리)을 유지**해야 한다.
  - 즉, `borderless`는 금지(= false 유지).

#### B) VIRTUAL_DESKTOP 모드
- 메인 창은 다음을 허용한다.
  - `WINDOW_MODE_MAXIMIZED`
  - `WINDOW_MODE_FULLSCREEN` (비독점)
  - `WINDOW_MODE_EXCLUSIVE_FULLSCREEN` (독점)
  - (선택) borderless fullscreen 도 허용
- 금지: `WINDOW_MODE_WINDOWED`
- 이 모드에서는 커스텀 프레임으로 서브창을 생성한다.
- 가상 윈도우의 타이틀바/버튼/리사이즈 핸들 역시 **게임이 직접 그린다(커스텀 크롬)**.

### 3.3 Fullscreen 계열 강제 규칙 (MUST)
- 사용자가 **Fullscreen/Exclusive/Borderless Fullscreen** 계열을 선택하거나, 메인 창이 해당 상태가 되면:
  - 시스템은 반드시 `WindowingMode.VIRTUAL_DESKTOP`로 강제 전환한다.
- 반대로, `WindowingMode.NATIVE_OS`에서는 fullscreen 계열 옵션을 UI에서 비활성화(또는 선택 시 자동 전환)해야 한다.

> 구현 참고: Godot는 fullscreen 진입 시 borderless 플래그가 강제로 설정될 수 있으므로, NATIVE_OS로 복귀 시 borderless를 반드시 원복한다(아래 “리스크/주의” 참고).

---

## 4. 네이티브 OS 윈도우 모드 요구사항

### 4.1 종속(생명주기) 정책 (MUST)
- 모든 서브 윈도우는 메인 창에 **transient(종속)로 설정**한다.
  - 목적:
    - 게임 종료 시 모든 서브 윈도우가 함께 닫힌다.
    - (가능하다면) 작업표시줄에 서브 윈도우가 별도로 뜨는 현상을 줄인다.

### 4.2 작업표시줄 억제는 Best effort (SHOULD)
- 작업표시줄(아래 바)에 서브 윈도우가 별도로 나타나지 않도록 **최선을 다한다**.
- 단, transient 동작은 플랫폼/환경에 따라 다를 수 있으므로 **완전 보장은 하지 않는다**.
- 폴백 정책(아래 중 하나 이상 MUST):
  1) 억제가 실패해도 그대로 허용(사용자 수용)
  2) “서브 윈도우가 작업표시줄에 뜨면” 사용자에게 가상 데스크톱 모드 전환 옵션을 안내/제공

### 4.3 위치 연동 없음 (MUST)
- 메인 창을 이동해도 서브 윈도우는 **자동으로 따라 움직이지 않는다**.
- 따라서 NATIVE_OS 모드의 지오메트리 저장/복원은 **스크린 절대 좌표 기반**으로 한다.
- 좌표 복원 시 해당 좌표가 현재 활성화된 전체 스크린 영역(Screen Rect) 내부인지 검증하는 로직이 반드시 포함되어야 한다.

### 4.4 버튼/리사이즈 정책 구현 (MUST)
- OS 프레임을 유지하되, 버튼 기능은 아래처럼 제어한다(“없음”은 “비활성화”로 해석).
  - 모든 창: 최소화 버튼 비활성화
  - resizable=false 창: 최대화 버튼 비활성화
  - resizable=true 창: 최대화 버튼 활성화(허용)
- 닫기 버튼은 OS 기본 동작을 사용하되, Godot의 `close_requested`를 반드시 처리한다(자동으로 닫히지 않을 수 있음).

### 4.5 NATIVE_OS 모드에서의 Passthrough 구현 (SHOULD):
- Passthrough 창은 Godot의 `Window.unfocusable = true` 설정을 통해 OS 레벨에서 포커스 획득을 차단한다.
- 이 설정이 정상 동작하지 않는 환경에서의 Fallback: 해당 창을 exclusive로 격상하여 동작시킨다 (포커스를 되돌리는 방식은 사용하지 않는다).

---

## 5. 가상 데스크톱 모드 요구사항

### 5.1 구성 (MUST)
- 메인 창은 크게 확장되며(최소 MAXIMIZED), 내부에 **에뮬레이티드 배경**이 존재한다.
- 모든 서브 윈도우는 메인 창 내부에 **임베디드(가상) 윈도우**로 표시된다.

### 5.2 커스텀 크롬 및 Z-order 관리 (MUST)

**타이틀바 / 버튼:**
- 최소화 버튼은 제공하지 않는다.
- 최대화 버튼은 resizable=true인 창에만 제공한다.
- 닫기 버튼은 모든 창에 제공한다.
- 드래그(이동), 리사이즈(허용 창만)는 게임이 직접 구현한다.

**Z-order 정책:**

1) **기본 원칙:** 일반적인 OS 윈도우와 동일하게 동작한다. Always-on-top 옵션은 제공하지 않는다.

2) **전면화(Raise) 트리거 — 다음 상호작용이 발생하면 해당 서브 창을 Z-order 최상위로 올린다:**
   - 창 내부 영역(콘텐츠, 타이틀바, 리사이즈 핸들 포함)에 대한 마우스 버튼 다운
   - `open_window(kind)` 호출로 기존 창이 전면/포커스 처리될 때
   - 드래그 이동 시작 시
   - 리사이즈 시작 시

3) **터미널(메인 UI)과의 관계:**
   - 터미널은 Z-order 스택에 참여하지 않으며, 항상 모든 서브 창의 **아래**에 위치한다.
   - 서브 창이 없거나 서브 창 바깥 영역을 클릭하면 터미널이 포커스를 받는다.

**4) 포커스 연동:**

- 포커스 동작은 각 서브 창의 Focus Mode(§2.2-7)에 따라 분기한다.

- **Exclusive 창:**
  - 해당 창이 Z-order 최상위일 때 입력 포커스를 갖는다.
  - 포커스를 가진 동안 키보드 입력은 해당 창이 독점하며, 터미널에는 전달하지 않는다.
  - 서브 창 바깥 영역 클릭 시 포커스를 해제하고 터미널에 포커스를 반환한다. 이때 해당 창의 Z-order는 변경하지 않는다.

- **Passthrough 창:**
  - 해당 창이 Z-order 최상위이더라도 입력 포커스를 갖지 않는다. 키보드 입력은 항상 터미널에 전달된다.
  - 마우스 클릭에 의한 Z-order 전면화는 정상 동작하되, 포커스는 터미널에 유지한다.

- **Exclusive 창과 Passthrough 창이 동시에 열려 있는 경우:**
  - 키보드 포커스는 Z-order가 가장 높은 **Exclusive 창**이 갖는다. Passthrough 창은 Z-order 순위와 무관하게 포커스 대상에서 제외한다.
  - Exclusive 창이 하나도 없으면 터미널이 포커스를 갖는다.

5) **Z-order 저장:**
   - Z-order는 지오메트리에 포함하여 저장/복원하지 않는다. 모드 전환이나 게임 재시작 시 서브 창은 열린 순서대로 쌓인다.

### 5.3 지오메트리 좌표계 (MUST)
- VIRTUAL_DESKTOP 모드의 지오메트리 저장/복원은 **VirtualDesktopRoot 좌표계(가상 데스크톱 내부 좌표)** 기준으로 한다.

### 5.4 메인 창 해상도 대응 (MUST)
- VIRTUAL_DESKTOP 모드에서 메인 창은 **MAXIMIZED 이상**(MAXIMIZED / FULLSCREEN / EXCLUSIVE_FULLSCREEN)을 유지해야 하며, WINDOWED 상태로의 전환은 허용하지 않는다.
  - 유저 또는 OS에 의해 unmaximize가 발생할 경우, 즉시 MAXIMIZED로 복귀한다.
- 메인 창의 usable rect가 변경되는 경우(모니터 전환, 해상도 변경, 작업표시줄 크기 변경 등), 서브 창이 메인 창 밖으로 벗어나지 않도록 위치를 클램핑한다.
  - 클램핑 우선순위: 서브 창의 **크기는 유지**하고 **위치만 조정**한다.
  - 유저 (창 드래그 등) 또는 OS에 의해 서브 창이 메인 창 밖으로 이동시킬 경우, 이동이 끝난 시점 (예: 드래그가 끝난 시점)에서 클램핑을 수행한다.

---

## 6. 지오메트리 저장/복원

### 6.1 저장 파일
- 게임 상태 저장 데이터 내부에 같이 저장하여, 게임 상태 로드시 레이아웃이 복원될 수 있도록 한다.
- 저장 트리거/범위/포맷/예외는 `12_save_load_persistence_spec_v0_1.md`를 따른다(See DOCS_INDEX.md → 12).

### 6.2 모드별 분리 저장 (MUST)
- NATIVE_OS / VIRTUAL_DESKTOP 레이아웃은 반드시 분리 저장한다.
- 각 모드로 **최초 진입 시(저장값 없음)** 게임이 정의한 `default` 위치/크기에서 표시한다.
- 레이아웃 변경 시 WindowManager는 모드별 최신 지오메트리 상태를 유지해야 한다.

---

## 7. WindowKind 레지스트리

### 7.1 MVP Kind
- `SSH_LOGIN`

### 7.2 Kind별 속성
| WindowKind | Single Instance | Resizable | Auto Focus | Focus Mode | Default Size | Default Pos (Native) | Default Pos (Virtual)|
|---|---:|---:|---:|---|---|---|---|
| SSH_LOGIN | YES | NO (MVP) | NO | Passthrough | 520×240 | (예: 120,80) | (예: 120,80) |

> 추후 창들이 추가되면 이 테이블에 속성을 확장한다.

> Godot의 Resource (tre) 파일로 각 Kind의 속성을 정의하는 것을 고려해본다.

---

## 8. WindowManager (Autoload 권장) — 책임과 API

### 8.1 책임 (MUST)
- 현재 WindowingMode 관리 및 강제 규칙 적용(fullscreen 계열 → virtual 강제)
- WindowKind 단일 인스턴스 보장
- 모드별 지오메트리 저장/복원
- 서브 윈도우 생성/닫기/포커스/전면화
- 모드 전환 및 화면 모드 전환 시 안전한 재생성 절차 수행(§10 참조)
- SSH 로그인 시도 이벤트 수신 → SSH 로그인 창 표시/갱신

### 8.2 공개 API (권장)
- `set_windowing_mode(mode) -> void`
- `set_main_window_display_mode(mode) -> void`
- `open_window(kind) -> void`
- `close_window(kind) -> void`
- `focus_window(kind) -> void`
- `notify_ssh_attempt(attempt: SshAuthAttempt) -> void`
- `load_layout() / save_layout()`

### 8.3 CEF 관련 고려 사항 (MAY)
- 추후 구현 목록에 있는 “웹페이지 뷰어(Godot CEF)” 는 리소스를 많이 먹으므로, WindowManager가 CEF의 초기화 또는 해제 (Initialize/Shutdown) 생명주기를 관리한다.

### 8.4 서브 윈도우 State 직렬화 인터페이스 (MUST)

- 모든 WindowKind는 아래 두 메서드를 반드시 구현해야 한다:
  - `serialize_state() -> Dictionary` — 현재 창의 런타임 상태를 Dictionary로 반환한다.
  - `restore_state(data: Dictionary) -> void` — Dictionary로부터 런타임 상태를 복원한다.
- Dictionary의 키/값 구성은 각 WindowKind가 자유롭게 정의한다. WindowManager는 내부 구조를 알지 않으며, 불투명(opaque) 데이터로 취급한다.
- WindowManager는 §10.2의 모드 전환 절차에서 이 인터페이스를 통해 State를 획득/복원한다.
- State가 없는 창(상태를 보존할 필요가 없는 경우)은 빈 Dictionary(`{}`)를 반환해도 된다.

---

## 9. SSH 로그인 창(SSH_LOGIN) — 기능 계약 (MVP)

### 9.1 트리거 (MUST)
다음 이벤트 발생 시 SSH_LOGIN 창을 자동으로 표시한다.
1) `ssh.connect` API 호출
2) `connect` systemcall 시도
   - 단, “SSH 접속 시도”로 판별된 케이스만(예: 포트 22 또는 상위 레이어 태깅)

### 9.2 표시 데이터 (MUST)
각 시도(attempt)에 대해 아래를 표시한다.
- Host: `hostname` (없으면 `ip`)
- User ID: 가능하면 표시, 없으면 `-` 또는 `<unknown>`
- Password: **마스킹 처리**로만 표시 (`***` 등)

비밀번호 정책:
- 실제 비밀번호 문자열은 UI/로그에 절대 노출하지 않는다.
- 표시는 길이 기반: `"*" * len(password)`
  - 비밀번호 길이는 노출되어도 된다.
  - 비밀번호가 제공되지 않을 경우 (null) 아무것도 표시하지 않는다.

### 9.3 자동 닫힘 (MUST)
- SSH_LOGIN 창이 나타난 이후 **3초간** 대기한다.
- 마지막 시도 이후 3초 동안 새로운 ssh/connect 이벤트가 없으면 자동으로 닫힌다.
- 3초 내 새 시도가 들어오면 닫힘 데드라인을 **마지막 시도 시점 + 3초**로 갱신한다.
- 마우스가 해당 창 안을 클릭(버튼 다운)하고 있을 경우 닫힘 데드라인이 넘어도 마우스가 버튼을 뗀 상태에서 창 밖으로 나갈 때까지 닫힘을 유예한다. (마우스가 창을 계속 나가지 않는다면 영원히 닫히지 않아도 됨)
- 전환 중 경과 시간은 타이머에서 차감하지 않는다.

### 9.4 UI 레이아웃(권장)
* **윈도우 타이틀바:** `SSH Login` 표시.
* **헤더 섹션:** 창 최상단 중앙에 `<Host_Name>` (또는 IP)을 큰 텍스트로 배치.
* **데이터 필드 구성 (VBoxContainer 구조):**
  * **User 필드:** "User:" 라벨 + 읽기 전용(ReadOnly) LineEdit.
  * **Passwd 필드:** "Passwd:" 라벨 + 읽기 전용(ReadOnly) LineEdit.
* **입력창 스타일:** 흰색 배경, 검은색 테두리, 내부 텍스트 좌측 정렬.
* **실시간 갱신 로직:** 새로운 SSH 시도(`SshAuthAttempt`) 이벤트 수신 시, 기존 창의 모든 필드 내용을 즉시 덮어쓴다.
  * 이때 자동 닫힘 타이머(3초)는 초기화(Reset)된다.

---

## 10. 모드 전환/재생성 절차 (리스크 대응) (MUST)

### 10.1 왜 필요한가
- `embed_subwindows`(임베디드/비임베디드) 전환은 **이미 떠 있는 창에 즉시/일관되게 적용되지 않을 수 있다**.
- 따라서 모드 전환 시 서브 윈도우를 “닫고 재생성”하는 방식이 가장 안정적이다.

### 10.2 절차 (MUST)
모드 전환 또는 메인 창 화면 모드 전환 시, 아래 절차를 따른다.

1) 모든 서브 윈도우에 대해:
   - 지오메트리 저장
   - 각 서브 윈도우의 상태 (State) 데이터 획득. §8.4의 serialize_state()를 호출하여 State를 획득한다.
   - 닫기(hide/queue_free 등 프로젝트 정책)
2) `embed_subwindows` 설정 변경
3) 메인 창 화면 모드(Display Mode) 설정 변경
4) 새 모드의 레이아웃 로드
5) 열려 있던 모든 서브 윈도우 재오픈
   - 변경된 모드의 저장되어 있는 지오메트리가 있을 경우 지오메트리 복원
   - 각 서브 윈도우의 상태 (State) 데이터 복원. §8.4의 restore_state()를 호출하여 State를 복원한다.
* 가상 모드 진입(`MAXIMIZED`)은 아래와 같이 더 세심한 정책을 따른다:
    1. `WINDOW_MODE_WINDOWED`로 1프레임 유지
    2. embed 설정/VirtualDesktopRoot 구성
    3. 그 다음 `MAXIMIZED` 전환
    4. 마지막에 서브윈도우 생성


---

## 11. EXCLUSIVE_FULLSCREEN 상태 제약 (리스크 대응) (MUST)

- `WINDOW_MODE_EXCLUSIVE_FULLSCREEN` 상태에서는:
  - **OS 윈도우 호출(네이티브 파일 다이얼로그, OS 서브윈도우 생성 등)을 금지**한다.
  - 이유: 독점 풀스크린은 OS/드라이버/윈도우 매니저에 따라 “전환/블랙스크린/모드 변화”가 발생할 수 있다.
- 필요한 경우:
  - 인게임 UI로 대체(가상 다이얼로그)
  - 또는 EXCLUSIVE 해제 후 호출(상태 전환 절차는 §10 준수)

---

## 12. (추후) 멀티 윈도우 옵션 목록 (중요도 순)
1) 네트워크 트레이싱 창
2) Network Topology viewer
3) 파일 전송 대기줄(큐/대역폭/실패/재시도)
4) 웹페이지 뷰어(Godot CEF)
5) 프로세서 목록
6) 코딩 에디터 윈도우
7) 유출된 CCTV 모니터링
8) 패킷 스니핑 창

---

## 13. 수용 기준(테스트 시나리오)

### 13.1 모드 전환 및 재생성 (§10)
- NATIVE_OS → VIRTUAL_DESKTOP 전환 시:
  - 모든 서브 윈도우가 닫힌 후 재생성된다.
  - 각 모드의 저장된 지오메트리가 정확히 복원된다.
  - 서브 윈도우의 State가 `serialize_state()` / `restore_state()`를 통해 보존된다 (예: SSH_LOGIN의 표시 데이터, 타이머 잔여 시간).
- VIRTUAL_DESKTOP → NATIVE_OS 전환 시에도 동일하게 동작한다.
- 전환 중 경과 시간이 SSH_LOGIN 자동 닫힘 타이머에서 차감되지 않는다.
- 가상 모드 진입 시 WINDOWED → embed 설정 → MAXIMIZED → 서브 윈도우 생성 순서가 지켜진다.

### 13.2 네이티브 OS 모드 (§3.2A, §4)
- 메인 창이 WINDOWED / MAXIMIZED에서 동작한다.
- FULLSCREEN / EXCLUSIVE_FULLSCREEN 진입 시 자동으로 VIRTUAL_DESKTOP으로 전환된다.
- 서브 윈도우는 OS 프레임(타이틀바/테두리)을 유지한다. borderless가 아니다.
- 모든 서브 윈도우의 최소화 버튼이 비활성화되어 있다.
- resizable=false 창의 최대화 버튼이 비활성화되어 있다.
- 메인 창을 이동해도 서브 윈도우는 따라 움직이지 않는다.
- 작업표시줄 억제는 best effort이며, 실패 시 폴백 정책이 동작한다.
- 서브 윈도우는 메인 창에 transient으로 종속되어, 게임 종료 시 함께 닫힌다.

### 13.3 가상 데스크톱 모드 (§3.2B, §5)
- 메인 창이 MAXIMIZED 이상 상태를 유지한다. WINDOWED는 금지된다.
- unmaximize 발생 시 즉시 MAXIMIZED로 복귀한다.
- 에뮬레이티드 배경이 표시된다.
- 가상 윈도우들은 커스텀 타이틀바 / 닫기 버튼 / (resizable=true 시) 최대화 버튼으로 동작한다.
- 최소화 버튼은 표시되지 않는다.
- usable rect 변경(모니터 전환, 해상도 변경 등) 시 서브 창이 메인 창 밖으로 벗어나지 않도록 클램핑된다. 클램핑 시 크기는 유지되고 위치만 조정된다.

### 13.4 Z-order 및 전면화 (§5.2)
- 서브 창 내부(콘텐츠, 타이틀바, 리사이즈 핸들)를 마우스 클릭하면 해당 창이 Z-order 최상위로 올라간다.
- `open_window(kind)` 호출로 기존 창이 전면화된다.
- 터미널은 항상 모든 서브 창 아래에 위치한다.
- 서브 창 바깥 영역 클릭 시 터미널이 포커스를 받되, 서브 창의 Z-order는 변경되지 않는다.
- Z-order는 저장/복원 대상이 아니다. 재시작 시 서브 창은 열린 순서대로 쌓인다.

### 13.5 Focus Mode (§2.2-7, §4.5, §5.2-4)

**Passthrough 창:**
- VIRTUAL_DESKTOP: Passthrough 창이 Z-order 최상위여도 키보드 입력이 터미널에 전달된다. 서브 창에는 키보드 입력이 전달되지 않는다.
- NATIVE_OS: Passthrough 창(`unfocusable=true`)의 드래그/닫기 조작이 메인 창의 포커스를 뺏지 않는다.
- NATIVE_OS: unfocusable이 정상 동작하지 않는 환경에서는 해당 창이 exclusive로 격상되어 동작한다. 포커스 되돌리기(줄다리기)는 발생하지 않는다.

**Exclusive 창:**
- Exclusive 창이 포커스를 가진 동안 키보드 입력이 해당 창에 독점되고 터미널에 전달되지 않는다.
- 서브 창 바깥 클릭 시 포커스가 터미널로 반환된다.

**혼합 상황:**
- Exclusive 창과 Passthrough 창이 동시에 열려 있을 때, Passthrough 창이 Z-order 최상위여도 키보드 포커스는 Z-order가 가장 높은 Exclusive 창이 갖는다.
- Exclusive 창이 하나도 없으면 터미널이 포커스를 갖는다.

### 13.6 자동 포커싱 (§2.2-6)
- autoFocus=true 창은 열릴 때 포커스가 해당 창으로 이동한다.
- autoFocus=false 창은 열릴 때 포커스가 이동하지 않는다 (best effort). Fail-safe로 다음 프레임에 메인 창으로 포커스가 복귀한다.

### 13.7 지오메트리 저장/복원 (§2.2-3, §6)

- 저장 트리거/범위/포맷은 `12_save_load_persistence_spec_v0_1.md`를 따른다(See DOCS_INDEX.md → 12).

**NATIVE_OS DPI/모니터 대응:**
- 모니터 A(150% DPI)에서 서브 창 위치를 저장한 후, 동일 모니터에서 DPI를 100%로 변경하고 복원하면 위치/크기가 비율 보정된다.
- 모니터 A에서 저장한 후, 모니터 A를 분리하고 게임을 재시작하면 서브 창이 주 모니터 usable rect 중앙에 default 크기로 배치된다.
- 복원된 좌표가 usable rect를 벗어나면 클램핑된다.

**VIRTUAL_DESKTOP:**
- VirtualDesktopRoot 내부 좌표계로 저장/복원된다. 모니터 식별 및 DPI 보정은 적용되지 않는다.

**최초 진입:**
- 저장값이 없는 모드로 최초 진입 시 Kind별 default 위치/크기에서 표시된다.

### 13.8 SSH_LOGIN (§9)
- `ssh.connect` 호출 또는 SSH로 판별된 `connect` 시도 시 자동으로 창이 열린다.
- Host, User, Password(마스킹)가 표시된다. 비밀번호가 null이면 Passwd 필드는 비어 있다.
- 새로운 시도 이벤트 수신 시 기존 필드 내용이 즉시 덮어쓰이고 타이머가 리셋된다.
- 마지막 시도 이후 3초간 추가 이벤트가 없으면 자동으로 닫힌다.
- 마우스 버튼이 창 내부에서 눌린 상태이면 닫힘이 유예된다. 버튼을 뗀 후 마우스가 창 밖으로 나가면 닫힌다.
- SSH_LOGIN은 Passthrough 모드이며, 창이 떠 있는 동안 터미널 입력이 방해받지 않는다.
- SSH_LOGIN은 autoFocus=false이며, 열릴 때 터미널 포커스를 뺏지 않는다.

### 13.9 단일 인스턴스 (§2.2-2)
- 이미 열려 있는 Kind에 대해 `open_window`를 재호출하면 새 창이 생기지 않고, 기존 창이 전면화/포커스되며 내용이 갱신된다.

### 13.10 EXCLUSIVE_FULLSCREEN 제약 (§11)
- EXCLUSIVE_FULLSCREEN 상태에서 OS 윈도우 호출(네이티브 파일 다이얼로그 등)이 발생하지 않는다.
- 필요한 경우 인게임 UI로 대체되거나, EXCLUSIVE 해제 후 호출된다.

### 13.11 State 직렬화 (§8.4)
- 모든 WindowKind가 `serialize_state()` / `restore_state()`를 구현한다.
- State가 없는 창은 빈 Dictionary를 반환하며, 복원 시 에러가 발생하지 않는다.
- 모드 전환 전후로 State가 정확히 보존된다 (SSH_LOGIN: host, user, masked password, 타이머 잔여 시간).

---

## 14. 참고(엔진 동작 관련)
- Transient 창은 부모와 생명주기가 연결되며, 플랫폼에 따라 동작이 다를 수 있다.
- Fullscreen 진입 시 borderless가 강제로 설정될 수 있으므로, 모드 복귀 시 원복이 필요할 수 있다.
- 임베디드/비임베디드 혼합은 별도의 Viewport 구성 없이는 어렵고, 런타임 전환은 “재생성”이 안전하다.
