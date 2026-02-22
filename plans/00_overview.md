# 프로젝트 개요 (문서 세트)

이 문서 세트는 **터미널 기반 코딩-해킹 인디 게임**을 만들 때, 구현/기획 판단을 빠르게 내릴 수 있도록 핵심 설계를 주제별로 분리해 정리한 것입니다.

- 엔진: Godot(PC) 가정  
  - (05 문서는 Unity 레거시 참고용, 나머지는 Godot 전제)
- UI: 기본은 **터미널/텍스트 중심**
- 핵심 차별점: 유저가 “툴(프로그램)”을 **MiniScript로 수정/개선/자동화**하여 공략 루트를 만들어냄
- 해킹 대상: **현실 시스템이 아닌, 게임 내 가상 네트워크/가상 OS/가상 서비스**
- 안전 원칙: 실제 공격 재현이 아니라, **전략·관찰·추론**을 중심으로 “현실 보안 개념”을 게임적으로 추상화

---

## 이 문서 세트 구성

1) **01_existing_hacking_games.md**  
   - Uplink/Hacknet/Bitburner 등 기존 장르 레퍼런스에서 *가져올 것/피할 것* 정리  
   - “왜 이 장르가 재밌는지”와 “어떤 UX/게임 루프가 검증되었는지” 요약

2) **02_miniscript_interpreter_and_constraints.md**  
   - Lua 대신 MiniScript 채택 이유, **Godot(C#) 임베딩/샌드박스 실행 모델**  
   - **CPU(실행 예산)/RAM(논리 메모리)** 제약, 업그레이드 설계  
   - 실행 타임슬라이스, wait/yield, 샌드박싱, 메모리 추정/예약(Reservation) 전략

3) **03_game_api_modules.md**  
   - 유저 스크립트가 접근하는 **샌드박스 API 모듈**(fs/net/http/auth/crypto/agent/monitor 등) 정리  
   - 권한 모델, 비용(시간/트레이스) 모델, 취약점 토글(콘텐츠 제작용)까지 포함

4) **04_attack_routes_and_missions.md**  
   - 너가 정리한 공격 루트를 기반으로, 각 루트의 **미션 템플릿**(단서→준비→위험→보상→교훈) 정리  
   - “현실감은 살리되 따라 하기 어렵게” 만들기 위한 추상화 원칙 포함

5) **05_ui_terminal_prototype.md** (레거시)
   - Unity 전제의 초기 터미널 UI 기획 문서(참고용 유지)
   - 최신 Godot 구현 스펙은 07 문서를 기준으로 함

6) **06_server_nodes_design_v0.md**
   - 프로토타입 v0의 **서버 노드/클러스터 설정**(쉬움/중간/어려움 v0) 정리  
   - IP/포트/서비스/계정/초기 파일시스템/토큰(OTP·unlock) 레지스트리 등 “시나리오 실행에 필요한 최소 데이터” 포함

7) **07_ui_terminal_prototype_godot.md**
   - Godot(PC) 기반 **터미널 UI + 코드 에디터 오버레이** 기획  
   - 씬/노드 트리, 입력 이벤트(히스토리/자동완성/클릭 링크), MVP 명령어 세트, DoD(성공 기준) 포함

8) **08_vfs_overlay_design_v0.md**
   - 서버 수가 커져도 확장 가능한 **VFS(Base + Overlay + Tombstone + BlobStore)** 설계  
   - 서버별 파일 추가/수정/삭제(기본 파일 삭제 포함)와 런타임 파일 조작 규칙을 정의

9) **09_server_node_runtime_schema_v0.md**
   - 월드 런타임의 **서버/프로세스/세션/디스크 오버레이** 등 핵심 상태를 담는 스키마(v0)  
   - `serverList(nodeId key)` + `ipIndex` + `processList` 단일 진실 원칙, reboot/프로세스 완료 규칙, subnet/exposure 런타임 모델 포함

10) **10_blueprint_schema_v0.md**
   - 콘텐츠 데이터를 정의하는 **Blueprint 스키마(v0)** 문서  
   - `ServerSpec / Scenario / Campaign` 구조, `interfaces/subnetTopology/events` 규칙, overlay 병합/검증 규칙 포함

11) **11_event_handler_spec_v0_1.md**
   - 전역 이벤트 큐 기반 **Event Handler 시스템(v0.1)** 문서  
   - `processFinished/privilegeAcquire/fileAcquire` 처리, 인덱싱 디스패치, `guardContent(MiniScript)` 실행 예산/오류 정책 포함


---

## 핵심 디자인 결정(현재까지)

- **언어**: MiniScript 기반 (유저가 배우기 쉽고, 임베드/샌드박싱 구조가 명확)  
- **툴 = 스크립트 파일**: 인게임 상점/드랍/도난으로 얻은 “프로그램”을 유저가 편집해 강화  
- **자원 제약**:  
  - CPU = 프레임당 실행 예산(또는 명령어 fuel)  
  - RAM = 논리 메모리(문자열/리스트/맵 규모 기반) + 실행 전 **예약(Reservation)** 모델
- **보안 개념 학습**: 공격 성공 시 “왜 뚫렸는지 / 어떻게 막는지” 피드백 제공
- **탐지/긴장감**: 로깅/모니터링 시스템 + Trace 게이지(소음/은밀함 트레이드오프)
- **디스크 모델**: 공통 BaseFS + 서버별 OverlayFS(tombstone 포함) + BlobStore 중복 제거(08 문서)
- **런타임 스키마**: `serverList(nodeId key)`/`ipIndex`/`processList` 중심(단일 진실) + subnet/exposure 상태 캐시 + reboot/프로세스 완료 규칙(09 문서)
- **블루프린트 스키마**: `ServerSpec/Scenario/Campaign` + 인터페이스/IP/토폴로지/이벤트 데이터 계약(10 문서)
- **이벤트 시스템**: condition 인덱싱 + once-only 디스패치 + MiniScript guard(`guardContent`) + tick 예산 기반 실행(11 문서)

---

## 용어(간단 사전)

- **프로그램/툴**: 유저가 실행하거나 스크립트로 조합하는 “해킹 도구” (MiniScript 코드 파일)
- **가상 호스트(host)**: 게임 월드의 서버/PC/라우터 등 네트워크 노드
- **서비스(service)**: 호스트의 포트/프로토콜/웹앱/DB/에이전트 같은 공격 표면 단위
- **세션(session)**: 인증 후 획득하는 권한 토큰/핸들(권한·추적에 영향을 줌)
- **Trace(추적 게이지)**: 공격 시도가 남기는 소음/로그 축적치. 특정 임계치에서 경보/차단/게임오버 이벤트 가능

---

## 앞으로 빠지기 쉬운 “추가로 필요한 설계 항목”(체크리스트)

이건 현재 파일들에 일부 언급되지만, 실제 제작에 들어가면 **별도 문서/스프레드시트로 관리**하는 게 좋아요.

- **게임 루프/경제**: 계약/보상/상점/업그레이드 비용/리스크 보상 곡선
- **콘텐츠 제작 파이프라인**: 서버/미션/취약점 토글 데이터를 어떻게 찍어낼지(에디터 툴, JSON/Godot Resource(.tres/.res))
- **UX**: 터미널 UI, 코드 에디터(자동완성/오류 표시), 디버깅(로그/스택/프로파일)
- **안전장치**: 무한루프/메모리 폭주/크래시 방지, 스크립트 강제 중단(Ctrl+C), 보호된 API 경계
- **밸런싱**: 자동화가 게임을 “혼자 돌리는” 상태로 가지 않게 하는 장치(수동 퍼즐/이벤트/탐지 강화)
- **세이브/로드/리플레이**: 미션 상태, 서버 상태, 유저 스크립트 버전 관리
- **튜토리얼/학습곡선**: 초반 30분에 코딩 경험 없는 유저도 “성공 경험”을 얻게 만들기
- **난이도 조절**: 공격 난이도/방어 강도/로그 감도/시간 제한을 데이터로 튜닝 가능하게
- **법/윤리 톤**: 실제 해킹 조장으로 보이지 않게 ‘시뮬레이션/교육’ 톤을 유지(페이로드/실전 절차는 추상화)

---

## 참고 링크(원문)
```text
MiniScript Manual: https://miniscript.org/files/MiniScript-Manual.pdf
MiniScript Integration Guide (Unity 예시, C# 임베딩 참고): https://miniscript.org/files/MiniScript-Integration-Guide.pdf
OWASP Top 10:2021: https://owasp.org/Top10/2021/
OWASP Top 10 for LLM Apps: https://owasp.org/www-project-top-10-for-large-language-model-applications/
Hacknet (Steam): https://store.steampowered.com/app/365450/Hacknet/
Uplink (Steam): https://store.steampowered.com/app/1510/Uplink/
Bitburner (Steam): https://store.steampowered.com/app/1812820/Bitburner/
MITRE ATT&CK T1566 Phishing: https://attack.mitre.org/techniques/T1566/
NIST PQC (Aug 2024): https://www.nist.gov/news-events/news/2024/08/nist-releases-first-3-finalized-post-quantum-encryption-standards
Godot RichTextLabel: https://docs.godotengine.org/en/stable/classes/class_richtextlabel.html
Godot LineEdit: https://docs.godotengine.org/en/stable/classes/class_lineedit.html
Godot CodeEdit: https://docs.godotengine.org/en/stable/classes/class_codeedit.html

```
