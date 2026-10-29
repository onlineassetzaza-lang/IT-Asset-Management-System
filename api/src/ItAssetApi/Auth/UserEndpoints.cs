using System.Text.RegularExpressions;
using ItAssetApi.Audit;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Auth;

public record CreateUserRequest(string? Username, string? DisplayName, string? Role, string? Password, Guid? EmployeeId);
public record UpdateUserRequest(string? DisplayName, string? Role, bool? Active, Guid? EmployeeId, string? ConfirmPassword);
public record ResetPasswordRequest(string? NewPassword, string? ConfirmPassword);

public static partial class UserEndpoints
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{2,31}$")]
    private static partial Regex UsernamePattern();

    private static object Dto(User u) => new
    {
        u.Id, u.Username, u.DisplayName, u.Role, u.EmployeeId, u.Active, u.LockedUntil, u.CreatedAt,
    };

    public static void Map(RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(Policies.UserManage);

        users.MapGet("/", async (AppDb db) =>
            Results.Ok(await db.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => Dto(u)).ToListAsync()));

        users.MapPost("/", async (CreateUserRequest req, AppDb db, AuditService audit, IPasswordHasher<User> hasher) =>
        {
            var errors = new Errors();
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            if (!UsernamePattern().IsMatch(username)) errors.Add("username", "Username ต้องเป็น a-z 0-9 . _ - ยาว 3-32 ตัวอักษร");
            if (string.IsNullOrWhiteSpace(req.DisplayName)) errors.Add("displayName", "กรุณากรอกชื่อที่แสดง");
            if (req.Role is null || !Roles.All.Contains(req.Role)) errors.Add("role", "Role ไม่ถูกต้อง");
            if (PasswordPolicy.Validate(req.Password) is { } pwError) errors.Add("password", pwError);
            if (req.EmployeeId is not null && !await db.Employees.AnyAsync(e => e.Id == req.EmployeeId))
                errors.Add("employeeId", "ไม่พบพนักงาน");
            if (errors.Any) return errors.ToResult();

            var user = new User
            {
                Username = username, DisplayName = req.DisplayName!.Trim(), Role = req.Role!, EmployeeId = req.EmployeeId,
            };
            user.PasswordHash = hasher.HashPassword(user, req.Password!);
            db.Users.Add(user);
            audit.Add("user.create", "User", user.Id.ToString(), new { username, role = user.Role });
            if (await db.SaveOrConflict($"Username {username} มีอยู่แล้ว") is { } conflict) return conflict;
            return Results.Created($"/api/v1/users/{user.Id}", Dto(user));
        });

        users.MapPatch("/{id:guid}", async (Guid id, UpdateUserRequest req, AppDb db, AuditService audit,
            IPasswordHasher<User> hasher, HttpContext ctx) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Problems.NotFound("ผู้ใช้");
            var me = ctx.User.UserId();

            var errors = new Errors();
            if (req.DisplayName is not null && string.IsNullOrWhiteSpace(req.DisplayName)) errors.Add("displayName", "กรุณากรอกชื่อที่แสดง");
            if (req.Role is not null && !Roles.All.Contains(req.Role)) errors.Add("role", "Role ไม่ถูกต้อง");
            if (errors.Any) return errors.ToResult();

            var roleChanged = req.Role is not null && req.Role != user.Role;
            var deactivating = req.Active == false && user.Active;
            if (roleChanged || deactivating)
            {
                // Re-authenticate for privilege-affecting changes
                var actor = await db.Users.FirstAsync(u => u.Id == me);
                if (hasher.VerifyHashedPassword(actor, actor.PasswordHash, req.ConfirmPassword ?? "") == PasswordVerificationResult.Failed)
                    return Errors.Single("confirmPassword", "ต้องยืนยันรหัสผ่านของคุณเพื่อเปลี่ยน Role หรือปิดบัญชี");
                if (deactivating && user.Id == me) return Problems.Conflict("ไม่สามารถปิดบัญชีที่กำลังใช้งานอยู่");

                var losesAdmin = user is { Role: Roles.Admin, Active: true } && (deactivating || (roleChanged && req.Role != Roles.Admin));
                if (losesAdmin && !await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == Roles.Admin && u.Active))
                    return Problems.Conflict("ต้องมี Admin ที่ใช้งานอยู่อย่างน้อย 1 คน");
            }

            var changes = new Dictionary<string, object?>();
            if (req.DisplayName is not null) { user.DisplayName = req.DisplayName.Trim(); changes["displayName"] = true; }
            if (roleChanged) { changes["role"] = new { from = user.Role, to = req.Role }; user.Role = req.Role!; user.SecurityStamp = Guid.NewGuid(); }
            if (req.Active is not null && req.Active != user.Active)
            {
                user.Active = req.Active.Value;
                if (!user.Active) user.SecurityStamp = Guid.NewGuid();
                changes["active"] = user.Active;
            }
            if (req.EmployeeId is not null) { user.EmployeeId = req.EmployeeId; changes["employeeId"] = true; }

            audit.Add("user.update", "User", user.Id.ToString(), changes);
            await db.SaveChangesAsync();
            return Results.Ok(Dto(user));
        });

        users.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest req, AppDb db, AuditService audit,
            IPasswordHasher<User> hasher, HttpContext ctx) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Problems.NotFound("ผู้ใช้");
            if (PasswordPolicy.Validate(req.NewPassword) is { } pwError) return Errors.Single("newPassword", pwError);

            var actor = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            if (hasher.VerifyHashedPassword(actor, actor.PasswordHash, req.ConfirmPassword ?? "") == PasswordVerificationResult.Failed)
                return Errors.Single("confirmPassword", "ต้องยืนยันรหัสผ่านของคุณเพื่อรีเซ็ตรหัสผ่าน");

            user.PasswordHash = hasher.HashPassword(user, req.NewPassword!);
            user.SecurityStamp = Guid.NewGuid();
            user.FailedLogins = 0;
            user.LockedUntil = null;
            audit.Add("user.reset_password", "User", user.Id.ToString());
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
