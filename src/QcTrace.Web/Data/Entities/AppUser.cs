using Microsoft.AspNetCore.Identity;

namespace QcTrace.Web.Data.Entities;

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}
