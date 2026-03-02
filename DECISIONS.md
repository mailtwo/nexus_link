# DECISIONS.md — 설계 결정 이력

plans/ 문서의 설계 결정이 추가되거나 변경될 때 기록한다.
코드 변경 이력은 git commit을 참고한다.

---

## 2025-02-26

### [13] DesktopOverlay — WindowKind 체계 분리
- **결정**: DesktopOverlay는 WindowKind 체계 바깥의 별도 시스템 레이어로 분리한다.
- **이유**: WindowKind는 두 모드(NATIVE_OS/VIRTUAL_DESKTOP) 모두 지원 의무가 있으나,
  DesktopOverlay는 NATIVE_OS 전용이라 체계 내 포함 시 §2.2-1 충돌 발생.

### [13] DesktopOverlay — Z-order 전략
- **결정**: 생성 시 Win32 `SetWindowPos(HWND_BOTTOM)` 1회 호출로 바닥 고정.
  이후 다른 게임 창은 별도 처리 없이 자동으로 위에 위치.
- **이유**: 토글 시마다 모든 창을 순회하는 방식은 창이 많아질수록 비용 증가.
  `FLAG_NO_FOCUS` 조합으로 Z-order가 자동으로 올라가는 현상도 방지됨.

### [13] DesktopOverlay — 모니터 식별
- **결정**: 모니터 핑거프린트(해상도 + DPI 배율 조합)를 내부 식별 키로 사용.
  UI 표시명은 `background_0`, `background_1` 등 인덱스 기반.
- **이유**: 모니터 연결 순서(인덱스)는 변경될 수 있어 식별자로 부적합.

### [13] 메인 창 구조 — 터미널을 서브 윈도우로 분리 (ideation)
- **결정**: 확정 아님. 장기적으로 메인 창을 투명 호스트로 두고,
  터미널/GUI 메인화면을 모두 서브 윈도우로 다루는 구조를 검토 중.
- **이유**: 게임 중반 이후 GUI 메인화면 추가 시 "메인 창 = 터미널" 가정이
  구조적으로 깨지는 문제를 사전에 방지.
- **현재 상태**: ideation 단계. 확정 시 13 문서 대규모 수정 필요.

### [03] import 라이브러리 계약 — @name 강제
- **결정**: 파일 최상단 연속 주석 블록에 `@name`이 있는 파일만 import 가능.
  `@name` 없는 파일 import 시도 시 `ERR_NOT_A_LIBRARY` 반환.
- **이유**: 실행용 스크립트와 라이브러리 스크립트를 구조적으로 구분.
  규칙이 단순해서 "import하고 싶으면 @name 달아라" 한 줄로 설명 가능.

### [14] scripts 프로그램 — config 서브커맨드
- **결정**: `scripts move` 대신 `scripts config`로 `.scripts_registry`를
  edit systemcall로 열어준다.
- **이유**: 순서 조정뿐 아니라 추가/삭제도 파일 직접 편집으로 가능해서
  전용 커맨드 여러 개 만드는 것보다 단순하고 강력함.
  `git config --edit`과 동일한 패턴으로 직관적.

### [14] scripts 프로그램 — 디렉토리 등록 flat 정책
- **결정**: `scripts add <dir>` 시 해당 디렉토리만 탐색(비재귀, flat).
- **이유**: LD_LIBRARY_PATH와 동일한 관례. 재귀 허용 시 탐색 범위 예측 불가.

### [04] 힌트 시스템 — LLM 미사용
- **결정**: hint agent는 LLM이 아닌 룰 테이블 기반으로 구현한다.
- **이유**: Qwen2.5-0.5B는 §3.3 간접 프롬프트 인젝션 미션의 공격 대상 NPC 전용.
  힌트용으로 쓰면 "공격 대상 LLM"과 "도움 주는 LLM" 역할이 혼재되어
  플레이어가 힌트 에이전트를 공격하려 드는 등 혼란 발생 가능.

### [15] 게임 시작 연출 — 타이핑 애니메이션
- **결정**: 최초 부팅 시 터미널에 `cat /home/<userId>/README` 명령어가
  한 글자씩 타이핑되는 애니메이션을 보여주고, 플레이어는 엔터만 치면 된다.
  최초 1회만 표시.
- **이유**: MOTD "help 쳐보세요"는 수동적이라 유저가 읽지 않을 수 있음.
  타이핑 애니메이션이 "지금 입력 중" 신호를 시각적으로 전달해
  엔터에 손이 가도록 자연스럽게 유도.

### [15] NEXUS 워크스테이션 이름 고정
- **결정**: 워크스테이션 이름은 `nexus`로 고정. 변경 불가.
- **이유**: 이전 주인의 흔적으로 스토리 훅과 연계됨(§4.1).
  이름 변경 기능을 구현하지 않아도 되는 실용적 이점도 있음.

### [15] MOTD "마지막 로그인" — 스토리 훅 연계
- **결정**: 최초 부팅 MOTD의 "마지막 로그인" 시간을 약 6개월 전으로 표시.
- **이유**: §4.1 NEXUS 스토리 훅과 연계. 이전 주인이 사라진 시점을
  암시하는 첫 번째 단서로 기능. 구체적 시간은 기획 확정 후 조정 예정.

## 2026-02-26

### [04] 힌트 시스템 — 플레이어 능동 요청 기반
- **결정**: hint agent는 플레이어가 메일로 요청했을 때만 동작한다.
  게임이 먼저 힌트를 제시하지 않는다.
- **이유**: 퍼즐 주도권을 플레이어에게 유지하고, 막혔을 때만 도움을 받는
  구조로 난이도 체감을 조절하기 위함.

### [04] 힌트 라이브러리 — 온디맨드 첨부
- **결정**: 힌트는 import 가능한 stdlib 스크립트(`@name` 계약)로 답장에 첨부한다.
  게임 시작 시 기본 제공하지 않는다.
- **이유**: 초기 난이도 붕괴를 막고, "질문 -> 도구 획득 -> 적용" 학습 루프를
  플레이 흐름에 포함하기 위함.

### [13] DesktopOverlay — 모드 제한 및 전환 규칙
- **결정**: DesktopOverlay는 NATIVE_OS 모드에서만 활성화한다.
  VIRTUAL_DESKTOP 전환 시 즉시 비활성화하고, NATIVE_OS 복귀 시 저장된 상태를 복원한다.
- **이유**: 가상 데스크톱 배경과 역할이 중복되며, 모드 전환 시 Z-order/포커스
  충돌을 방지하기 위함.

### [13] DesktopOverlay — 포커스 비선점
- **결정**: DesktopOverlay는 `FLAG_NO_FOCUS`를 전제로 포커스를 획득하지 않으며,
  입력 포커스는 메인 터미널 창을 우선한다.
- **이유**: 배경창이 키보드 입력을 훔치면 핵심 플레이(터미널 조작)가 즉시 저해됨.

### [13] DesktopOverlay 저장 정책 — 임시 메모리 유지
- **결정**: 영속 저장 정책 확정 전까지 DesktopOverlay on/off 상태는 런타임 메모리에만 유지한다.
- **이유**: 저장 규격(See DOCS_INDEX.md -> 12)과의 경계가 확정되기 전, 구현 충돌을 피하기 위함.

### [15] 타겟 플레이어 범위 확정
- **결정**: 주 타겟은 코딩 경험자, 보조 타겟은 초보 코더로 둔다.
  코딩에 관심이 없는 플레이어는 타겟에서 제외한다.
- **이유**: 코딩 유도형 게임 정체성을 유지하고 온보딩/미션 설계 기준을 명확히 하기 위함.

### [03] import 사용자 문서 표기 예외
- **결정**: DocFX 사용자 문서에서 `import`는 모듈 형식(`Module ...`)으로 표기하지 않고,
  독립 intrinsic 이름인 `import`로 표기한다.
- **이유**: `import`는 `ssh/fs/net`처럼 모듈 map이 아니라 MiniScript 전역 intrinsic 함수이므로,
  모듈 표기를 쓰면 API 성격이 왜곡된다.

### [03] import 타입맵 동기화 정책
- **결정**: `import` 성공 실행 후 child VM의 `listType/mapType/stringType` 변경을 caller VM으로 동기화한다.
  충돌은 **나중 import 우선(덮어쓰기)** 으로 처리하고, 실패한 import 결과는 반영하지 않는다.
- **이유**: `listUtil/mapUtil/stringUtil`처럼 타입 메서드를 패치하는 라이브러리의 변경이
  호출자 스크립트에서 즉시 보이도록 보장하면서, 실패 경로의 부분 반영으로 인한 상태 오염을 방지하기 위함.

### [07] ping async 실행 정책
- **결정**: `ping`은 터미널 프로그램 async 실행 경로로 편입해 실시간 probe 출력을 스트리밍하고,
  실행 중에는 miniscript와 동일하게 입력 제출을 차단하며 `Ctrl+C`로 즉시 중단할 수 있게 한다.
  기존 동기 `ExecuteTerminalCommand("ping ...")` 경로는 호환성을 위해 유지한다.
- **이유**: 동기 `ping` 실행으로 인한 프리즈를 제거하고, 긴 실행 커맨드의 UX를 miniscript와 동일한
  단일 규칙으로 통일하기 위함이다.

## 2026-02-27

### [13] 알파 메인 창 정책 — CoreWindow Primary 승격
- **결정**: 알파에서는 `TerminalWindow`/`GuiMainWindow` 중 정확히 하나를 Primary Core Window로 운영한다.
  사용자가 전면화 요청한 코어 창을 Primary로 승격하고, 현재 Primary가 닫히면 남은 코어 창을 즉시 승격한다.
- **이유**: "메인 창 = 터미널" 고정 가정을 제거하면서도, 숨은 Host 창 방식 없이 빠르게 안정 구현하기 위함.

### [13] 알파 노출 정책 — Secondary 창 Alt+Tab/작업표시줄 억제
- **결정**: Primary를 제외한 Secondary 창(다른 코어 창 + WindowKind 창)은 `transient(primary)`와 플랫폼 스타일로
  Alt+Tab/작업표시줄 노출 억제를 적용한다(Best effort).
- **이유**: 사용자 체감상 대표 엔트리를 단순화하면서, 플랫폼 제약으로 인한 실패 가능성은 계약에 명시해 리스크를 관리하기 위함.

### [13] 베타 준비 — Hidden Host 미구현, Host-ready 경계 선반영
- **결정**: 알파에서는 1x1/오프스크린 Hidden Host 창을 구현하지 않는다.
  대신 창 역할 분리, 플랫폼 어댑터 격리, 입력 라우팅 추상화 등 Host-ready 경계를 계약에 강제한다.
- **이유**: 알파 복잡도를 억제하면서도, 베타에서 Host Window 구조로 이행할 때 재개발 비용을 줄이기 위함.

### [13] 단일 인스턴스와 다중 항목 분리 — 파일 전송 큐
- **결정**: `FILE_TRANSFER_QUEUE`는 WindowKind 창 인스턴스 1개를 유지하되, 창 내부 전송 항목(`TransferJob`)은 다중(N개) 허용한다.
  `open_window(FILE_TRANSFER_QUEUE)` 재호출 시 새 창을 만들지 않고 기존 창을 전면화/갱신한다.
- **이유**: WindowKind 단일 인스턴스 규칙을 유지하면서, 다중 파일 전송 UX 요구를 충족하기 위함.

### [13] 코딩 에디터 다중 문서 정책 — 알파 제외, 추후 구현
- **결정**: 코딩 에디터는 단일 창 + 탭 기반 다중 문서 모델을 채택하되, 알파 구현 목표에서는 제외하고 추후 구현한다.
- **이유**: 파일 전송 큐는 알파에서 즉시 필요하지만, 코드 에디터 탭 시스템은 후속 구현으로 분리해 알파 범위를 관리하기 위함.

### [13] SSH_LOGIN 비밀번호 표시 정책 — 원문 표시(런타임 메모리 한정)
- **결정**: SSH_LOGIN의 Passwd 필드는 `****` 마스킹이 아니라 실제 시도 비밀번호 원문을 표시한다.
  단, 원문은 런타임 메모리 범위에서만 유지하고, 이벤트/로그/save-load 영속 데이터에는 저장하지 않는다.
- **이유**: SSH 시도 연출의 가시성을 높이면서도, 영속 저장이나 로그를 통한 불필요한 노출 리스크를 제한하기 위함.

## 2026-02-28

### [13] Minimizable 플래그 도입 + Primary 최소화 허용
- **결정**: WindowKind 속성에 `Minimizable` 플래그를 도입하고, Primary Core Window는 최소화를 허용한다.
  `WEB_VIEWER`, `CODE_EDITOR`는 `Minimizable=YES`로 예약하며, 알파 구현에서는 서브창 최소화 동작을 비활성화한다.
- **이유**: 추후 "메인 1x1 + 다중 서브창" 구조 확장을 대비하면서, 알파 범위를 통제하고 사용자 최소화 진입점을 보존하기 위함.

## 2026-03-02

### [13] WORLD_MAP_TRACE 기준 해상도 갱신
- **결정**: WORLD_MAP_TRACE 기준 월드맵 원본 크기를 `2048×1024`, 작게 보기 map viewport를 `512×256`, 작게 보기 기본 창 크기를 `556×300`으로 갱신한다.
- **이유**: 월드맵 원본/축소 에셋 리사이즈(원본 2048×1024, min 512×256)에 맞춰 창 지오메트리 계약과 스케일 계산 기준을 일치시키기 위함.

### [10] RegionData 경로/로딩 정책
- **결정**: RegionData 기본 경로를 `res://scenario_content/campaigns/base/regions.yaml`로 고정하고, 월드 런타임에서 1회 로딩 + 전처리(`TotalArea` 계산/정렬 캐시) 후 불변 캐시로 재사용한다.
- **이유**: BlueprintLocationInfo 도입 전 공용 지역 인덱스를 안정적으로 준비하고, save/load와 무관한 정적 데이터의 재직렬화 비용/복잡도를 제거하기 위함.

### [10] ServerSpec location 스키마 스칼라화
- **결정**: `ServerSpec.location`은 중첩 객체(`location.location`)가 아닌 단일 scalar 문자열로 정의한다. 허용 입력은 `AUTO:<regionId>`, `<lat>,<lng>`, 생략(= `AUTO:Unknown`)으로 고정한다.
- **이유**: 실제 프로토타입 YAML 작성 패턴과 스키마 표현을 일치시켜 작성/리뷰 혼선을 줄이고, location 키 중복 표기를 제거하기 위함.

### [12] Save/Load 버전 정책 갱신 (location runtime 승격 반영)
- **결정**: save 컨테이너 기본 버전을 `FormatMinor 0 -> 1`, `saveSchemaVersion "0.1" -> "0.2"`로 상향한다.
- **이유**: 서버 location 런타임 데이터를 영속화 범위에 추가하는 하위 호환 확장을 명시적으로 구분하기 위함.

### [12] 구버전 save 로드 정책
- **결정**: `FormatMinor < 1` save 파일은 로드를 허용하지 않고 `UnsupportedVersion`으로 실패 처리한다.
- **이유**: location 필드 없는 save를 암묵 복원하면 월드 좌표 일관성/검증 규칙이 깨질 수 있어, 명시적 실패 정책으로 데이터 계약을 고정하기 위함.

### [12] ServerState location 저장 범위
- **결정**: save에는 `regionId`, `lat`, `lng`만 저장하고 `displayName`은 저장하지 않는다. 로드 시 `lat/lng` 기반으로 `displayName`을 재계산한다.
- **이유**: displayName은 RegionData 전처리 결과(TotalArea 최소 포함 region)에 의해 파생되는 값이므로 중복 저장을 피하고 로드 시 일관된 파생 규칙을 유지하기 위함.

### [09][10][12] Server icon 런타임/영속화 확장 (minor 고정)
- **결정**: ServerStruct에 `icon` 런타임 필드(`iconType`, `haloType`)를 추가하고, save/load에 영속화한다.
  블루프린트에서는 icon 입력을 받지 않고 월드 생성 시 기본값(`circle`, `none`)으로 초기화한다.
  로드 시 `icon` 필드가 누락되면 동일 기본값으로 복구한다.
- **이유**: WORLD_MAP_TRACE 아이콘 렌더 계약에 필요한 최소 런타임 데이터를 확보하면서,
  기존 save 포맷 호환성을 유지하기 위해 `FormatMinor=1`을 유지하고 optional 필드 확장으로 처리하기 위함.

### [13] WORLD_MAP_TRACE 1차 아이콘 표시 범위 제한
- **결정**: WORLD_MAP_TRACE `map` 탭 1차 구현에서는 표시 대상을 `PlayerWorkstationServer + KnownNodesByNet["internet"]`로 제한하고,
  trace/SSH 마커/라벨은 렌더링에서 제외한다.
- **이유**: `known` 명령의 public 인지 범위(`internet`)와 표시 기준을 맞추고, 과도한 정보 노출 없이 아이콘 렌더 파이프라인을 단계적으로 도입하기 위함.

## 2026-03-03

### [09][11] SSH SessionKey 고정 규칙
- **결정**: SSH 세션 lineage 식별 키를 `(targetNodeId, sessionId)`로 고정한다.
- **이유**: incident origin 서버(E) 기준 역추적 시작점을 안정적으로 확보하고,
  `parentSessionKey` 체인 복원과 `byTargetNodeId` 인덱스 탐색을 일관되게 만들기 위함.

### [09][11] Session history 보존 + active 인덱스 분리
- **결정**: disconnect 시 세션 history를 즉시 삭제하지 않고 `closedAt`만 기록하며,
  active 인덱스(`activeSessionKeys`, `activeByTargetNodeId`)에서만 제거한다.
- **이유**: forensic TTL/중첩 trace/경로 스냅샷 참조를 유지하면서,
  live 세션 판정은 active 인덱스로 분리해 단순화하기 위함.

### [04][11] Forensic 시작 시점 및 route disconnect 판정
- **결정**: forensic는 incident 탐지와 분리해 disconnect/handoff 시점에 시작한다.
  같은 체인에서는 Hot Trace와 forensic 동시 진행을 금지하며, hot 종료 후 forensic를 시작한다.
  `ssh.disconnect(route)`는 닫힌 각 session별 incident를 조회해 forensic를 개별 생성한다.
- **이유**: 즉시 동시 추적으로 인한 과도한 처벌/가독성 저하를 막고,
  다중 hop 종료 시 실제 닫힌 세션 단위 책임 추적을 일관되게 유지하기 위함.

### [12] Session lineage/forensic 영속화 deferred
- **결정**: v0.1 save/load에서는 session lineage/forensic runtime state를 상세 설계하지 않고 deferred로 남긴다.
- **이유**: 현재 포맷 안정성을 유지하면서도, save/load 악용(trace reset) 가능성은 후속 버전에서
  별도 chunk/schema/version 정책으로 명시적으로 해결하기 위함.

### [09][11] Session lineage TTL 정리 범위/순서
- **결정**: TTL 정리는 `ForensicTraceStore -> ForensicIncidentBufferStore -> SessionHistoryStore` 순으로 수행하고,
  적용 범위는 세 저장소 전체(`Forensic+Incident+History`)로 고정한다.
  history는 `active`, `incident`, `forensic origin` 및 `parentSessionKey` 조상 체인을 보호한 뒤 만료분만 제거한다.
- **이유**: 메모리 증가를 제어하면서도, 활성 체인/진행 중 forensic가 참조하는 lineage가 TTL 정리로 끊기는 문제를 방지하기 위함.

### [11] Disconnect handoff incident 소비 규칙
- **결정**: forensic handoff는 닫힌 `sessionKey`에 incident buffer가 있을 때만 수행하고, handoff 직후 해당 incident buffer를 소비(삭제)한다.
- **이유**: incident 없는 disconnect에서 불필요한 forensic 엔트리 생성을 막고, 동일 세션의 중복 handoff를 방지하기 위함.

### [11] Session lineage TTL 기본값
- **결정**: session lineage/forensic TTL 기준은 `worldTimeMs` 5분(`300000ms`)으로 둔다.
- **이유**: 알파 밸런싱 단계에서 단일 상수로 빠르게 조정 가능하게 하면서도, 기본 추적 잔상 지속 시간을 확보하기 위함.
