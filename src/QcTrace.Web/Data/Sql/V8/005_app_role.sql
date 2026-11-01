/*
  검사 일정 기록(ScheduleEvents)도 SP로만 쓴다. 앱 계정은 조회만 가능(스키마 SELECT).
*/
DENY INSERT, UPDATE, DELETE ON dbo.ScheduleEvents TO qctrace_app_role;
