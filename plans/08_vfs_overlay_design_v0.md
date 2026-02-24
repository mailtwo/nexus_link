# 가상 파일시스템(VFS) 설계 문서 (서버 노드 로컬 디스크 / v0)

목적: 서버 노드가 200개+로 늘어나는 상황에서, 공통 기본 파일을 공유하면서도  
서버별 파일 추가/수정/삭제(기본 파일 삭제 포함) 및 유저 런타임 파일 조작을 지원하는 VFS 구조를 정의한다.  
이 문서만 보고 Codex가 C#로 구현을 시작할 수 있게, **구현 규칙 중심**으로 정리한다.

전제:
- 엔진: Godot(PC), 로직은 C#
- 실제 OS/디스크 접근 없음(전부 가상)
- 심볼릭 링크: **미구현**
- “현재 디렉토리 삭제” 엣지 케이스: **삭제 불가**(rmdir . → 에러)

---

## 1) 요구사항 요약

- 서버 노드 수: 200개 이상(최종적으로 더 증가 가능)
- 모든 서버는 공통 기본 파일 트리(BaseFS)를 가진다(예: `system.bin`, `/bin/*`, `/etc/*`)
- 서버별로:
  - 파일이 없을 수도 있고,
  - 1~2개만 다를 수도 있고,
  - 특정 서버는 파일이 많을 수도 있음(100개+)
- 유저가 런타임에 파일을 업로드/다운로드/수정/삭제해야 한다.
- 기본 파일(`system.bin` 등)도 서버별로 삭제 가능해야 한다.

---

## 2) 전체 구조(권장): Base + Overlay + DirDelta + BlobStore

### 2.1 BaseFS (공유/불변)
- 모든 서버가 공유하는 “기본 OS 이미지”
- 데이터:
  - `baseEntries: Dictionary<fullPath, EntryMeta>`
  - `baseDirIndex: Dictionary<dirPath, HashSet<childName>>`  (ls 최적화)

### 2.2 OverlayFS (서버별/가변)
- 서버별 변경분(추가/수정/삭제)을 저장
- 데이터:
  - `overlayEntries: Dictionary<fullPath, EntryMeta>` (추가/수정/override)
  - `tombstones: HashSet<fullPath>` (삭제 마커: base 파일도 숨김)
  - `dirDelta: Dictionary<dirPath, DirDelta>`  (현재 델타만 유지)

`DirDelta`:
- `added: HashSet<childName>`  : Base에는 없는데 지금은 있음
- `removed: HashSet<childName>`: Base에는 있는데 지금은 없음
- **중립 상태(added/removed 모두 empty)가 되면 dirDelta에서 제거** (누적 방지)

### 2.3 BlobStore (전역 공유, 콘텐츠 중복 제거)
- 파일 내용은 EntryMeta에 직접 저장하지 않고 contentId로 참조할 수 있음.
- 데이터:
  - `blobs: Dictionary<contentId, bytes/string>`
  - `refCount: Dictionary<contentId, int>`  (overlay 파일만 카운트)
- BaseFS의 contentId는 “pin” 처리(삭제/감소 대상 아님) 또는 refCount 관리에서 제외.

---

## 3) 엔트리 모델(EntryMeta)

`EntryMeta` 최소 필드:
- `entryKind: File | Dir`
- `fileKind` (File일 때): `Text | Binary | Image | ExecutableScript | ExecutableHardcode`
- `contentId` (File일 때)
- (선택) `size(Optional[int])`, `mtime`, `owner`, `perms`

`size`/`realSize` 규칙:
- `realSize`: 실제 UTF-8 payload 바이트 크기(런타임 계산값)
- `size`: 게임에서 인식하는 논리 파일 크기
  - `size` 생략 시 `size = realSize`
  - `size` 지정 시 해당 값을 파일 크기로 사용
- `BlueprintEntryMeta`는 `realSize`를 별도 필드로 유지한다(디버그/검증용)

디렉토리 표현:
- “숨김 파일(dir_entry)” 없음
- 빈 디렉토리를 유지하려면 `EntryMeta(entryKind=Dir)`로 명시적으로 만든다(`mkdir`).

파일 실행 가능성 규칙(v0):
- 직접 실행은 `fileKind == ExecutableScript | ExecutableHardcode`에서만 허용
- `fileKind == Text`는 **직접 실행 불가**
- `ExecutableScript`:
  - 파일 내용은 MiniScript 소스 텍스트
- `ExecutableHardcode`:
  - 파일 내용은 `exec:<executableId>` 텍스트
- 확장자(예: `.ms`)는 실행 가능성 판정 키가 아니라 표시/편의용 메타데이터로만 취급
- 실행 파일 해석/디스패처/오류 처리 계약은 `14_official_programs.md`를 따른다.  
  See DOCS_INDEX.md → 14.
- `cat`/`edit` 등 시스템콜의 파일종류별 UX는 `07_ui_terminal_prototype_godot.md`를 따른다.  
  See DOCS_INDEX.md → 07.

---

## 4) 조회 규칙(병합 규칙)

### 4.1 ResolveEntry(path)
우선순위 고정:

1) `if tombstones.contains(path)` → 없음
2) `else if overlayEntries.contains(path)` → overlay 반환
3) `else if baseEntries.contains(path)` → base 반환
4) else 없음

> 기본 파일 삭제는 “tombstone이 base를 가림”으로 처리.

### 4.2 ls(dir)
- `names = baseDirIndex[dir]` 복사(없으면 empty)
- `delta = dirDelta[dir]` 적용:
  - `names -= delta.removed`
  - `names += delta.added`
- (선택) `ResolveEntry(dir/name)`로 실제 존재 확인 후 필터링(초기 개발 안전장치)

### 4.3 find(dir, pattern)
- `find(dir, pattern)`는 VFS 내부/엔진 보조 API로 정의한다.
- MiniScript intrinsic `fs.find`의 포함 여부는 `03_game_api_modules.md`를 따른다.
- 터미널 명령어 `find`의 포함 여부는 `07_ui_terminal_prototype_godot.md`를 따른다.
- 구현은 DFS/BFS 재귀 탐색을 권장한다.
- 자식 목록은 `ls(dir)`를 사용해 tombstone/override를 자동 반영한다.

### 4.4 프로그램 실행 해석/디스패치 참조
- 명령 해석 순서(시스템콜 → 프로그램 fallback), PATH 규칙, `ExecutableHardcode` 디스패처 오류 처리 규약은 `14_official_programs.md`를 따른다.  
  See DOCS_INDEX.md → 14.

---

## 5) 변경 규칙(쓰기/삭제) + DirDelta 갱신

모든 변경 연산은 아래를 함께 갱신:
- `overlayEntries`
- `tombstones`
- `dirDelta`
- (file이면) `BlobStore.refCount`

### 5.1 DirDelta 갱신 규칙(누적 방지 핵심)

헬퍼:
- `baseHasChild(dir, name) = baseDirIndex[dir].contains(name)` (없으면 false)

#### ApplyAddChild(dir, name)
- `delta = getOrCreate(dirDelta[dir])`
- `delta.removed.remove(name)`
- if `baseHasChild(dir, name)`:
  - `delta.added.remove(name)`  (base 상태로 복귀)
- else:
  - `delta.added.add(name)`     (base에 없던 새 항목)
- 정리:
  - added/removed 모두 비면 `dirDelta.remove(dir)`

#### ApplyRemoveChild(dir, name)
- `delta = getOrCreate(dirDelta[dir])`
- if `baseHasChild(dir, name)`:
  - `delta.removed.add(name)`   (base에 있던 걸 숨김)
  - `delta.added.remove(name)`
- else:
  - `delta.added.remove(name)`  (base에 없던 건 지우면 델타도 제거)
  - `delta.removed.remove(name)` (일반적으로 불필요)
- 정리 동일

> 이 규칙을 지키면 “만들었다 지웠다” 반복해도 델타가 히스토리처럼 쌓이지 않는다.

---

## 6) 파일/디렉토리 연산 스펙

### 6.1 mkdir(dirPath)
- `dirPath = Normalize(cwd, input)`
- 부모 디렉토리 존재/권한 확인(guest/root 규칙에 맞춰)
- `tombstones.remove(dirPath)` (있으면 복구)
- `overlayEntries[dirPath] = DirEntryMeta`
- `ApplyAddChild(parent, name)`

### 6.2 write(filePath, content)
- `filePath = Normalize(cwd, input)`
- `tombstones.remove(filePath)` (있으면 복구)
- `contentId = BlobStore.Put(content)` (refCount++)
- `realSize = UTF8ByteCount(content)`
- `fileKind` 입력이 없으면 기본값 `Text`로 저장
- 바이너리/이미지/실행 파일 업로드 경로에서는 `fileKind`를 명시적으로 지정
- `size` 입력이 없으면 `size = realSize`, 입력이 있으면 입력값을 논리 파일 크기로 저장
- 기존 overlay 파일이 있으면 old contentId refCount-- 후 교체
- `overlayEntries[filePath] = FileEntryMeta(fileKind=..., contentId=...)`
- `ApplyAddChild(parent, name)`

### 6.3 rm(filePath)
- `filePath = Normalize(...)`
- (권장) **현재 디렉토리 삭제 금지**
  - `if filePath == session.cwd` → 에러(`rmdir .` 포함)
- overlay에 있으면 제거:
  - File이면 old contentId refCount--
  - `overlayEntries.remove(filePath)`
- base에 존재하면 tombstone:
  - `if baseEntries.contains(filePath)` → `tombstones.add(filePath)`
- `ApplyRemoveChild(parent, name)`

### 6.4 rm -r(dirPath) (Base dir 삭제 tombstone: 옵션 2 “전개”)
정책: 디렉토리 삭제 시, **삭제되는 하위 엔트리 경로를 모두 나열**하여 tombstone/overlay 정리를 수행한다.

권장 구현:
1) `targets = EnumerateLiveSubtree(dirPath)`
   - Base subtree(= baseDirIndex) + overlay subtree를 병합하여 “현재 보이는” 경로 리스트 생성
2) children-first 순서로:
   - `rm(target)` 호출(overlay/refCount/dirDelta 정리)
   - `if baseEntries.contains(target)` → `tombstones.add(target)`
3) 마지막으로 `rm(dirPath)`도 수행

주의:
- 전개 방식은 tombstone이 많아질 수 있음(대신 구현이 단순).
- HashSet이라 동일 경로의 반복 삭제는 누적되지 않음.

### 6.5 rename/copy 엔진 연산(선택)
- 엔진 레벨 VFS primitive로 rename/copy를 둘 수 있다.
- 사용자 명령 노출(`mv`/`cp`)과 UX는 `07_ui_terminal_prototype_godot.md`를 따른다.

---

## 7) 경로 처리 & cd .. 규칙

### 7.1 경로 정규화(Normalize)
- absolute/relative 처리:
  - input이 `/`로 시작하면 절대경로
  - 아니면 `cwd + "/" + input`로 합친 뒤 정규화
- 정규화 규칙:
  - `.` 제거
  - `..`는 세그먼트 pop (루트에서는 더 못 올라가며 `/` 유지)
  - 중복 `/` 제거

### 7.2 cd
- `target = Normalize(cwd, arg)`
- `entry = ResolveEntry(target)`
- entry가 `Dir`이면 `cwd = target`, 아니면 에러

엣지 케이스(합의):
- 현재 디렉토리 삭제는 금지(삭제 시도 시 에러) → cwd가 “사라지는 상황” 자체가 없음.

---

## 8) 저장/로드 참조
- 저장 대상/제외, 스냅샷 포맷, 재구축 경계는 `12_save_load_persistence_spec_v0_1.md`가 SSOT다.  
  See DOCS_INDEX.md → 12.
- 본 문서는 VFS **런타임 의미/자료구조**만 정의하며 저장 정책은 재정의하지 않는다.

---

## 9) 구현 체크리스트

- [ ] BaseFS (entries + dirIndex) 생성
- [ ] 서버별 OverlayFS (overlayEntries + tombstones + dirDelta)
- [ ] ResolveEntry/ls/find 구현
- [ ] mkdir/write/rm/rm -r 구현
- [ ] 경로 Normalize + cd .. 동작 확인
- [ ] 현재 디렉토리 삭제 금지 처리(`rmdir .` 에러)
- [ ] BlobStore(refCount) 기본 동작 + overlay 파일 교체/삭제 시 refCount 감소
- [ ] `EntryMeta.size(Optional[int])` + `realSize` 분리 규칙 반영
- [ ] `fileKind` 확장 반영(`ExecutableScript`, `ExecutableHardcode`)
