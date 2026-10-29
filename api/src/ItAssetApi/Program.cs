using System.Text.Json.Serialization;
using ItAssetApi;
using ItAssetApi.Audit;
using ItAssetApi.Auth;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Inventory;
using ItAssetApi.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// ---- Services ----
// Read lazily so configuration added later (e.g. by tests) is honoured
builder.Services.AddDbContext<AppDb>((sp, o) =>
{
    var connection = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default") ?? "Data Source=storage/itam.db";
    if (connection.StartsWith("Data Source=storage/")) Directory.CreateDirectory("storage");
    o.UseSqlite(connection);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "itam.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Configuration.GetValue("Security:RequireHttpsCookie", true)
        ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = async ctx =>
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
        ctx.HttpContext.RequestServices.GetRequiredService<AuditService>()
            .Add("auth.access_denied", "Route", null, new { method = ctx.Request.Method, path = ctx.Request.Path.Value }, outcome: "denied");
        await db.SaveChangesAsync();
        ctx.Response.StatusCode = 403;
    };
    // Re-check the user on every request so deactivation, role changes and password changes apply immediately
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ctx.Principal!.UserId());
        if (user is null || !user.Active || ctx.Principal!.FindFirst(PrincipalExtensions.StampClaim)?.Value != user.SecurityStamp.ToString())
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        if (ctx.Principal!.IsInRole(user.Role) == false) ctx.ReplacePrincipal(PrincipalExtensions.Build(user));
    };
});

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.AssetWrite, p => p.RequireRole(Policies.RolesFor[Policies.AssetWrite]))
    .AddPolicy(Policies.EmployeeManage, p => p.RequireRole(Policies.RolesFor[Policies.EmployeeManage]))
    .AddPolicy(Policies.SettingsManage, p => p.RequireRole(Policies.RolesFor[Policies.SettingsManage]))
    .AddPolicy(Policies.UserManage, p => p.RequireRole(Policies.RolesFor[Policies.UserManage]))
    .AddPolicy(Policies.AuditRead, p => p.RequireRole(Policies.RolesFor[Policies.AuditRead]));

var app = builder.Build();

// ---- Database ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Initialize();
    Bootstrap.Seed(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(), app.Configuration, app.Logger);
}

// ---- Pipeline ----
app.UseExceptionHandler();
app.UseStatusCodePages();

// The API also serves the web UI (same origin, so no CORS needed). Path is relative to the API project.
var webRoot = Path.GetFullPath(app.Configuration["Web:Root"] ?? Path.Combine(app.Environment.ContentRootPath, "..", "..", "..", "web"));
if (Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        // Always revalidate (cheap 304 via ETag) so a browser never mixes a new index.html with stale JS/CSS
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
    });
}

app.UseAuthentication();

// Local convenience only: skip the login page by signing in as a given user. Requires the Development
// environment AND Dev:AutoLoginUsername, and only applies to loopback requests. Never enabled otherwise.
var devUser = app.Environment.IsDevelopment() ? app.Configuration["Dev:AutoLoginUsername"] : null;
if (!string.IsNullOrWhiteSpace(devUser))
{
    app.Logger.LogWarning("DEV AUTO-LOGIN is ON: loopback requests are signed in as '{User}' without a password.", devUser);
    app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true && ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip))
        {
            var user = await ctx.RequestServices.GetRequiredService<AppDb>().Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == devUser && u.Active);
            if (user is not null)
            {
                var principal = PrincipalExtensions.Build(user);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                ctx.User = principal;
            }
        }
        await next();
    });
}

// CSRF defence: cookies are SameSite=Strict, and state-changing calls must also carry a custom header,
// which a cross-site form or simple request cannot set.
app.Use(async (ctx, next) =>
{
    var unsafeMethod = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method);
    if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-Requested-With"] != "itam")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { title = "Bad Request", detail = "missing X-Requested-With header" });
        return;
    }
    await next();
});

app.UseAuthorization();

app.MapGet("/health", (AppDb db) => db.Database.CanConnect() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503)).AllowAnonymous();

var api = app.MapGroup("/api/v1");
AuthEndpoints.Map(api);
UserEndpoints.Map(api);
MasterEndpoints.Map(api);
AssetEndpoints.Map(api);
DashboardEndpoints.Map(api);
AuditEndpoints.Map(api);

app.Run();

public partial class Program;
