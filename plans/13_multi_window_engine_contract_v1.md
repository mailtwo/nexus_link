# 멀티 윈도우 시스템 & SSH 로그인 창(MVP) — 엔진 계약서

- 문서 버전: v1.3
- 대상 엔진: **Godot 4.6**
- 타겟 플랫폼: **PC / Windows**
- 문서 목적: Codex가 그대로 구현 가능한 수준의 **규칙/데이터/흐름**을 명문화합니다.
- 구현 범위:
  - ✅ 멀티 윈도우 시스템(네이티브 OS 윈도우 모드 + 가상 데스크톱 모드)
  - ✅ SSH 로그인 창(MVP)
  - ✅ 데스크톱 오버레이(DesktopOverlay) — 배경 창

---

## 0. RFC 용어

- **MUST**: 반드시 지켜야 함
- **SHOULD**: 가능한 지켜야 함(환경/플랫폼 제약으로 예외 가능)
- **MAY**: 선택 사항

---

## 1. 용어 정의

- **메인 창(Main Window)**: 현재 OS에서 게임 대표로 노출되는 창. NATIVE_OS 모드에서는 `Primary Core Window`를 의미한다.
- **코어 창(Core Window)**: 게임의 주 플레이 인터페이스 창. 알파 범위에서는 `TerminalWindow`와 `GuiMainWindow` 두 종류를 사용한다.
- **Primary Core Window**: 현재 세션에서 대표 메인 창 역할을 가진 코어 창. NATIVE_OS 모드에서 Alt+Tab/작업표시줄의 대표 노출 대상을 의미한다.
- **Secondary Window**: Primary Core Window가 아닌 나머지 게임 창. 다른 코어 창과 WindowKind 창을 모두 포함한다.
- **서브 윈도우(Sub Window)**: WindowKind 기반 기능 창(예: `SSH_LOGIN`). 코어 창과 구분되는 계약 단위.
- **Host Window (Reserved)**: 베타 범위에서 검토할 분리형 메인 호스트 창. 알파에서는 구현하지 않으며, Host-ready 경계만 선반영한다.
- **윈도우 종류(WindowKind)**: 서브 윈도우의 타입(예: `SSH_LOGIN`). 각 Kind는 "단일 인스턴스/지오메트리/리사이즈 정책"의 단위.
- **지오메트리(Geometry)**: 위치(Position) + 크기(Size) + (선택) 최대화 상태.
- **네이티브 OS 윈도우 모드(NATIVE_OS)**: 서브 윈도우가 **Windows OS 윈도우**로 생성되는 모드(= OS 프레임 유지).
- **가상 데스크톱 모드(VIRTUAL_DESKTOP)**: 메인 창 내부에 **에뮬레이티드 배경(가상 데스크톱)** 을 만들고, 그 위에 서브 윈도우를 **임베디드(가상) 윈도우**로 띄우는 모드.
- **데스크톱 오버레이(DesktopOverlay)**: NATIVE_OS 모드 전용. 특정 모니터 전체를 덮는 borderless 배경 창. WindowKind 체계와 별개인 시스템 레이어.
- **모니터 핑거프린트(Monitor Fingerprint)**: 모니터를 고유하게 식별하기 위한 값. 해상도 + DPI 배율의 조합으로 구성.

---

## 2. 최상위 목표 및 제약

### 2.1 전역 목표
- "영화처럼 여러 창이 동시에 살아있는 느낌"의 멀티 윈도우 UX를 제공한다.
- 알파 버전 구현 목표 창:
  - **SSH 로그인 창**
  - **웹페이지 뷰어(Godot CEF)**
  - **파일 전송 대기줄**
  - **Network Topology viewer**
  - **월드 맵 및 네트워크 트레이싱창**
  - **프로세서 목록**
- 코딩 에디터 윈도우는 알파 범위에서 제외하고 추후 구현한다(§12 참조).

### 2.2 전역 제약 (MUST)

0) 엔진/플랫폼  
- Godot 4.6, Windows PC만 지원한다.

1) 모드 2종 동시 지원  
- 모든 서브 윈도우는 `NATIVE_OS` / `VIRTUAL_DESKTOP` 두 모드를 모두 지원한다.
- DesktopOverlay는 이 제약에서 제외된다(NATIVE_OS 전용 시스템 레이어).

2) 단일 인스턴스  
- WindowKind마다 **동시에 1개만** 존재한다.
- 동일 Kind에 대한 "열기"가 다시 요청되면: 기존 창을 전면/포커스 처리하고 내용을 갱신한다.
- 위 제약은 "창 인스턴스 수"에만 적용한다.
- 창 내부의 논리 항목(예: 파일 전송 작업 목록, 에디터 탭 문서)은 여러 개를 허용한다.

3) 지오메트리 기억  
- 각 WindowKind는 모드별로 지오메트리를 저장/복원한다.
- 저장은 게임 재실행 후에도 유지된다.

**NATIVE_OS 모드 저장 항목 (MUST):**
- 위치(Position), 크기(Size), 최대화 여부
- 해당 창이 위치한 **모니터 핑거프린트** (해상도 + DPI 배율의 조합)
  - 모니터 인덱스만으로는 모니터 연결 순서가 바뀔 수 있으므로, 해상도와 DPI 배율을 함께 저장하여 동일 모니터를 식별하는 데 사용한다.
- 저장 좌표는 해당 모니터의 **물리 픽셀(Physical Pixel) 기준 스크린 절대 좌표**로 한다.

**복원 절차 (MUST):**
1. 저장된 모니터 핑거프린트와 일치하는 모니터가 현재 연결되어 있는지 확인한다.
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
- Primary Core Window는 최소화 기능을 허용한다.
- Secondary(WindowKind) 창의 최소화 허용 여부는 `Minimizable` 플래그로 정의한다.
- 알파 구현에서는 서브창 최소화 기능을 구현하지 않는다.
  - NATIVE_OS 모드에선 서브창 최소화 버튼을 비활성화 처리한다.
  - VIRTUAL_DESKTOP 모드에선 서브창 최소화 버튼을 그리지 않는 것으로 숨김 처리한다.
- 최대화 기능은 `resizable=true`인 창에서만 제공한다.
- 닫기 기능은 모든 창에서 제공한다.

6) 자동 포커싱 정책 (Auto Focus)
- WindowKind 별로 `autoFocus=true/false`를 정의한다.
- `autoFocus=false`인 창은 새로 열렸을 때 해당 창으로 포커스가 이동하지 않는다.
- `autoFocus=true`인 창은 새로 열렸을 때 해당 창으로 포커스가 이동한다.
- `autoFocus=false` 구현은 Best effort로 하고 "열기 직후 다음 프레임에 Primary Core Window로 포커스 복귀" 등의 Fail-safe 처리를 한다.
- Passthrough 창은 autoFocus 설정과 무관하게 포커스를 갖지 않는다 (autoFocus는 Exclusive 창에만 적용)

7) 포커스 입력 전달 정책 (Focus Mode)
- 모든 서브 창은 아래 두 포커스 모드 중 하나를 가질 수 있다.
- Exclusive: 서브 창이 포커스를 가지면 키보드 입력을 해당 창이 독점하고, Primary Core Window에는 전달하지 않음.
- Passthrough: 서브 창이 화면에 떠 있어도 키보드 입력은 서브 창에 전달되지 않고 항상 Primary Core Window에만 전달됨.
- SSH Login 서브 창은 Passthrough 포커스 모드를 가진다.
- Passthrough 창에서 클립보드 복사가 필요한 경우, 키보드 단축키가 아닌 UI 요소(버튼, 컨텍스트 메뉴 등)를 통해 제공한다 (MAY)

8) 알파 코어 창 / Primary 승격 정책 (MUST)
- 알파 기간에는 `TerminalWindow`와 `GuiMainWindow`를 코어 창으로 운영한다.
- NATIVE_OS 모드에서는 코어 창 중 정확히 1개만 Primary Core Window여야 한다.
- 게임 실행 중 코어 창은 최소 1개 이상 항상 존재해야 한다.
- 코어 창 승격 트리거:
  - 사용자가 터미널/GUI 메인 창을 전면화하도록 명시 요청한 경우(열기/포커스/전환 명령), 대상 코어 창을 Primary로 승격한다.
  - 현재 Primary 코어 창이 닫히거나 숨겨지고 다른 코어 창이 살아 있으면, 남은 코어 창을 즉시 Primary로 승격한다.
- Fail-safe: 예외 상황으로 코어 창이 모두 닫히면 `TerminalWindow`를 재오픈하고 Primary로 승격한다.

9) Secondary 창 노출 억제 정책 (MUST)
- Primary Core Window를 제외한 모든 Secondary Window(다른 코어 창 + WindowKind 창)는 작업표시줄/Alt+Tab 노출 억제를 시도해야 한다.
- 구현은 `transient(primary)` + 플랫폼별 확장 스타일 조합을 사용한다.
- 플랫폼/드라이버 제약으로 완전 억제가 실패할 수 있으므로 Best effort로 취급하고, 폴백 정책은 §4.2를 따른다.

10) 베타용 Host Window 분리 준비 (MUST)
- 알파에서는 1x1/오프스크린 Host Window를 구현하지 않는다.
- 대신, 추후 베타에서 Host Window로 무중단 이행할 수 있도록 Host-ready 경계를 §8.5에 정의하고 현재 구현에 적용한다.

---

## 3. 모드 정의 및 화면 모드 제한

### 3.1 WindowingMode
- `WindowingMode.NATIVE_OS`
- `WindowingMode.VIRTUAL_DESKTOP`

### 3.2 메인 창(Primary Core Window) 화면 모드(Display Mode) 제한 (MUST)

#### A) NATIVE_OS 모드
- Primary Core Window는 **창모드만 허용**한다.
  - 허용: `WINDOW_MODE_WINDOWED`, `WINDOW_MODE_MAXIMIZED` (둘 다 "창모드"로 간주)
  - 금지: `WINDOW_MODE_FULLSCREEN`, `WINDOW_MODE_EXCLUSIVE_FULLSCREEN`
- Primary/Secondary 게임 창은 **OS 기본 프레임(타이틀바/테두리)을 유지**해야 한다.
  - 즉, `borderless`는 금지(= false 유지).
  - DesktopOverlay는 이 제약에서 제외된다(§15 참조).

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
- 사용자가 **Fullscreen/Exclusive/Borderless Fullscreen** 계열을 선택하거나, Primary Core Window가 해당 상태가 되면:
  - 시스템은 반드시 `WindowingMode.VIRTUAL_DESKTOP`로 강제 전환한다.
  - VIRTUAL_DESKTOP으로 전환될 때 활성화된 모든 DesktopOverlay는 자동으로 비활성화한다.
- 반대로, `WindowingMode.NATIVE_OS`에서는 fullscreen 계열 옵션을 UI에서 비활성화(또는 선택 시 자동 전환)해야 한다.

> 구현 참고: Godot는 fullscreen 진입 시 borderless 플래그가 강제로 설정될 수 있으므로, NATIVE_OS로 복귀 시 borderless를 반드시 원복한다(아래 "리스크/주의" 참고).

---

## 4. 네이티브 OS 윈도우 모드 요구사항

### 4.1 종속(생명주기) 정책 (MUST)
- 모든 Secondary Window(비-Primary 코어 창 + WindowKind 창)는 Primary Core Window에 **transient(종속)로 설정**한다.
  - 목적:
    - 게임 종료 시 모든 Secondary 창이 함께 닫힌다.
    - (가능하다면) 작업표시줄/Alt+Tab에서 Secondary 창 노출을 줄인다.
- DesktopOverlay 창은 transient 설정을 적용하지 않는다(§15.4 참조).

### 4.2 작업표시줄 억제는 Best effort (SHOULD)
- 작업표시줄(아래 바) 및 Alt+Tab에 Secondary 창이 별도 엔트리로 나타나지 않도록 **최선을 다한다**.
- 단, transient/확장 스타일 동작은 플랫폼/환경에 따라 다를 수 있으므로 **완전 보장은 하지 않는다**.
- 폴백 정책(아래 중 하나 이상 MUST):
  1) 억제가 실패해도 그대로 허용(사용자 수용)
  2) "Secondary 창이 작업표시줄/Alt+Tab에 뜨면" 사용자에게 가상 데스크톱 모드 전환 옵션을 안내/제공

### 4.3 위치 연동 없음 (MUST)
- Primary Core Window를 이동해도 Secondary 창은 **자동으로 따라 움직이지 않는다**.
- 따라서 NATIVE_OS 모드의 지오메트리 저장/복원은 **스크린 절대 좌표 기반**으로 한다.
- 좌표 복원 시 해당 좌표가 현재 활성화된 전체 스크린 영역(Screen Rect) 내부인지 검증하는 로직이 반드시 포함되어야 한다.

### 4.4 버튼/리사이즈 정책 구현 (MUST)
- OS 프레임을 유지하되, 버튼 기능은 아래처럼 제어한다("없음"은 "비활성화"로 해석).
  - Primary Core Window: 최소화 버튼 활성화(허용)
  - Secondary(WindowKind): `Minimizable` 플래그 기준으로 제어하되, 알파에서는 비활성화
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
- Secondary(WindowKind)의 최소화 버튼은 `Minimizable=true`인 창에만 제공할 수 있다.
- 단, 알파 구현에서는 `Minimizable=true`인 창도 최소화 버튼을 표시하지 않는다.
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

3) **Primary Core Window와의 관계:**
   - Primary Core Window는 서브 창 Z-order 스택 아래 레이어에 위치한다.
   - 서브 창이 없거나 서브 창 바깥 영역을 클릭하면 Primary Core Window가 포커스를 받는다.

**4) 포커스 연동:**

- 포커스 동작은 각 서브 창의 Focus Mode(§2.2-7)에 따라 분기한다.

- **Exclusive 창:**
  - 해당 창이 Z-order 최상위일 때 입력 포커스를 갖는다.
  - 포커스를 가진 동안 키보드 입력은 해당 창이 독점하며, Primary Core Window에는 전달하지 않는다.
  - 서브 창 바깥 영역 클릭 시 포커스를 해제하고 Primary Core Window에 포커스를 반환한다. 이때 해당 창의 Z-order는 변경하지 않는다.

- **Passthrough 창:**
  - 해당 창이 Z-order 최상위이더라도 입력 포커스를 갖지 않는다. 키보드 입력은 항상 Primary Core Window에 전달된다.
  - 마우스 클릭에 의한 Z-order 전면화는 정상 동작하되, 포커스는 Primary Core Window에 유지한다.

- **Exclusive 창과 Passthrough 창이 동시에 열려 있는 경우:**
  - 키보드 포커스는 Z-order가 가장 높은 **Exclusive 창**이 갖는다. Passthrough 창은 Z-order 순위와 무관하게 포커스 대상에서 제외한다.
  - Exclusive 창이 하나도 없으면 Primary Core Window가 포커스를 갖는다.

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

### 7.1 알파 WindowKind
- `SSH_LOGIN`
- `WORLD_MAP_TRACE`
- `NETWORK_TOPOLOGY`
- `FILE_TRANSFER_QUEUE`
- `WEB_VIEWER`
- `PROCESS_LIST`

> `TERMINAL`, `GUI_MAIN`은 WindowKind가 아니라 CoreWindowKind로 취급한다(§1, §2.2-8 참조).

### 7.2 Kind별 속성
| WindowKind | Single Instance | Resizable | Auto Focus | Focus Mode | Volatile | Minimizable | Default Size | Default Pos (Native) | Default Pos (Virtual)|
|---|---:|---:|---:|---|---:|---:|---|---|---|
| SSH_LOGIN | YES | NO | NO | Passthrough | YES | NO | 520×240 | 120,80 | 120,80 |
| FILE_TRANSFER_QUEUE | YES | NO | NO | Passthrough | YES | NO | 520×240 | 120,80 | 120,80 |
| WEB_VIEWER | YES | YES | YES | Exclusive | NO | YES | 520×240 | 120,80 | 120,80 |
| CODE_EDITOR (Reserved/Future) | YES | TBD | TBD | TBD | NO | YES | TBD | TBD | TBD |

> 웹페이지 뷰어, Network Topology viewer, 월드 맵 및 네트워크 트레이싱창, 프로세서 목록 속성은 알파 범위에서 추가 확정이 필요하다.

> 코딩 에디터 윈도우는 추후 구현 항목이며, 탭 기반 다중 문서 규칙은 §7.3을 따른다.

> 알파 구현 목표 창들이 추가되면 이 테이블에 속성을 확장한다.

> TBD 항목은 알파 구현 시점에 확정한다.

> `WEB_VIEWER`, `CODE_EDITOR`는 `Minimizable=YES`를 계약상 예약하지만, 알파 구현에서는 최소화 버튼/동작을 비활성화한다.

> Godot의 Resource (tre) 파일로 각 Kind의 속성을 정의하는 것을 고려해본다.

### 7.3 단일 창 내 다중 항목 패턴
- `FILE_TRANSFER_QUEUE`는 WindowKind 인스턴스 1개 창만 유지하되, 창 내부에 `TransferJob` 항목을 여러 개 동시에 표시/갱신해야 한다 (MUST, 알파).
- 여러 파일 전송이 동시에 발생해도 새 창을 추가로 만들지 않고, 기존 `FILE_TRANSFER_QUEUE` 창의 목록 항목을 추가/갱신한다 (MUST).
- `open_window(FILE_TRANSFER_QUEUE)` 재호출은 "새 창 생성"이 아니라 "기존 창 전면화 + 목록 갱신"으로 처리한다 (MUST).
- `CODE_EDITOR`는 추후 구현 시 동일 패턴을 따른다: 창 1개 + 탭 N개(파일별 문서/버퍼 전환) (SHOULD).
- `CODE_EDITOR` 탭 정책은 알파 범위 밖이며, 구현 시점에 Kind 속성/상세 계약을 별도 확정한다.

### 7.4 Volatile 자동 닫힘 정책 (MUST)
- `Volatile=YES`인 WindowKind는 마지막 업데이트 시점 기준으로 **3초 무업데이트** 상태가 되면 자동 닫힘 대상이 된다.
- 새 업데이트가 들어오면 닫힘 데드라인은 **마지막 업데이트 시점 + 3초**로 갱신한다.
- 창 내부에서 마우스 버튼 다운 상태이면, 데드라인이 지나도 자동 닫힘을 유예한다.
- 마우스 버튼 업 이후 커서가 창 밖으로 나가면 유예를 해제한다.
- 모드 전환 중 경과 시간은 Volatile 자동 닫힘 타이머에서 차감하지 않는다.
- Kind별 업데이트 소스:
  - `SSH_LOGIN`: `ssh.connect` 또는 `connect` 시도 이벤트.
  - `FILE_TRANSFER_QUEUE`: 전송 목록 업데이트(추가/진행률/상태 변화).
- `FILE_TRANSFER_QUEUE`는 추가로 아래 조건을 만족할 때만 닫힌다:
  - `3초 무업데이트`이면서 `활성 전송 작업 수=0`.

---

## 8. WindowManager (Autoload 권장) — 책임과 API

### 8.1 책임 (MUST)
- 현재 WindowingMode 관리 및 강제 규칙 적용(fullscreen 계열 → virtual 강제)
- 코어 창(`TerminalWindow`, `GuiMainWindow`)의 Primary 승격/강등 관리
- WindowKind 단일 인스턴스 보장
- 모드별 지오메트리 저장/복원
- Secondary 창 노출 억제 정책(transient/작업표시줄/Alt+Tab) 적용
- 서브 윈도우 생성/닫기/포커스/전면화
- 모드 전환 및 화면 모드 전환 시 안전한 재생성 절차 수행(§10 참조)
- SSH 로그인 시도 이벤트 수신 → SSH 로그인 창 표시/갱신
- DesktopOverlay 생명주기 관리(§15 참조)

### 8.2 공개 API (권장)
- `set_windowing_mode(mode) -> void`
- `set_main_window_display_mode(mode) -> void`
- `set_primary_core_window(coreKind) -> void` (`TERMINAL` / `GUI_MAIN`)
- `get_primary_core_window() -> CoreWindowKind`
- `open_window(kind) -> void`
- `close_window(kind) -> void`
- `focus_window(kind) -> void`
- `notify_ssh_attempt(attempt: SshAuthAttempt) -> void`
- `load_layout() / save_layout()`
- `set_desktop_overlay(monitorFingerprint, enabled) -> void`
- `get_desktop_overlay_states() -> Dictionary<MonitorFingerprint, bool>`

### 8.3 CEF 관련 고려 사항 (MAY)
- 구현 목록에 있는 "웹페이지 뷰어(Godot CEF)" 는 리소스를 많이 먹으므로, WindowManager가 CEF의 초기화 또는 해제 (Initialize/Shutdown) 생명주기를 관리한다.

### 8.4 서브 윈도우 State 직렬화 인터페이스 (MUST)

- 모든 WindowKind는 아래 두 메서드를 반드시 구현해야 한다:
  - `serialize_state() -> Dictionary` — 현재 창의 런타임 상태를 Dictionary로 반환한다.
  - `restore_state(data: Dictionary) -> void` — Dictionary로부터 런타임 상태를 복원한다.
- Dictionary의 키/값 구성은 각 WindowKind가 자유롭게 정의한다. WindowManager는 내부 구조를 알지 않으며, 불투명(opaque) 데이터로 취급한다.
- WindowManager는 §10.2의 모드 전환 절차에서 이 인터페이스를 통해 State를 획득/복원한다.
- State가 없는 창(상태를 보존할 필요가 없는 경우)은 빈 Dictionary(`{}`)를 반환해도 된다.

### 8.5 Host-ready 경계 (MUST)

- 알파 구현에서 아래 경계를 반드시 지켜 베타의 Host Window 분리를 대비한다.
- 창 역할(Primary/Secondary)과 창 종류(WindowKind/CoreWindowKind)를 분리한다.
  - 코드에서 "`TerminalWindow`가 항상 메인"이라는 가정을 금지한다.
- 지오메트리/상태 저장 키는 논리 식별자(WindowKind/CoreWindowKind) 기준으로 관리하고, HWND 같은 OS 핸들에 직접 의존하지 않는다.
- 작업표시줄/Alt+Tab/transient/소유자 설정은 `PlatformWindowAdapter` 계층으로 격리하고, gameplay/UI 레이어에서 Win32 직접 호출을 금지한다.
- 입력 라우팅 대상은 "현재 Primary Core Window"를 조회해 결정하며, 특정 창 이름 하드코딩을 금지한다.
- 모드 전환/재생성 절차(§10)는 "Primary가 교체될 수 있음"을 전제로 작성한다.

---

## 9. SSH 로그인 창(SSH_LOGIN) — 기능 계약 (MVP)

### 9.1 트리거 (MUST)
다음 이벤트 발생 시 SSH_LOGIN 창을 자동으로 표시한다.
1) `ssh.connect` API 호출
2) `connect` systemcall 시도
   - 단, "SSH 접속 시도"로 판별된 케이스만(예: 포트 22 또는 상위 레이어 태깅)

### 9.2 표시 데이터 (MUST)
각 시도(attempt)에 대해 아래를 표시한다.
- Host: `hostname` (없으면 `ip`)
- User ID: 가능하면 표시, 없으면 `-` 또는 `<unknown>`
- Password: 실제 시도 비밀번호 원문을 표시한다.
- Result: 접속 시도 결과(`success`/`failure`)와 결과 코드(`OK` 또는 `ERR_*`)를 함께 전달한다.

비밀번호 노출 범위 정책:
- 비밀번호 원문 표시는 SSH_LOGIN UI의 런타임 메모리 표시 범위로 한정한다.
- 게임 이벤트 payload 및 로그에는 비밀번호 원문을 기록하지 않는다.
- save/load 영속 데이터에는 비밀번호 원문을 저장하지 않는다.
- 비밀번호가 제공되지 않을 경우 (null) 빈 문자열로 표시한다.

### 9.3 자동 닫힘 (MUST)
- SSH_LOGIN은 `Volatile=YES` 창이며, 자동 닫힘 규칙은 §7.4를 따른다.

### 9.4 UI 레이아웃(권장)
* **윈도우 타이틀바:** `SSH Login` 표시.
* **헤더 섹션:** 창 최상단 중앙에 `<Host_Name>` (또는 IP)을 큰 텍스트로 배치.
* **데이터 필드 구성 (VBoxContainer 구조):**
  * **User 필드:** "User:" 라벨 + 읽기 전용(ReadOnly) LineEdit.
  * **Passwd 필드:** "Passwd:" 라벨 + 읽기 전용(ReadOnly) LineEdit.
* **입력창 스타일:** 흰색 배경, 검은색 테두리, 내부 텍스트 좌측 정렬.
* **실시간 갱신 로직:** 새로운 SSH 시도(`SshAuthAttempt`) 이벤트 수신 시, 기존 창의 모든 필드 내용을 즉시 덮어쓴다.
  * 이때 자동 닫힘 타이머(3초)는 초기화(Reset)된다.
* **성공/실패 연출 효과(색상/애니메이션)는 알파 범위에서 제외**하고, 결과 메타데이터(`success/failure`, `OK/ERR_*`) 전달까지만 구현한다.

---

## 10. 모드 전환/재생성 절차 (리스크 대응) (MUST)

### 10.1 왜 필요한가
- `embed_subwindows`(임베디드/비임베디드) 전환은 **이미 떠 있는 창에 즉시/일관되게 적용되지 않을 수 있다**.
- 따라서 모드 전환 시 서브 윈도우를 "닫고 재생성"하는 방식이 가장 안정적이다.

### 10.2 절차 (MUST)
모드 전환 또는 메인 창 화면 모드 전환 시, 아래 절차를 따른다.

1) 모든 서브 윈도우에 대해:
   - 지오메트리 저장
   - 각 서브 윈도우의 상태 (State) 데이터 획득. §8.4의 serialize_state()를 호출하여 State를 획득한다.
   - 닫기(hide/queue_free 등 프로젝트 정책)
2) 활성화된 모든 DesktopOverlay 비활성화(§15.5 전환 절차 참조)
3) `embed_subwindows` 설정 변경
4) 메인 창 화면 모드(Display Mode) 설정 변경
5) 새 모드의 레이아웃 로드
6) 열려 있던 모든 서브 윈도우 재오픈
   - 변경된 모드의 저장되어 있는 지오메트리가 있을 경우 지오메트리 복원
   - 각 서브 윈도우의 상태 (State) 데이터 복원. §8.4의 restore_state()를 호출하여 State를 복원한다.
7) 새 모드가 NATIVE_OS이면, 저장된 DesktopOverlay 활성 상태를 복원한다.

* 가상 모드 진입(`MAXIMIZED`)은 아래와 같이 더 세심한 정책을 따른다:
    1. `WINDOW_MODE_WINDOWED`로 1프레임 유지
    2. embed 설정/VirtualDesktopRoot 구성
    3. 그 다음 `MAXIMIZED` 전환
    4. 마지막에 서브윈도우 생성

---

## 11. EXCLUSIVE_FULLSCREEN 상태 제약 (리스크 대응) (MUST)

- `WINDOW_MODE_EXCLUSIVE_FULLSCREEN` 상태에서는:
  - **OS 윈도우 호출(네이티브 파일 다이얼로그, OS 서브윈도우 생성 등)을 금지**한다.
  - 이유: 독점 풀스크린은 OS/드라이버/윈도우 매니저에 따라 "전환/블랙스크린/모드 변화"가 발생할 수 있다.
- 필요한 경우:
  - 인게임 UI로 대체(가상 다이얼로그)
  - 또는 EXCLUSIVE 해제 후 호출(상태 전환 절차는 §10 준수)

---

## 11.5 멀티 윈도우 알파 구현 목표 목록 (중요도 순)
1) 월드 맵 및 네트워크 트레이싱창
2) Network Topology viewer
3) 파일 전송 대기줄(큐/대역폭/실패/재시도)
4) 웹페이지 뷰어(Godot CEF)
5) 프로세서 목록

---

## 12. (추후) 멀티 윈도우 옵션 목록 (중요도 순)
1) 유출된 CCTV 모니터링
2) 패킷 스니핑 창
3) 코딩 에디터 윈도우(탭 기반 다중 문서)

---

## 13. 수용 기준(테스트 시나리오)

### 13.1 모드 전환 및 재생성 (§10)
- NATIVE_OS → VIRTUAL_DESKTOP 전환 시:
  - 모든 서브 윈도우가 닫힌 후 재생성된다.
  - 각 모드의 저장된 지오메트리가 정확히 복원된다.
  - 서브 윈도우의 State가 `serialize_state()` / `restore_state()`를 통해 보존된다 (예: SSH_LOGIN의 표시 데이터, 타이머 잔여 시간).
  - 활성화된 DesktopOverlay가 전환 전에 비활성화된다.
- VIRTUAL_DESKTOP → NATIVE_OS 전환 시에도 동일하게 동작하고, 저장된 DesktopOverlay 상태가 복원된다.
- 전환 중 경과 시간이 Volatile 창 자동 닫힘 타이머에서 차감되지 않는다.
- 가상 모드 진입 시 WINDOWED → embed 설정 → MAXIMIZED → 서브 윈도우 생성 순서가 지켜진다.

### 13.2 네이티브 OS 모드 (§3.2A, §4)
- Primary Core Window가 WINDOWED / MAXIMIZED에서 동작한다.
- FULLSCREEN / EXCLUSIVE_FULLSCREEN 진입 시 자동으로 VIRTUAL_DESKTOP으로 전환된다.
- 서브 윈도우(WindowKind)는 OS 프레임(타이틀바/테두리)을 유지한다. borderless가 아니다.
- Primary Core Window는 최소화 버튼이 활성화되어 있다.
- 알파 구현에서 모든 서브 윈도우(WindowKind)의 최소화 버튼은 비활성화되어 있다.
- resizable=false 창의 최대화 버튼이 비활성화되어 있다.
- Primary Core Window를 이동해도 Secondary 창은 따라 움직이지 않는다.
- TerminalWindow / GuiMainWindow 중 정확히 하나만 Primary Core Window이며, 전면 요청 시 대상 창으로 승격된다.
- 작업표시줄/Alt+Tab 억제는 best effort이며, 실패 시 폴백 정책이 동작한다.
- Secondary 창은 Primary Core Window에 transient으로 종속되어, 게임 종료 시 함께 닫힌다.

### 13.3 가상 데스크톱 모드 (§3.2B, §5)
- 메인 창이 MAXIMIZED 이상 상태를 유지한다. WINDOWED는 금지된다.
- unmaximize 발생 시 즉시 MAXIMIZED로 복귀한다.
- 에뮬레이티드 배경이 표시된다.
- 가상 윈도우들은 커스텀 타이틀바 / 닫기 버튼 / (resizable=true 시) 최대화 버튼으로 동작한다.
- 알파 구현에서 최소화 버튼은 표시되지 않는다.
- usable rect 변경(모니터 전환, 해상도 변경 등) 시 서브 창이 메인 창 밖으로 벗어나지 않도록 클램핑된다. 클램핑 시 크기는 유지되고 위치만 조정된다.

### 13.4 Z-order 및 전면화 (§5.2)
- 서브 창 내부(콘텐츠, 타이틀바, 리사이즈 핸들)를 마우스 클릭하면 해당 창이 Z-order 최상위로 올라간다.
- `open_window(kind)` 호출로 기존 창이 전면화된다.
- Primary Core Window는 모든 서브 창 아래에 위치한다.
- 서브 창 바깥 영역 클릭 시 Primary Core Window가 포커스를 받되, 서브 창의 Z-order는 변경되지 않는다.
- Z-order는 저장/복원 대상이 아니다. 재시작 시 서브 창은 열린 순서대로 쌓인다.

### 13.5 Focus Mode (§2.2-7, §4.5, §5.2-4)

**Passthrough 창:**
- VIRTUAL_DESKTOP: Passthrough 창이 Z-order 최상위여도 키보드 입력이 Primary Core Window에 전달된다. 서브 창에는 키보드 입력이 전달되지 않는다.
- NATIVE_OS: Passthrough 창(`unfocusable=true`)의 드래그/닫기 조작이 Primary Core Window의 포커스를 뺏지 않는다.
- NATIVE_OS: unfocusable이 정상 동작하지 않는 환경에서는 해당 창이 exclusive로 격상되어 동작한다. 포커스 되돌리기(줄다리기)는 발생하지 않는다.

**Exclusive 창:**
- Exclusive 창이 포커스를 가진 동안 키보드 입력이 해당 창에 독점되고 Primary Core Window에 전달되지 않는다.
- 서브 창 바깥 클릭 시 포커스가 Primary Core Window로 반환된다.

**혼합 상황:**
- Exclusive 창과 Passthrough 창이 동시에 열려 있을 때, Passthrough 창이 Z-order 최상위여도 키보드 포커스는 Z-order가 가장 높은 Exclusive 창이 갖는다.
- Exclusive 창이 하나도 없으면 Primary Core Window가 포커스를 갖는다.

### 13.6 자동 포커싱 (§2.2-6)
- autoFocus=true 창은 열릴 때 포커스가 해당 창으로 이동한다.
- autoFocus=false 창은 열릴 때 포커스가 이동하지 않는다 (best effort). Fail-safe로 다음 프레임에 Primary Core Window로 포커스가 복귀한다.

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
- Host, User, Password(원문)가 표시된다. 비밀번호가 null이면 Passwd 필드는 비어 있다.
- 각 시도에 대한 결과(`success/failure`, `OK/ERR_*`) 정보가 SSH_LOGIN으로 전달된다.
- SSH_LOGIN은 `Volatile=YES`이며, 자동 닫힘/유예/타이머 리셋 규칙은 §7.4를 따른다.
- SSH_LOGIN은 Passthrough 모드이며, 창이 떠 있는 동안 Primary Core Window 입력이 방해받지 않는다.
- SSH_LOGIN은 autoFocus=false이며, 열릴 때 Primary Core Window 포커스를 뺏지 않는다.

### 13.9 단일 인스턴스 (§2.2-2)
- 이미 열려 있는 Kind에 대해 `open_window`를 재호출하면 새 창이 생기지 않고, 기존 창이 전면화/포커스되며 내용이 갱신된다.

### 13.10 파일 전송 대기줄 다중 항목 (§2.2-2, §7.3)
- `FILE_TRANSFER_QUEUE` 창은 동시에 1개만 존재한다.
- 파일 전송 작업이 여러 개일 때, 창 내부 목록에 여러 `TransferJob` 항목이 동시에 표시된다.
- `open_window(FILE_TRANSFER_QUEUE)`를 반복 호출해도 새 창은 생기지 않고, 기존 창만 전면화/갱신된다.
- `FILE_TRANSFER_QUEUE`는 `Volatile=YES`이며, 업데이트가 없고 활성 전송 작업 수가 0이면 3초 후 자동으로 닫힌다.
- `FILE_TRANSFER_QUEUE`에서 전송 목록 업데이트(추가/진행률/상태 변화)가 발생하면 자동 닫힘 데드라인이 리셋된다.
- `CODE_EDITOR` 탭 기반 다중 문서 정책은 추후 구현 범위이며, 본 알파 수용 기준에서 제외된다.

### 13.11 EXCLUSIVE_FULLSCREEN 제약 (§11)
- EXCLUSIVE_FULLSCREEN 상태에서 OS 윈도우 호출(네이티브 파일 다이얼로그 등)이 발생하지 않는다.
- 필요한 경우 인게임 UI로 대체되거나, EXCLUSIVE 해제 후 호출된다.

### 13.12 State 직렬화 (§8.4)
- 모든 WindowKind가 `serialize_state()` / `restore_state()`를 구현한다.
- State가 없는 창은 빈 Dictionary를 반환하며, 복원 시 에러가 발생하지 않는다.
- 모드 전환 전후로 State가 정확히 보존된다 (SSH_LOGIN: host, user, password, 타이머 잔여 시간).
- SSH_LOGIN 비밀번호 원문은 같은 프로세스 내 모드 전환/창 재생성에서만 복원 가능하며, save/load 영속 복원 대상이 아니다.
- 모드 전환 전후로 Volatile 창의 자동 닫힘 상태도 보존된다 (예: FILE_TRANSFER_QUEUE의 타이머 잔여 시간, 활성 전송 작업 수).

### 13.13 DesktopOverlay (§15)
- NATIVE_OS 모드에서만 DesktopOverlay를 활성화할 수 있다.
- VIRTUAL_DESKTOP 모드에서 DesktopOverlay 활성화 시도는 무시된다.
- 모니터당 독립적으로 켜고 끌 수 있다.
- 활성화된 DesktopOverlay는 항상 해당 모니터의 모든 게임 창 아래에 위치한다(Z-order 최하단).
- DesktopOverlay는 포커스를 받지 않으며, 클릭 시 Primary Core Window로 포커스가 전달된다.
- 연결이 끊어진 모니터의 DesktopOverlay는 자동 비활성화된다. 재연결 시 저장된 활성 상태가 복원된다.
- NATIVE_OS → VIRTUAL_DESKTOP 전환 시 모든 DesktopOverlay가 비활성화된다.
- 게임 종료 시 모든 DesktopOverlay가 함께 닫힌다.

---

## 14. 참고(엔진 동작 관련)
- Transient 창은 부모와 생명주기가 연결되며, 플랫폼에 따라 동작이 다를 수 있다.
- Fullscreen 진입 시 borderless가 강제로 설정될 수 있으므로, 모드 복귀 시 원복이 필요할 수 있다.
- 임베디드/비임베디드 혼합은 별도의 Viewport 구성 없이는 어렵고, 런타임 전환은 "재생성"이 안전하다.
- Win32 `SetWindowPos(hwnd, HWND_BOTTOM, ...)` 호출은 Godot C# 레이어에서 P/Invoke로 처리한다.

---

## 15. DesktopOverlay (배경 오버레이 창)

### 15.1 개요
- **적용 모드**: NATIVE_OS 전용. VIRTUAL_DESKTOP 모드에서는 비활성 상태를 유지한다.
- **분류**: WindowKind 체계와 별개인 시스템 레이어. WindowManager가 별도 관리한다.
- **목적**: 인게임 옵션(프로그램 형태 포함)으로 켜고 끌 수 있는 borderless 배경 창. 게임 창이 다른 앱 위로 올라갈 일이 없도록 데스크톱을 덮는다.

### 15.2 모니터 단위 관리

- DesktopOverlay는 **모니터 1개당 1개**의 창으로 구성된다.
- 각 창의 UI 표시명은 `background_0`, `background_1`, ... 으로 모니터 인덱스 기반 순서를 따른다.
  - 표시명은 편의용이며 내부 식별 키로 사용하지 않는다.
- **내부 식별 키(모니터 핑거프린트)**: 해상도 + DPI 배율 조합을 사용한다.
  - 모니터 연결 순서(인덱스)가 바뀌어도 동일 모니터를 올바르게 식별하기 위함이다.
  - 핑거프린트 구성은 §2.2-3의 NATIVE_OS 모드 모니터 식별 정보와 동일한 방식을 따른다.
- 모니터가 분리되면 해당 DesktopOverlay는 자동으로 비활성화한다.
- 분리된 모니터가 재연결되면 저장된 활성 상태를 복원한다.

### 15.3 창 속성 (MUST)

- **Borderless**: `true` (OS 프레임 없음)
- **포커스 불가**: `Window.FLAG_NO_FOCUS = true`
  - DesktopOverlay는 어떤 경우에도 포커스를 획득하지 않는다.
- **크기/위치**: 해당 모니터 전체 rect (`screen_get_display_safe_area` 또는 전체 화면 크기)와 동일하게 유지한다. 리사이즈/이동 불가.
- **Z-order**: 생성 직후 Win32 `SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` 1회 호출로 OS Z-order 최하단에 고정한다.
  - 이후 게임의 다른 창들은 별도 처리 없이 자동으로 DesktopOverlay 위에 위치한다.
  - DesktopOverlay가 `FLAG_NO_FOCUS` 상태이므로 Windows가 이 창을 활성화 대상으로 삼지 않아 Z-order가 자동으로 올라가는 현상이 발생하지 않는다.
- **작업표시줄 노출**: 숨김 처리를 권장한다(SHOULD). `transient` 설정 또는 Win32 확장 스타일 조정으로 억제한다.
- **생명주기**: 게임 종료 시 자동으로 닫힌다.

### 15.4 토글 동작 (MUST)

```
[켜기]
1. 해당 모니터 핑거프린트에 대응하는 DesktopOverlay 창 생성/show
2. Win32 SetWindowPos(HWND_BOTTOM) 1회 호출
3. 완료 — 다른 게임 창은 건드리지 않음

[끄기]
1. 해당 DesktopOverlay 창 hide (또는 queue_free)
2. 완료
```

- 토글 시 다른 창을 순회하거나 Z-order를 재조정하지 않는다.
- 배경을 껐다가 다시 켜도 위 절차만 반복한다.

### 15.5 모드 전환 시 처리 (MUST)

- NATIVE_OS → VIRTUAL_DESKTOP 전환 시:
  1. 활성화된 모든 DesktopOverlay를 비활성화(hide/queue_free)한다.
  2. 각 모니터 핑거프린트별 활성 상태를 메모리에 보존한다(전환 중에만 유지).
  3. VIRTUAL_DESKTOP으로 전환이 완료된다.
- VIRTUAL_DESKTOP → NATIVE_OS 전환 시:
  1. NATIVE_OS 전환이 완료된 후, 보존된 활성 상태를 기반으로 DesktopOverlay를 복원한다.
  2. 저장된 핑거프린트에 해당하는 모니터가 현재 연결되어 있는 경우에만 복원한다.

### 15.6 클릭 처리 (MUST)

- DesktopOverlay 영역을 클릭하면 포커스가 Primary Core Window로 전달된다.
- `FLAG_NO_FOCUS` 설정으로 인해 클릭이 OS 레벨에서 DesktopOverlay에 흡수되지 않고 하위 창(또는 바탕화면)으로 통과되는 경우, 별도 처리 없이 그대로 허용한다.
  - 단, 클릭이 게임 외부 창(실제 바탕화면이나 다른 앱)으로 전달되지 않도록 주의한다. 문제가 발생하면 마우스 이벤트를 명시적으로 캡처해 Primary Core Window로 전달하는 방식으로 보완한다(SHOULD).

### 15.7 구현 참고 (Win32 P/Invoke)

```csharp
[DllImport("user32.dll")]
static extern bool SetWindowPos(
    IntPtr hWnd, IntPtr hWndInsertAfter,
    int x, int y, int cx, int cy, uint uFlags);

static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
const uint SWP_NOMOVE    = 0x0002;
const uint SWP_NOSIZE    = 0x0001;
const uint SWP_NOACTIVATE = 0x0010;

void PinToBottom(int godotWindowId) {
    var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(
        DisplayServer.HandleType.WindowHandle, godotWindowId);
    SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
```

### 15.8 저장/복원

- DesktopOverlay의 on/off 상태 저장 정책은 별도로 결정한다(미확정).
- 저장 정책이 확정될 때까지 WindowManager는 런타임 중 상태를 메모리에만 유지한다.
