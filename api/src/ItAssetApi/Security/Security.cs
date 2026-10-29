using System.Security.Claims;
using ItAssetApi.Domain;

namespace ItAssetApi.Security;

public static class Policies
{
    public const string AssetWrite = "AssetWrite";       // create/update/retire, check-out/in, maintenance
    public const string EmployeeManage = "EmployeeManage";
    public const string SettingsManage = "SettingsManage"; // categories, locations
    public const string UserManage = "UserManage";
    public const string AuditRead = "AuditRead";

    public static readonly Dictionary<string, string[]> RolesFor = new()
    {
        [AssetWrite] = [Roles.Admin, Roles.ITStaff],
        [EmployeeManage] = [Roles.Admin, Roles.ITStaff],
        [SettingsManage] = [Roles.Admin],
        [UserManage] = [Roles.Admin],
        [AuditRead] = [Roles.Admin, Roles.Auditor],
    };

    public static string[] PermissionsOf(string role) =>
        RolesFor.Where(p => p.Value.Contains(role)).Select(p => p.Key).ToArray();
}

public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"รหัสผ่านต้องยาวอย่างน้อย {MinLength} ตัวอักษร";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "รหัสผ่านต้องมีทั้งตัวอักษรและตัวเลข";
        return null;
    }
}

public static class PrincipalExtensions
{
    public const string StampClaim = "stamp";

    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    public static ClaimsPrincipal Build(User u) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
        new Claim(ClaimTypes.Name, u.Username),
        new Claim(ClaimTypes.Role, u.Role),
        new Claim(StampClaim, u.SecurityStamp.ToString()),
    ], "cookie"));
}
