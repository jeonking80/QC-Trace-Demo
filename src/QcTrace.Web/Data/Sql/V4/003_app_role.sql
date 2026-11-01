/*
  단위 7 권한 추가: 역할 변경은 usp_SetUserRoles로만 한다.
  앱 계정은 UserRoles·RoleChanges에 직접 쓸 수 없다(조회는 스키마 SELECT로 가능).
*/
DENY INSERT, UPDATE, DELETE ON dbo.UserRoles TO qctrace_app_role;
DENY INSERT, UPDATE, DELETE ON dbo.RoleChanges TO qctrace_app_role;
