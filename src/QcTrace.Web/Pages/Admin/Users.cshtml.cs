using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Data.Seed;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>
/// 기준정보: 사용자. 역할 변경은 usp_SetUserRoles로만 한다(사유 필수, RoleChanges에 추가 전용 기록).
/// 앱 계정은 UserRoles에 직접 쓸 수 없다. 계정 생성·잠금은 Identity(UserManager)로 한다.
/// </summary>
public class UsersModel(QcTraceDbContext db, UserManager<AppUser> users, QcCommands qc) : QcPageModel
{
    public static readonly (string Role, string Label)[] RoleLabels =
    [
        (Roles.Admin, "관리자"), (Roles.Producer, "생산"), (Roles.Inspector, "검사"), (Roles.Approver, "승인"), (Roles.Shipper, "출하"),
    ];

    public sealed record Row(AppUser User, HashSet<string> Roles, bool Locked);
    public List<Row> Rows { get; private set; } = [];

    [BindProperty] public string? NewUserName { get; set; }
    [BindProperty] public string? NewDisplayName { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public List<string> NewRoles { get; set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRolesAsync(string userId, List<string>? roles, string? reason)
    {
        var result = await TryRunAsync(() => qc.SetUserRolesAsync(CurrentUserId, userId, roles ?? [], reason));
        if (result is null) { await LoadAsync(); return Page(); }

        var added = result.Json.GetProperty("added").EnumerateArray().Select(x => x.GetString()).ToList();
        var removed = result.Json.GetProperty("removed").EnumerateArray().Select(x => x.GetString()).ToList();
        StatusMessage = added.Count + removed.Count == 0
            ? "바뀐 역할이 없습니다."
            : $"역할 변경: {(added.Count > 0 ? "추가 " + string.Join(", ", added) : "")}{(added.Count > 0 && removed.Count > 0 ? " / " : "")}{(removed.Count > 0 ? "제거 " + string.Join(", ", removed) : "")}. 대상 사용자의 로그인에는 1분 안에 반영됩니다.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUserName) || string.IsNullOrWhiteSpace(NewDisplayName) || string.IsNullOrEmpty(NewPassword))
        {
            ModelState.AddModelError("", "아이디·이름·초기 비밀번호가 필요합니다.");
            await LoadAsync();
            return Page();
        }
        var user = new AppUser { UserName = NewUserName.Trim(), DisplayName = NewDisplayName.Trim(), EmailConfirmed = true, LockoutEnabled = true };
        var created = await users.CreateAsync(user, NewPassword);
        if (!created.Succeeded)
        {
            foreach (var e in created.Errors) ModelState.AddModelError("", IdentityMessage(e));
            await LoadAsync();
            return Page();
        }

        if (NewRoles.Count > 0)
        {
            var result = await TryRunAsync(() => qc.SetUserRolesAsync(CurrentUserId, user.Id, NewRoles, $"신규 사용자 {user.UserName} 등록"));
            if (result is null)
            {
                ModelState.AddModelError("", $"사용자 {user.UserName}은 만들었지만 역할은 넣지 못했습니다. 목록에서 다시 지정하세요.");
                await LoadAsync();
                return Page();
            }
        }
        StatusMessage = $"사용자 {user.UserName}을 만들었습니다.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLockAsync(string userId, bool locked)
    {
        if (userId == CurrentUserId)
        {
            ModelState.AddModelError("", "자기 자신은 잠글 수 없습니다.");
            await LoadAsync();
            return Page();
        }
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        if (!locked) await users.ResetAccessFailedCountAsync(user);
        // 잠근 사용자의 기존 쿠키도 1분 안에 끊기게 한다
        await users.UpdateSecurityStampAsync(user);
        StatusMessage = locked ? $"{user.UserName}을 잠갔습니다." : $"{user.UserName} 잠금을 풀었습니다.";
        return RedirectToPage();
    }

    private static string IdentityMessage(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" => "같은 아이디가 이미 있습니다.",
        "InvalidUserName" => "아이디에는 영문·숫자·일부 기호만 쓸 수 있습니다.",
        "PasswordTooShort" => "비밀번호가 너무 짧습니다(6자 이상).",
        "PasswordRequiresNonAlphanumeric" => "비밀번호에 특수문자가 필요합니다.",
        "PasswordRequiresDigit" => "비밀번호에 숫자가 필요합니다.",
        "PasswordRequiresUpper" => "비밀번호에 영문 대문자가 필요합니다.",
        "PasswordRequiresLower" => "비밀번호에 영문 소문자가 필요합니다.",
        _ => e.Description
    };

    private async Task LoadAsync()
    {
        var roleMap = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name })
            .AsNoTracking().ToListAsync();
        var now = DateTimeOffset.UtcNow;
        Rows = (await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync())
            .Select(u => new Row(u, roleMap.Where(x => x.UserId == u.Id).Select(x => x.Name!).ToHashSet(), u.LockoutEnd > now))
            .ToList();
    }
}
