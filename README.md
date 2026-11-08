# Slay the Spire 2 Tier Overlay

슬레이 더 스파이어 2를 실시간으로 읽어 카드 위에 티어 배지를 그리고, 별도 오버레이 패널에서
덱 맞춤 추천·빌드(아키타입) 완성도·경로 추천을 보여주는 개인용 도구입니다.

- 설계: [docs/PLAN.md](docs/PLAN.md)
- 기술 검증(M0) 결과: [docs/SPIKE.md](docs/SPIKE.md) (작성 중)

## 구성 (예정)

| 프로젝트 | 역할 |
|---|---|
| `Sts2Overlay.Bridge` | 게임 공식 모드 로더용 읽기 전용 C# 모드. 상태 수집 + 카드 배지 |
| `Sts2Overlay.Core` | 게임 의존성 없는 추천 엔진 (점수·빌드·경로) |
| `Sts2Overlay.Contracts` | 모드↔앱 프로토콜 DTO |
| `Sts2Overlay.App` | WPF 오버레이 패널 |

게임 DLL과 게임 텍스트(카탈로그 덤프)는 저작권 때문에 레포에 커밋하지 않습니다.
