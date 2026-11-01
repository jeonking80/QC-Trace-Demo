/*
  앱 실행 계정용 DB 역할 qctrace_app_role (로그인 qctrace_app이 가입한다)
  - 조회: 전 테이블 SELECT
  - 쓰기: Identity 테이블과 기준정보(Items, InspectionItems)만 직접 허용
  - 수량·이력 테이블(Lots, LotQuantities, Inspections, ShipmentApprovals, Shipments, Ledger, QcRequests)은
    직접 INSERT·UPDATE·DELETE를 거부하고, 저장 프로시저 실행(EXECUTE)으로만 바뀐다.
    SP와 테이블 소유자가 같아(dbo) 소유권 체인으로 SP 안의 쓰기는 허용된다.
  - ALTER 권한이 없으므로 TRUNCATE·DDL도 불가
  로그인·사용자 생성과 역할 가입은 앱 시작 시 관리자 연결로 한다(비밀번호가 설정값이라서).
*/
IF DATABASE_PRINCIPAL_ID(N'qctrace_app_role') IS NULL
    CREATE ROLE qctrace_app_role AUTHORIZATION dbo;

GRANT SELECT ON SCHEMA::dbo TO qctrace_app_role;
GRANT EXECUTE ON SCHEMA::dbo TO qctrace_app_role;

GRANT INSERT, UPDATE, DELETE ON dbo.Users TO qctrace_app_role;
GRANT INSERT, UPDATE, DELETE ON dbo.Roles TO qctrace_app_role;
GRANT INSERT, UPDATE, DELETE ON dbo.UserRoles TO qctrace_app_role;
GRANT INSERT, UPDATE, DELETE ON dbo.UserClaims TO qctrace_app_role;
GRANT INSERT, UPDATE, DELETE ON dbo.UserLogins TO qctrace_app_role;
GRANT INSERT, UPDATE, DELETE ON dbo.UserTokens TO qctrace_app_role;
GRANT INSERT, UPDATE, DELETE ON dbo.RoleClaims TO qctrace_app_role;
GRANT INSERT, UPDATE ON dbo.Items TO qctrace_app_role;
GRANT INSERT, UPDATE ON dbo.InspectionItems TO qctrace_app_role;

DENY INSERT, UPDATE, DELETE ON dbo.Lots TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.LotQuantities TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.Inspections TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.ShipmentApprovals TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.Shipments TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.Ledger TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.QcRequests TO qctrace_app_role;
