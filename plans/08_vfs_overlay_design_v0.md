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
- (선택) `size`, `mtime`, `owner`, `perms`

디렉토리 표현:
- “숨김 파일(dir_entry)” 없음
- 빈 디렉토리를 유지하려면 `EntryMeta(entryKind=Dir)`로 명시적으로 만든다(`mkdir`).

파일 실행 가능성 규칙(v0):
- 직접 실행은 `fileKind == ExecutableScript | ExecutableHardcode`에서만 허용
- `fileKind == Text`는 **직접 실행 불가**
- `ExecutableScript`:
  - 파일 내용은 MiniScript 소스 텍스트
  - 파일명을 직접 실행하면 내장 MiniScript 인터프리터로 실행
- `ExecutableHardcode`:
  - 파일 내용은 `executableId` 텍스트
  - 파일명을 직접 실행하면 `executableId` 기반 dispatcher로 실행
- MiniScript 스크립트 실행은 `miniscript <scriptPath>` 형태를 지원
  - `miniscript`는 **시스템콜이 아니라 VFS 실행 파일 이름**이다(예: `/opt/bin/miniscript`)
  - `<scriptPath>`는 `fileKind == Text`면 확장자와 무관하게 실행 시도 가능
  - 스크립트가 MiniScript 문법/런타임 조건을 만족하지 않으면 인터프리터 오류 반환
- 개발/검증 전용 예외:
  - 프로젝트 `DEBUG` 옵션이 켜진 경우에 한해 `DEBUG_miniscript <scriptPath>` 시스템콜을 임시 허용할 수 있다
- 확장자(예: `.ms`)는 실행 가능성 판정 키가 아니라 표시/편의용 메타데이터로만 취급
- 실행계열 파일(`ExecutableScript`, `ExecutableHardcode`)은 바이너리처럼 취급:
  - `cat`/`edit` 대상에서 차단
  - 권장 오류 문구: `error: cannot read executable file: <path>`

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
- MVP는 DFS/BFS 재귀 탐색
- 자식 목록은 `ls(dir)`를 사용해 tombstone/override를 자동 반영

### 4.4 명령 실행 해석(시스템콜 + 프로그램 fallback)
- 디스패치 순서:
  1) 시스템콜 registry 조회
  2) 미일치 시 프로그램 실행 탐색
  3) 최종 미해결 시 `unknown command`
- 프로그램 탐색 규칙(PATH 하드코딩):
  - `PATH = ["/opt/bin"]`
  - `command`에 `/`가 포함되면 `Normalize(cwd, command)`만 시도하고 PATH는 탐색하지 않음
  - `/`가 없으면 순서대로:
    1) `Normalize(cwd, command)`
    2) `/opt/bin/<command>`
- 상대경로 실행 지원:
  - `../prog`, `./tools/prog` 모두 `Normalize(cwd, command)`로 해석
- 실행 권한:
  - 프로그램 실행은 `read + execute` 둘 다 필요

### 4.5 ExecutableHardcode 미등록 ID 처리
- `ExecutableHardcode` 실행 시 `executableId`가 빈값/미등록이면 사용자 응답은 `unknown command: <command>`로 통일
- 내부 디버그 로그:
  - `WorldRuntime.DebugOption == true`일 때만 `GD.PushWarning(...)` 출력
  - 권장 포함 필드: `command`, `resolvedProgramPath`, `executableId`, `nodeId`, `userKey`, `cwd`

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
- `fileKind` 입력이 없으면 기본값 `Text`로 저장
- 바이너리/이미지/실행 파일 업로드 경로에서는 `fileKind`를 명시적으로 지정
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

### 6.5 mv/cp (선택, MVP 이후)
- `mv`: read+write+rm 조합으로 구현 가능(권한/메타 고려)
- `cp`: BlobStore contentId 재사용 가능(내용 복사 대신 참조 복사)

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

## 8) 저장/로드(권장 정책)
- 저장: 서버별로 `overlayEntries + tombstones`만 저장
- 로드: `dirDelta`는 재구성(overlay/tombstone을 훑어 ApplyAdd/Remove로 rebuild)
- BlobStore:
  - 전역 저장 시 refCount는 overlayEntries를 스캔해서 재계산 가능(안전)

---

## 9) 구현 체크리스트

- [ ] BaseFS (entries + dirIndex) 생성
- [ ] 서버별 OverlayFS (overlayEntries + tombstones + dirDelta)
- [ ] ResolveEntry/ls/find 구현
- [ ] mkdir/write/rm/rm -r 구현
- [ ] 경로 Normalize + cd .. 동작 확인
- [ ] 현재 디렉토리 삭제 금지 처리(`rmdir .` 에러)
- [ ] BlobStore(refCount) 기본 동작 + overlay 파일 교체/삭제 시 refCount 감소
- [ ] `fileKind` 확장 반영(`ExecutableScript`, `ExecutableHardcode`)
- [ ] 시스템콜 미일치 시 프로그램 fallback + `PATH=/opt/bin` + 상대경로 실행 확인
- [ ] 실행계열 파일 `cat`/`edit` 차단 규칙 적용
- [ ] `ExecutableHardcode` 미등록 ID 시 사용자 `unknown command` 유지 + DEBUG warning 로그 출력
