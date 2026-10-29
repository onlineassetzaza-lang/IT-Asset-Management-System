using ItAssetApi.Audit;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Auth;

public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public static class AuthEndpoints
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    // Used to spend comparable time when the username does not exist
    private static readonly User DummyUser = new();
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "dummy-password-1");

    public static object Profile(User u) => new
    {
        u.Id, u.Username, u.DisplayName, u.Role, u.EmployeeId, permissions = Policies.PermissionsOf(u.Role),
    };

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapPost("/login", async (LoginRequest req, AppDb db, AuditService audit,
            IPasswordHasher<User> hasher, HttpContext ctx) =>
        {
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            var password = req.Password ?? "";
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);

            if (user?.LockedUntil > DateTime.UtcNow)
            {
                audit.Add("auth.login", "User", user.Id.ToString(), outcome: "locked", actor: user.Id);
                await db.SaveChangesAsync();
                return Results.Problem(title: "Too Many Requests", detail: "บัญชีถูกล็อกชั่วคราว กรุณาลองใหม่ภายหลัง", statusCode: 429);
            }

            bool ok;
            if (user is null)
            {
                hasher.VerifyHashedPassword(DummyUser, DummyHash, password);
                ok = false;
            }
            else
            {
                ok = user.Active && hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
            }

            if (!ok)
            {
                if (user is not null)
                {
                    user.FailedLogins++;
                    if (user.FailedLogins >= MaxFailedLogins)
                    {
                        user.LockedUntil = DateTime.UtcNow.Add(LockDuration);
                        user.FailedLogins = 0;
                    }
                }
                // Unknown usernames are not recorded (they may be mistyped passwords)
                audit.Add("auth.login", "User", user?.Id.ToString(), outcome: "failure", actor: user?.Id);
                await db.SaveChangesAsync();
                return Results.Problem(title: "Unauthorized", detail: "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง", statusCode: 401);
            }

            user!.FailedLogins = 0;
            user.LockedUntil = null;
            audit.Add("auth.login", "User", user.Id.ToString(), actor: user.Id);
            await db.SaveChangesAsync();
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PrincipalExtensions.Build(user));
            return Results.Ok(Profile(user));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (AppDb db, AuditService audit, HttpContext ctx) =>
        {
            audit.Add("auth.logout", "User", ctx.User.UserId().ToString());
            await db.SaveChangesAsync();
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapGet("/me", async (AppDb db, HttpContext ctx) =>
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == ctx.User.UserId());
            return Results.Ok(Profile(user));
        });

        auth.MapPost("/change-password", async (ChangePasswordRequest req, AppDb db, AuditService audit,
            IPasswordHasher<User> hasher, HttpContext ctx) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Errors.Single("currentPassword", "รหัสผ่านเดิมไม่ถูกต้อง");
            if (PasswordPolicy.Validate(req.NewPassword) is { } policyError)
                return Errors.Single("newPassword", policyError);

            user.PasswordHash = hasher.HashPassword(user, req.NewPassword!);
            user.SecurityStamp = Guid.NewGuid();   // signs out every other session
            audit.Add("auth.change_password", "User", user.Id.ToString());
            await db.SaveChangesAsync();
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PrincipalExtensions.Build(user));
            return Results.NoContent();
        });
    }
}
