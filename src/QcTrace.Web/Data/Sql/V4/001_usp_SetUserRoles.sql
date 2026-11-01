/*
  사용자 역할 변경 (관리자 전용). 역할 변경은 이 SP로만 한다.
  - 관리자 역할(Admin)이 있는 사용자만 실행할 수 있다(50403).
  - 사유 필수(52004, R10). 바뀐 역할마다 RoleChanges에 한 행(추가만)을 남긴다.
  - 자기 자신의 관리자 역할은 뺄 수 없고, 관리자가 한 명도 남지 않게 할 수 없다(50400).
  - 바뀐 것이 있으면 SecurityStamp를 새로 만들어 대상 사용자의 기존 로그인 쿠키에도 1분 안에 반영되게 한다.
  - 같은 역할 목록을 다시 보내면 아무것도 바뀌지 않는다(자연 멱등).
  @Roles: 쉼표로 구분한 역할 이름 (예: N'Inspector,Approver'), 빈 문자열이면 역할 없음.
  결과: 한 행 (ResponseJson, Replayed=0) — 다른 SP와 같은 모양
*/
CREATE OR ALTER PROCEDURE dbo.usp_SetUserRoles
    @ActorId nvarchar(450),
    @UserId  nvarchar(450),
    @Roles   nvarchar(1000),
    @Reason  nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');
    IF @Reason IS NULL
        THROW 52004, N'역할 변경은 사유가 필요합니다. (R10)', 1;

    DECLARE @Wanted TABLE (Name nvarchar(256) PRIMARY KEY);
    INSERT INTO @Wanted (Name)
    SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(ISNULL(@Roles, N''), N',') WHERE LTRIM(RTRIM(value)) <> N'';

    IF EXISTS (SELECT 1 FROM @Wanted w WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles r WHERE r.Name = w.Name))
        THROW 50400, N'알 수 없는 역할 이름이 있습니다.', 1;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @ActorId AND r.Name = N'Admin')
        THROW 50403, N'관리자만 역할을 바꿀 수 있습니다.', 1;

    -- 같은 사용자 역할 변경을 직렬화한다
    IF NOT EXISTS (SELECT 1 FROM dbo.Users WITH (UPDLOCK, ROWLOCK) WHERE Id = @UserId)
        THROW 50404, N'사용자를 찾을 수 없습니다.', 1;

    DECLARE @Current TABLE (RoleId nvarchar(450), Name nvarchar(256));
    INSERT INTO @Current SELECT r.Id, r.Name FROM dbo.UserRoles ur WITH (UPDLOCK, HOLDLOCK) JOIN dbo.Roles r ON r.Id = ur.RoleId
    WHERE ur.UserId = @UserId;

    DECLARE @Changes TABLE (RoleId nvarchar(450), Name nvarchar(256), Change nvarchar(10));
    INSERT INTO @Changes
    SELECT r.Id, r.Name, N'Add' FROM @Wanted w JOIN dbo.Roles r ON r.Name = w.Name
    WHERE NOT EXISTS (SELECT 1 FROM @Current c WHERE c.Name = w.Name)
    UNION ALL
    SELECT c.RoleId, c.Name, N'Remove' FROM @Current c
    WHERE NOT EXISTS (SELECT 1 FROM @Wanted w WHERE w.Name = c.Name);

    IF @UserId = @ActorId AND EXISTS (SELECT 1 FROM @Changes WHERE Name = N'Admin' AND Change = N'Remove')
        THROW 50400, N'자기 자신의 관리자 역할은 뺄 수 없습니다.', 1;

    IF EXISTS (SELECT 1 FROM @Changes WHERE Name = N'Admin' AND Change = N'Remove')
       AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WITH (UPDLOCK, HOLDLOCK) JOIN dbo.Roles r ON r.Id = ur.RoleId
                       WHERE r.Name = N'Admin' AND ur.UserId <> @UserId)
        THROW 50400, N'관리자가 한 명도 남지 않게 할 수 없습니다.', 1;

    DECLARE @Now datetime2 = SYSUTCDATETIME();

    DELETE ur FROM dbo.UserRoles ur
    JOIN @Changes c ON c.RoleId = ur.RoleId AND c.Change = N'Remove'
    WHERE ur.UserId = @UserId;

    INSERT INTO dbo.UserRoles (UserId, RoleId)
    SELECT @UserId, RoleId FROM @Changes WHERE Change = N'Add';

    INSERT INTO dbo.RoleChanges (UserId, RoleName, Change, ActorId, Reason, CreatedAt)
    SELECT @UserId, Name, Change, @ActorId, @Reason, @Now FROM @Changes;

    IF EXISTS (SELECT 1 FROM @Changes)
        UPDATE dbo.Users SET SecurityStamp = CONVERT(nvarchar(36), NEWID()), ConcurrencyStamp = CONVERT(nvarchar(36), NEWID())
        WHERE Id = @UserId;

    COMMIT TRANSACTION;

    SELECT (
        SELECT @UserId AS userId,
               JSON_QUERY(COALESCE((SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(Name, 'json') + N'"', N',') + N']'
                                    FROM @Changes WHERE Change = N'Add'), N'[]')) AS added,
               JSON_QUERY(COALESCE((SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(Name, 'json') + N'"', N',') + N']'
                                    FROM @Changes WHERE Change = N'Remove'), N'[]')) AS removed
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
