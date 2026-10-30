using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QcTrace.Web.Auth;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Pages.Account;

public class LoginModel(SignInManager<AppUser> signInManager, UserManager<AppUser> userManager) : PageModel
{
    [BindProperty, Required(ErrorMessage = "아이디를 입력하세요.")]
    public string UserName { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "비밀번호를 입력하세요."), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var result = await signInManager.PasswordSignInAsync(UserName, Password, isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "로그인 실패가 반복되어 계정이 잠시 잠겼습니다. 5분 뒤 다시 시도하세요.");
            return Page();
        }
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "아이디 또는 비밀번호가 올바르지 않습니다.");
            return Page();
        }

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        var user = await userManager.FindByNameAsync(UserName);
        var roles = user is null ? [] : await userManager.GetRolesAsync(user);
        return LocalRedirect(Policies.HomePathFor(roles));
    }
}
