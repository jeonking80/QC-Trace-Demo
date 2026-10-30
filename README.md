# QC Trace

제조 LOT의 검사·출하 이력을 관리하는 사내 운영 시스템 데모입니다. .NET 8 · ASP.NET Core Razor Pages · SQL Server 2022로 만들었고, 수량 변경은 모두 MS-SQL 저장 프로시저 안에서 트랜잭션으로 처리합니다.

> 제조업 전산관리부 직무 지원용으로 만든 개인 프로젝트입니다. 품목·공정·검사 기준은 전부 가상 데이터이며, 특정 회사의 실제 업무를 반영한 것이 아닙니다.

## 무엇을 보여주는가

- 검사 완료 전 물량은 출하되지 않는다 (수량 지표: 미검사 · 합격 · 불합격 · 보류 · 출하)
- 부분 검사와 정정을 허용하되, 모든 변경은 불변 원장에 추가 기록한다
- 검사자 본인은 자기 LOT의 출하를 승인할 수 없다 (독립 승인)
- 같은 요청이 두 번 와도 수량은 한 번만 반영된다 (멱등 처리)
- 매일 아침 검사·출하 보고서(엑셀)가 자동 생성된다

## 실행

```bash
git clone <repo-url>
cd qc-trace
docker compose up -d
```

- 앱: http://localhost:8080
- 헬스체크: http://localhost:8080/health
- 첫 실행 시 마이그레이션과 시드 데이터(품목 5종, LOT 30건)가 자동 적재됩니다.
- 로그인 쿠키 암호화 키는 `dp-keys` 볼륨에 보관하며, 데모 범위라 키 파일 자체는 암호화하지 않습니다(시작 로그의 `XmlKeyManager` 경고).

### 데모 계정

| 역할 | 아이디 | 비밀번호 |
| --- | --- | --- |
| 관리자 | admin | (시드 스크립트 참조) |
| 생산 담당 | producer | (시드 스크립트 참조) |
| 검사자 | inspector1 | (시드 스크립트 참조) |
| 검사자 | inspector2 | (시드 스크립트 참조) |
| 승인권자 | approver | (시드 스크립트 참조) |
| 출하 담당 | shipper | (시드 스크립트 참조) |
| 역할 없음 (권한 확인용) | norole | (시드 스크립트 참조) |

## 시연 순서

1. `producer`로 로그인 → LOT 등록 (100 EA)
2. `inspector1`로 검사 입력 → 60 EA 합격, 40 EA 미검사 잔량 확인
3. 나머지 40 EA 검사 → 5 EA 불합격, 사유 입력
4. `inspector1`로 출하 승인 시도 → 본인 LOT이라 거절
5. `approver`로 출하 승인 → 95 EA
6. `shipper`로 출하 100 EA 시도 → 승인 수량 초과로 거절, 95 EA 출하
7. `admin`으로 원장·대사 화면에서 전 과정 이력과 수량 등식 확인

## 기술 구성

| 계층 | 선택 |
| --- | --- |
| 런타임 | .NET 8 LTS, C# |
| 웹 | ASP.NET Core Razor Pages + Minimal API |
| DB | SQL Server 2022 (Docker) |
| DB 접근 | EF Core (조회·CRUD), 저장 프로시저 (수량 변경) |
| 인증 | ASP.NET Core Identity, 역할 5개 |
| 대시보드 | 프레임워크 없는 HTML · JavaScript (fetch API) |
| 보고서 | ClosedXML, Hosted Service |
| 테스트 | xUnit, Testcontainers (실제 SQL Server), Playwright |
| CI | GitHub Actions |

## 검증

| 층 | 도구 | 결과 |
| --- | --- | --- |
| 단위 | xUnit | TBD |
| 통합 (실제 SQL Server) | xUnit + Testcontainers | TBD |
| E2E | Playwright 시나리오 3개 | TBD |
| 독립 대조 | 별도 콘솔 앱, 원장 합으로 수량 재계산 | TBD |

- 업무 규칙 R1~R10과 테스트의 대응표: [docs/rules-traceability.md](docs/rules-traceability.md)
- 기획서: [docs/PLAN.md](docs/PLAN.md)

```bash
dotnet test                     # 단위 · 통합
dotnet run --project tools/Reconcile   # 독립 대조
```

## 개발 방식

Claude Code로 구현했습니다. 요구사항·업무 규칙·검증 기준은 직접 정했고, 코드 작성·테스트 생성·오류 분석은 AI가 맡았습니다. 결과는 코드 리뷰가 아니라 테스트와 독립 대조로 확인했습니다. 작업 단위별 기록은 [docs/claude-code-log.md](docs/claude-code-log.md)에 있습니다.

## 범위 밖

- 실제 계측 장비·바코드 스캐너 연동
- ERP · MES 연계
- 성능 측정 (하지 않았으므로 수치를 적지 않습니다)
- 상용 환경 운영 실적

## 작성자

전우석 · 2026.10
