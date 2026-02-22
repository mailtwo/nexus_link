# MiniScript 적용 + CPU/RAM 제약 설계

이 문서는 **Lua 대신 MiniScript를 채택**했을 때의 **Godot(PC, C#) 임베딩 구조**(엔진 무관 C# 호스트 모델), 샌드박싱, 그리고 **CPU(실행 예산) / RAM(논리 메모리)** 제약을 “업그레이드 가능한 성장 시스템”으로 연결하는 설계를 정리한다.

---

## 1) MiniScript를 쓰는 이유(프로젝트 관점)

- MiniScript는 “임베드(embedded) 언어”로 설계되어, 게임/앱 내부에서 스크립트를 실행하기 좋다.
- MiniScript의 C# 통합 가이드(예시는 Unity지만 개념은 엔진 무관)가 존재하고,
  “여러 스크립트를 각각의 독립 샌드박스”로 동시에 돌릴 수 있으며,
  호스트(게임)가 노출한 것만 접근하도록 만들 수 있다.

**프로젝트 결정**  
- 유저 스크립트 언어: **MiniScript**
- 프로그램 배포 형태: 인게임 파일(`.ms`) + 에디터로 수정 가능
- 샌드박스 원칙: “실제 OS/네트워크 접근 0” (전부 가상 API만)

---

## 2) Godot에서의 실행 모델(권장: time-slicing)

### 기본 구조(요약)
- 프로그램(툴)마다 `Interpreter` 인스턴스 1개
- 게임 루프(예: Godot의 `_Process`)에서 매 프레임 `RunUntilDone(timeSliceSeconds)` 호출  
  → 스크립트가 길어도 **프레임을 멈추지 않고** 조금씩 진행

### 예시(의사 코드)
```csharp
using Godot;
using Miniscript; // 예시 네임스페이스(실제는 패키지 구조에 맞게 조정)

// per tool/program
public partial class ProgramRunner : Node
{
    private Interpreter _itp;
    public float CpuBudgetSecondsPerFrame = 0.002f; // CPU 레벨에 따라 조절

    public override void _Ready()
    {
        _itp = new Interpreter();
        _itp.Reset(sourceCode);
        _itp.Compile();
    }

    public override void _Process(double delta)
    {
        _itp.RunUntilDone(CpuBudgetSecondsPerFrame);
    }
}
```

### wait/yield와 프레임 협업
- (권장) 호스트가 `wait(seconds)` / `yield` 계열 intrinsic를 제공하면
  - 네트워크/크랙/다운로드 같은 “시간 걸리는 작업”을 게임적으로 표현 가능
  - `yield`는 다음 메인 루프(다음 프레임)까지 대기하는 느낌으로 사용

---

## 3) 샌드박싱(“가상 세계 API만 접근”)

### 원칙
- 유저는 MiniScript 표준 함수/전역만 가지고 시작한다.
- 게임은 **전역 변수/맵 + intrinsic 함수**로 “가상화된 API”만 주입한다.
- 절대 노출하면 안 되는 것
  - 실제 파일 시스템 접근, 실제 소켓/HTTP, 실제 프로세스 실행, reflection 등

### 구조 패턴
- 전역에 `fs`, `net`, `http`, `auth`, `crypto`, `agent` 같은 모듈(맵/클래스)을 주입
- 각 함수는 내부적으로 **가상 월드 시뮬레이터**를 호출한다.

---

## 4) CPU 제약(업그레이드) 설계

CPU 제약은 크게 두 가지 방식이 있다.

### A안(MVP) — 시간 예산(time slice) 기반
- **CPU 레벨 = 프레임당 허용 실행 시간(ms)**  
- 구현이 단순하고, MiniScript time-slicing과 자연스럽게 맞음.
- 단점: 사용자 PC 성능이 좋으면 같은 시간에 더 많은 작업을 수행할 수 있음(공정성 이슈).

**권장 사용**
- 싱글플레이/오프라인 밸런스면 충분히 OK.
- “현실 PC 성능이 곧 인게임 CPU”가 되어도 괜찮다면 가장 빠른 길.

### B안(밸런스 강화) — 명령어/호출 횟수(fuel) 기반
- **CPU 레벨 = 초당 허용되는 스크립트 스텝(fuel)**  
- 구현: 인터프리터 실행 루프(TAC 실행 등)에 카운터를 두고, 스텝마다 fuel 감소.
- 장점: 어떤 PC에서도 “가상 CPU 성능”이 동일.
- 단점: 엔진 커스텀 필요(하지만 장기적으로 밸런싱이 편해짐).

**권장 사용**
- 리더보드/경쟁 요소가 있거나, 난이도 튜닝을 정밀하게 하고 싶을 때.

---

## 5) RAM 제약(업그레이드) 설계

### 목표(UX)
- 유저가 툴을 실행하기 전에:
  - “이 프로그램은 RAM을 얼마나 먹는지(대략)” 알 수 있어야 함
  - 실행 중 RAM이 들쭉날쭉해서 갑자기 터지는 느낌은 최소화하고 싶음
- 따라서 **실행 전 RAM 예약(Reservation)** 모델을 채택한다.

### 현실적인 한계
- 모든 스크립트의 “최대 메모리 피크”를 실행 전에 정확히 구하는 건 일반적으로 불가능하다  
  (루프/입력 의존/재귀 때문에 정적 분석 한계가 있음).
- 그래서 게임에서는 “논리 메모리 포인트 + 상한 규칙” 조합이 실용적이다.

### 논리 메모리 포인트(예시 규칙)
- 숫자: 1
- 문자열: `len(s)`
- 리스트: `base + k * len(list)` (원소 타입 가중치 가능)
- 맵: `base + k * entries` (키/값 가중치 가능)
- 콜스택/프레임: `k_frame * depth` (대충)

**주의**  
- 이 포인트는 실제 바이트가 아니라 **밸런스용 단위**다.  
- “RAM 256 → 512 업그레이드”는 이 포인트 한도 증가로 표현.

---

## 6) “실행 전 RAM 소모 예측”을 만드는 2단계 방법

### 1단계: 정적 추정(compile 후 분석)
- 상수 리터럴(문자열/리스트/맵)의 크기는 즉시 계산 가능
- “반복 횟수가 상수인 루프”는 반복 수만큼 성장 연산을 곱해 상한 추정 가능
- 상한이 불명확하면 `UNBOUNDED` 표시(추정 불가)

### 2단계: 상한 규칙으로 UNBOUNDED를 없애기(게임 룰)
둘 중 하나를 강제하면 예측이 안정된다.

- (추천) **환경 상한**  
  예: `fs.read`는 최대 4096 chars, `http.get`은 최대 8192 chars, `list.push`는 최대 2000 elems  
  → 입력/네트워크로부터 오는 값도 상한을 갖게 되어 “대충 계산”이 가능해짐

- (선택) **스크립트 예산 선언(계약)**  
  예: 파일 상단에 `#budget ram=1200` 같은 헤더를 쓰게 하고  
  초과하면 실행 불가/에러 처리  
  → 유저가 프로그램을 배포/판매하는 시스템과 궁합이 좋음

---

## 7) “RAM 예약(Reservation)” 실행 흐름(권장)

1) 유저가 실행 버튼 클릭  
2) 엔진이 스크립트를 컴파일 + 정적 추정  
3) `estimated_ram_points` 표시  
4) PC/서버/툴 슬롯에서 남은 RAM과 비교  
5) 실행 승인 시: 해당 프로그램에 RAM 블록 예약  
   - 실행 중 늘어나도 “예약치”를 넘는 성장은 금지(런타임 가드)

### 런타임 가드(필수)
- 문자열 결합/리스트 push/맵 set 같은 “성장 지점”에서 포인트를 증가시키고,
  예약치를 넘으면 즉시 예외/중단(혹은 soft-fail 반환)

---

## 8) 구현 시 놓치기 쉬운 것(실무 체크)

- **무한 루프 방지**: time slice/fuel + 사용자 강제 중단(Ctrl+C)  
- **디버깅 UX**: 스택트레이스, 에러 라인, 실행 중 로그, “RAM breakdown”  
- **프로파일링**: 어떤 프로그램이 CPU/RAM을 얼마나 쓰는지(툴별)  
- **멀티 프로그램 스케줄링**: 여러 스크립트를 동시에 돌릴 때 CPU 예산 분배 정책 필요  
- **안전한 API 경계**: 실세계 접근이 절대 섞이지 않도록 코드 리뷰/테스트

---

## 참고 링크(원문)
```text
MiniScript Manual: https://miniscript.org/files/MiniScript-Manual.pdf
MiniScript Wait Wiki: https://miniscript.org/wiki/Wait
MiniScript Integration Guide (Unity 예시, C# 임베딩 참고): https://miniscript.org/files/MiniScript-Integration-Guide.pdf
Halting problem(정적 상한 계산 한계 배경): https://en.wikipedia.org/wiki/Halting_problem
```
