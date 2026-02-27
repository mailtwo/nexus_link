# TODO.md — Deferred Plan Items

plans/ 문서에 정의되어 있으나, 현재 기준으로 명시적으로 보류된 항목만 기록한다.

## Open

- [03] `import` 알파 미구현 항목 구현: `.scripts_registry` 탐색, `reload/importReload` (`plans/03_game_api_modules.md` §8.3, §8.2 alpha 구현 필요 사항).
- [04] Trace/경보/대응 루프 알파 구현 (`plans/04_attack_routes_and_missions.md` §6, line 9 기준).
- [04] 힌트 시스템 상세 스펙 확정 (`plans/04_attack_routes_and_missions.md` §7은 ideation 단계).
- [07] 실시간 Trace/추적 대응 루프 UI 구현 (`plans/07_ui_terminal_prototype_godot.md` line 5, See DOCS_INDEX.md -> 13 연계).
- [07] 프로토타입 임시 `save/load` 명령 제거(알파 제거 대상 반영) (`plans/07_ui_terminal_prototype_godot.md` §6.5).
- [10] 동적 생성 Pass(알파) 구현: 서버 수/배치, topology, 이벤트, 초기 데이터, 계정/자격정보를 `worldSeed` 결정적 생성으로 확정 (`plans/10_blueprint_schema_v0.md` §7 2.5).
- [10] Campaign `flowMeta` YAML 작성/로더 초기화 매핑 구현 (`plans/10_blueprint_schema_v0.md` §5, 상세 스테이지 설계는 See DOCS_INDEX.md -> 15).
- [11] Action -> Event emit reentrancy 구현: tail append + tick budget 기반 같은 tick 처리/이월 (`plans/11_event_handler_spec_v0_1.md` §7.3).
- [12] Save/Load 엔진 API + 저장/로드 UI 연동 구현 (`plans/12_save_load_persistence_spec_v0_1.md` line 9, §8.1).
- [13] DesktopOverlay 상태 영속 저장 정책 확정 및 Save/Load 연계 (`plans/13_multi_window_engine_contract_v1.md` §15.8, See DOCS_INDEX.md -> 12).
- [13] 알파 구현 목표 창 6종 WindowKind 속성 정의/레지스트리 확장 (`plans/13_multi_window_engine_contract_v1.md` §7.2, line 260 메모).
- [13] 멀티 윈도우 알파 구현 목표 창 구현(우선순위): 월드 맵+네트워크 트레이싱, topology viewer, 파일 전송 대기줄, 웹페이지 뷰어(CEF), 코딩 에디터, 프로세서 목록 (`plans/13_multi_window_engine_contract_v1.md` §11.5).
- [15] README 이후 첫 미션 연결 흐름 확정 (`plans/15_game_flow_design.md` §1.2 미결).
- [15] 튜토리얼 구조 결정: 별도 튜토리얼 미션 vs 첫 정식 미션 내 통합 (`plans/15_game_flow_design.md` §1.3 미결).
- [15] 코딩 필요성 노출 섹션 상세 설계 (`plans/15_game_flow_design.md` §2 방향성만).
- [15] 중반 흐름 유지 섹션 상세 설계 (`plans/15_game_flow_design.md` §3 방향성만).
- [15] 최종 미션 유도 섹션 상세 설계 (`plans/15_game_flow_design.md` §4 방향성만).
