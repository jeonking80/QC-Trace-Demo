using QcTrace.Web.Data.Seed;

namespace QcTrace.Web.Auth;

/// <summary>
/// 역할별 접근 정책. 업무 화면은 해당 역할만, 관리자 화면은 관리자만 들어간다.
/// 역할이 하나도 없는 사용자는 로그인은 되지만 어떤 업무 화면·API에도 들어가지 못한다(403).
/// </summary>
public static class Policies
{
    public const string AnyRole = "AnyRole";
    public const string Producer = "ProducerOnly";
    public const string Inspector = "InspectorOnly";
    public const string Approver = "ApproverOnly";
    public const string Shipper = "ShipperOnly";
    public const string Admin = "AdminOnly";

    /// <summary>Razor Pages 폴더 → 정책. 폴더 이름이 곧 화면 묶음이다.</summary>
    public static readonly (string Folder, string Policy)[] PageFolders =
    [
        ("/Lots", Producer),
        ("/Inspections", Inspector),
        ("/Approvals", Approver),
        ("/Shipments", Shipper),
        ("/Admin", Admin),
        ("/Trace", AnyRole),   // LOT 조회·상세: 역할이 있는 사용자 누구나(집 PC 피드백 B1)
    ];

    public static void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        options.AddPolicy(AnyRole, p => p.RequireRole(Roles.All));
        options.AddPolicy(Producer, p => p.RequireRole(Roles.Producer));
        options.AddPolicy(Inspector, p => p.RequireRole(Roles.Inspector));
        options.AddPolicy(Approver, p => p.RequireRole(Roles.Approver));
        options.AddPolicy(Shipper, p => p.RequireRole(Roles.Shipper));
        options.AddPolicy(Admin, p => p.RequireRole(Roles.Admin));

        // 명시하지 않은 엔드포인트는 로그인을 요구한다. 익명 허용은 [AllowAnonymous]로만 연다.
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }

    /// <summary>로그인 직후 역할별 첫 화면. 역할이 여러 개면 위에서부터 먼저 맞는 것.</summary>
    public static string HomePathFor(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet();
        if (set.Contains(Roles.Admin)) return "/Admin/Dashboard";
        if (set.Contains(Roles.Producer)) return "/Lots";
        if (set.Contains(Roles.Inspector)) return "/Inspections";
        if (set.Contains(Roles.Approver)) return "/Approvals";
        if (set.Contains(Roles.Shipper)) return "/Shipments";
        return "/";
    }
}
