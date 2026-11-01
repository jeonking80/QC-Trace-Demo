# QC Trace

제조 LOT의 검사·출하 이력을 관리하는 사내 운영 시스템 데모입니다. .NET 8 · ASP.NET Core Razor Pages · SQL Server 2022로 만들었고, 수량 변경은 모두 MS-SQL 저장 프로시저 안에서 트랜잭션으로 처리합니다.

> 제조업 전산관리부 직무 지원용으로 만든 개인 프로젝트입니다. 품목·공정·검사 기준은 전부 가상 데이터이며, 특정 회사의 실제 업무를 반영한 것이 아닙니다.

![운영 대시보드](docs/screenshots/02-dashboard.png)

## 무엇을 보여주는가

- 검사 완료 전 물량은 출하되지 않는다 (수량 지표: 미검사 · 합격 · 불합격 · 보류 · 폐기 · 출하 · 출하 예약)
- 부분 검사와 정정을 허용하되, 모든 변경은 지울 수 없는 원장에 추가 기록한다
- 검사자 본인은 자기 LOT의 출하를 승인할 수 없다 (독립 승인)
- 같은 요청이 두 번 와도 수량은 한 번만 반영된다 (멱등 처리)
- 매일 08:00 검사·출하 보고서(엑셀)가 자동 생성되고, 48시간 넘게 검사되지 않은 LOT은 알림으로 남는다
- 앱 코드와 무관한 콘솔 앱이 원장 합으로 수량을 다시 계산해 대조한다

## 실행

Docker만 있으면 됩니다.

```bash
git clone https://github.com/jeonking80/qc-trace-demo.git
cd qc-trace-demo
docker compose up -d
```

- 앱: http://localhost:8080 (헬스체크 http://localhost:8080/health)
- 첫 실행 시 마이그레이션과 시드 데이터(품목 5종, LOT 30건)가 자동 적재됩니다. 시드 날짜는 처음 실행한 시각을 기준으로 지난 15일에 걸쳐 만들어집니다.
- DB 비밀번호를 바꾸려면 `.env.example`을 `.env`로 복사해 값을 넣습니다.
- 일일 보고서는 `reports` 볼륨(`/app/reports`)에 쌓입니다. 앱을 처음 띄우면 1분 안에 보고서가 하나 만들어집니다(한국 시간 08:00 이후면 전날, 그 전이면 그저께 기준).
- 로그인 쿠키 암호화 키는 `dp-keys` 볼륨에 보관하며, 데모 범위라 키 파일 자체는 암호화하지 않습니다(시작 로그의 `XmlKeyManager` 경고).

### 데모 계정

비밀번호는 모두 `12341234!` 입니다(데모 전용, 설정 `Seed:DemoPassword`로 바꿀 수 있음). 이미 만들어진 DB의 계정 비밀번호는 바뀌지 않으므로, 예전 비밀번호로 시드된 DB는 `docker compose down -v` 후 다시 띄우면 새 비밀번호로 시드됩니다.

| 역할 | 아이디 | 첫 화면 |
| --- | --- | --- |
| 관리자 | admin | 운영 대시보드 |
| 생산 담당 | producer | LOT 목록·등록 |
| 검사자 | inspector1, inspector2 | 검사 |
| 승인권자 | approver | 출하 승인 |
| 출하 담당 | shipper | 출하 실적 |
| 역할 없음 (권한 확인용) | norole | 안내 화면 (업무 화면은 403) |

검사자 겸 승인권자 계정은 시드에 없습니다. 독립 승인(R5) 화면을 보려면 관리자 → 사용자에서 두 역할을 가진 계정을 만들거나 역할을 더해 주세요(변경 사유가 이력에 남습니다).

## 시연 순서

E2E 시나리오 1·3과 같은 흐름입니다. 시드의 검사·출하 기록은 지난 날짜에 있어서, 대시보드의 "오늘" 지표(불합격률·당일 출하)는 아래처럼 당일 작업을 해야 0이 아닌 값이 나옵니다.

1. `producer` → LOT 등록 (100 EA) → 검사 의뢰
2. `inspector1` → 검사 입력: 측정값을 넣으면 항목별 자동 판정, 60 EA 합격 → 남은 미검사 40 표시
3. 나머지 35 EA 합격, 5 EA는 규격 밖 측정값으로 불합격(사유 필수)
4. `approver` → 출하 승인: 출하 가능 = 합격 95. (`inspector1`처럼 직접 검사한 사람이면 버튼이 막힘)
5. `shipper` → 100 EA 출하 시도 → 승인 수량 초과로 거절, 95 EA 출하
6. `admin` → 대시보드, 이력 원장(LOT 번호로 필터, CSV), 재고 대사(불일치 0건), 보고서·알림(엑셀 내려받기)

| | |
| --- | --- |
| ![로그인](docs/screenshots/01-login.png) 01 로그인 | ![LOT 등록](docs/screenshots/03-lot-register.png) 03 LOT 등록 |
| ![부분 검사](docs/screenshots/04-inspection-partial.png) 04 부분 검사, 잔량 표시 | ![불합격 처리](docs/screenshots/05-dispose-reinspect.png) 05 불합격 처리(재검사) |
| ![본인 승인 차단](docs/screenshots/06-approval-self-blocked.png) 06 본인 검사 LOT 승인 차단 | ![승인 초과 출하 거절](docs/screenshots/07-shipment-over-approved.png) 07 승인 초과 출하 거절 |
| ![이력 원장](docs/screenshots/08-ledger.png) 08 이력 원장 | ![재고 대사](docs/screenshots/09-reconcile.png) 09 재고 대사 |
| ![보고서·알림](docs/screenshots/10-reports.png) 10 보고서·알림 | ![대시보드](docs/screenshots/02-dashboard.png) 02 운영 대시보드 |
| ![긴급 의뢰](docs/screenshots/13-urgent-request.png) 13 긴급 의뢰·일정 회신 확인 | ![일정 회신](docs/screenshots/14-schedule-propose.png) 14 검사자 일정 회신 |
| ![일정 결정](docs/screenshots/15-schedule-decide.png) 15 일정 결정(관리자·승인권자) | ![불량 유형](docs/screenshots/16-defect-code.png) 16 불량 유형 선택 |
| ![불합격 폐기](docs/screenshots/17-admin-scrap.png) 17 불합격 폐기(관리자만) | |
| ![LOT 조회](docs/screenshots/11-lot-search.png) 11 LOT 조회 | ![LOT 상세](docs/screenshots/12-lot-detail.png) 12 LOT 상세(생산 담당 화면: 불합격 사유·측정값·다음 할 일) |

## 기술 구성

| 계층 | 선택 |
| --- | --- |
| 런타임 | .NET 8 LTS, C# |
| 웹 | ASP.NET Core Razor Pages + Minimal API (`/api/...`, `Idempotency-Key` 헤더) |
| DB | SQL Server 2022 (Docker) |
| DB 접근 | EF Core (조회·마이그레이션), 저장 프로시저 (수량 변경). 앱은 최소 권한 계정으로 접속해 수량·이력 테이블을 직접 바꾸지 못함 |
| 인증 | ASP.NET Core Identity 쿠키, 역할 5개 |
| 대시보드 | 프레임워크 없는 HTML · JavaScript (fetch API, 60초 갱신) |
| 보고서 | ClosedXML, Hosted Service (매일 08:00 한국 시간) |
| 테스트 | xUnit, Testcontainers (실제 SQL Server), Playwright |
| 검증 실행 | 로컬 Docker (GitHub Actions는 쓰지 않음) |

## 검증

2026-11-01 로컬 Docker(SQL Server 2022)에서 실행한 결과입니다. 테스트 수는 자동화 실행 건수입니다.

| 층 | 도구 | 결과 |
| --- | --- | --- |
| 규칙 (R1~R10, 구현 코드를 보지 않고 작성) | xUnit + Testcontainers | 81/81 통과 |
| 통합 (SP · 트리거 · 권한 · API · 화면 · 자동화 · R11 · 검사 일정) | xUnit + Testcontainers | 156/156 통과 |
| E2E | Playwright 시나리오 5개 (정상, 불합격 재검사, 본인 승인 차단, 검사 일정 협의, 불량 유형·관리자 폐기) + 화면 확인 2개 | 7/7 통과 |
| 독립 대조 | 별도 콘솔 앱, 원장 합으로 수량 재계산 + 원장 행마다 근거 원본 행 확인 | LOT 33건 불일치 0건 |

- 업무 규칙 R1~R11과 테스트 대응표: [docs/rules-traceability.md](docs/rules-traceability.md)
- 기획서: [docs/PLAN.md](docs/PLAN.md)

```bash
dotnet test                                   # 규칙 · 통합 (Docker 필요)
cd tests/e2e && npm ci && npx playwright test # E2E (앱이 떠 있어야 함, CAPTURE=1이면 docs/screenshots 갱신)
dotnet run --project tools/Reconcile          # 독립 대조 (--all: 전체 LOT 표시, 불일치 있으면 종료 코드 1)
```

### 알아 둘 점

- **불합격률**은 처음 판정 기준입니다. 나중에 정정해도 그날 불합격률은 바뀌지 않습니다(정정 건수는 따로 셉니다).
- **R9 초과 보류**: 생산 수량보다 많이 검사된 분량은 보류로 남고, 그만큼 "생산 = 미검사 + 합격 + 불합격 + 보류 + 폐기" 등식이 어긋납니다. 대사 화면과 독립 대조는 이를 불일치가 아닌 "초과 보류"로 구분합니다.
- **독립 대조의 한계**: 원장 행이 근거 행(검사·승인·출하 행, LOT)과 수량·방향까지 맞는지 보지만, 재검사·폐기는 별도 기록 테이블이 없어 LOT 자체를 근거로 삼습니다. 관리자(sa) 권한으로 원장·수량·근거 행을 모두 맞춰 조작하면 잡지 못합니다. 앱 계정은 이 테이블들에 쓸 권한이 없습니다.
- **R11**: 합격은 그 품목의 검사 항목을 모두 측정하고 전부 규격 안이어야 저장됩니다. 검사 항목이 하나도 없는 품목은 측정 없이 합격할 수 있으므로, 품목을 추가하면 검사 항목을 함께 넣어야 합니다(기준정보 화면 안내).

## 개발 방식

Claude Code로 구현했습니다. 요구사항·업무 규칙·검증 기준은 직접 정했고, 코드 작성·테스트 생성·오류 분석은 AI가 맡았습니다. 개발 세션과 검증 세션을 나눠, 결과는 코드 리뷰가 아니라 테스트·화면·DB 직접 조회와 독립 대조로 확인했습니다. 작업 단위별 기록은 [docs/claude-code-log.md](docs/claude-code-log.md), 핵심 저장 프로시저 설명은 [docs/sp-explainer.md](docs/sp-explainer.md)에 있습니다.

## 범위 밖

- 실제 계측 장비·바코드 스캐너 연동
- ERP · MES 연계, 메일·메신저 알림 발송(지연 알림은 화면·로그·보고서에만 남김)
- 성능 측정 (하지 않았으므로 수치를 적지 않습니다)
- 상용 환경 운영 실적

## 작성자

전우석 · 2026.10
