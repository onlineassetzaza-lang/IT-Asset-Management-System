using System.Net.Http.Json;
using System.Text.Json;
using ItAssetApi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ItAssetApi.Tests;

/// <summary>Boots the real app against a throw-away SQLite file.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "Admin-pass-1234";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"itam-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
            ["ITAM_ADMIN_USERNAME"] = "admin",
            ["ITAM_ADMIN_PASSWORD"] = AdminPassword,
            ["Security:RequireHttpsCookie"] = "false",
            ["Web:Root"] = Path.Combine(Path.GetTempPath(), "no-such-web-root"),
        }));
    }

    public HttpClient NewClient(bool csrfHeader = true)
    {
        var c = CreateClient();
        if (csrfHeader) c.DefaultRequestHeaders.Add("X-Requested-With", "itam");
        return c;
    }

    public async Task<HttpClient> LoginAs(string username, string password)
    {
        var c = NewClient();
        var res = await c.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        res.EnsureSuccessStatusCode();
        return c;
    }

    public Task<HttpClient> Admin() => LoginAs("admin", AdminPassword);

    /// <summary>Creates a user as admin and returns a client signed in as that user.</summary>
    public async Task<HttpClient> UserWithRole(string role, string? name = null)
    {
        name ??= $"{role.ToLower()}-{Guid.NewGuid():N}"[..14];
        var admin = await Admin();
        var res = await admin.PostAsJsonAsync("/api/v1/users",
            new { username = name, displayName = name, role, password = "Passw0rd-12345" });
        res.EnsureSuccessStatusCode();
        return await LoginAs(name, "Passw0rd-12345");
    }

    public async Task<T> Db<T>(Func<AppDb, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteCleanup();
    }

    private void SqliteCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* temp file; OS will clean up */ }
    }
}

public static class Json
{
    public static async Task<JsonElement> Body(this HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>());

    public static Guid Id(this JsonElement e) => e.GetProperty("id").GetGuid();
}

/// <summary>Helpers to set up common fixtures through the public API.</summary>
public static class Fixtures
{
    public static async Task<Guid> Category(HttpClient admin)
    {
        var list = await (await admin.GetAsync("/api/v1/categories")).Body();
        return list[0].Id();
    }

    public static async Task<JsonElement> Asset(HttpClient client, Guid categoryId, string? tag = null)
    {
        tag ??= "IT-" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        var res = await client.PostAsJsonAsync("/api/v1/assets", new { assetTag = tag, name = "Test device", categoryId });
        Assert.True(res.StatusCode == System.Net.HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await res.Body();
    }

    public static async Task<Guid> Employee(HttpClient client, string? number = null)
    {
        number ??= "E" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.PostAsJsonAsync("/api/v1/employees", new { employeeNumber = number, displayName = "สมชาย ใจดี" });
        Assert.Equal(System.Net.HttpStatusCode.Created, res.StatusCode);
        return (await res.Body()).Id();
    }

    public static object CheckOut(Guid employeeId, string? key = null) =>
        new { employeeId, requestKey = key ?? Guid.NewGuid().ToString("N") };
}
