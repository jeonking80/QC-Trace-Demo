using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QcTrace.Web.Auth;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Pages;

public class IndexModel(UserManager<AppUser> userManager) : PageModel
{
    public IList<string> Roles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        Roles = user is null ? [] : await userManager.GetRolesAsync(user);

        // 역할이 있으면 역할별 첫 화면으로 보낸다
        var home = Policies.HomePathFor(Roles);
        return home == "/" ? Page() : LocalRedirect(home);
    }
}
