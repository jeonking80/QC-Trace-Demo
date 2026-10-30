using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Pages.Account;

/// <summary>로그아웃은 POST로만 받는다(GET 링크로 로그아웃되지 않게).</summary>
public class LogoutModel(SignInManager<AppUser> signInManager, UserManager<AppUser> userManager) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        // SecurityStamp를 바꿔 로그아웃 전에 복사된 쿠키도 다음 검증(최대 1분) 때 무효가 되게 한다
        var user = await userManager.GetUserAsync(User);
        if (user is not null)
        {
            await userManager.UpdateSecurityStampAsync(user);
        }
        await signInManager.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
