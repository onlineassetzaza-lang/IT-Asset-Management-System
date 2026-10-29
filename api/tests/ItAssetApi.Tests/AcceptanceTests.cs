using System.Net;
using System.Net.Http.Json;
using ItAssetApi.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Tests;

/// <summary>Maps to the MVP acceptance criteria in the system design (section 11).</summary>
public class AcceptanceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AcceptanceTests(ApiFactory f) => _f = f;

    // 1. Users without permission cannot change data, even by calling the API directly
    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var res = await _f.NewClient().GetAsync("/api/v1/assets");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Auditor_cannot_write_and_denial_is_audited()
    {
        var admin = await _f.Admin();
        var auditor = await _f.UserWithRole(Roles.Auditor);
        var cat = await Fixtures.Category(admin);

        var res = await auditor.PostAsJsonAsync("/api/v1/assets", new { assetTag = "NOPE-1", name = "x", categoryId = cat });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/users",
            new { username = "evil", displayName = "x", role = "Admin", password = "Passw0rd-12345" })).StatusCode);

        var denied = await _f.Db(db => db.AuditEvents.CountAsync(e => e.Action == "auth.access_denied"));
        Assert.True(denied >= 2);
    }

    [Fact]
    public async Task Requests_without_csrf_header_are_rejected()
    {
        var res = await _f.NewClient(csrfHeader: false).PostAsJsonAsync("/api/v1/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // 2. Duplicate asset IDs are rejected with a readable message
    [Fact]
    public async Task Duplicate_asset_tag_is_rejected_and_invalid_input_is_422()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var cat = await Fixtures.Category(await _f.Admin());
        var asset = await Fixtures.Asset(staff, cat, "dup-001");
        Assert.Equal("DUP-001", asset.GetProperty("assetTag").GetString());

        var dup = await staff.PostAsJsonAsync("/api/v1/assets", new { assetTag = "DUP-001", name = "again", categoryId = cat });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("มีอยู่ในระบบแล้ว", (await dup.Body()).GetProperty("detail").GetString());

        var bad = await staff.PostAsJsonAsync("/api/v1/assets", new { assetTag = "", name = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    // 3. Non-available assets cannot be issued twice, and retries do not duplicate
    [Fact]
    public async Task Asset_cannot_be_checked_out_twice_and_retry_is_idempotent()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var asset = await Fixtures.Asset(staff, await Fixtures.Category(await _f.Admin()));
        var emp = await Fixtures.Employee(staff);
        var id = asset.Id();

        var first = await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-outs", Fixtures.CheckOut(emp, "retry-key-0001"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var assignmentId = (await first.Body()).Id();

        var replay = await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-outs", Fixtures.CheckOut(emp, "retry-key-0001"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(assignmentId, (await replay.Body()).Id());

        var other = await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-outs", Fixtures.CheckOut(emp));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);

        var count = await _f.Db(db => db.Assignments.CountAsync(a => a.AssetId == id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Concurrent_check_outs_produce_exactly_one_assignment()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var asset = await Fixtures.Asset(staff, await Fixtures.Category(await _f.Admin()));
        var emp = await Fixtures.Employee(staff);
        var id = asset.Id();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-outs", Fixtures.CheckOut(emp))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await _f.Db(db => db.Assignments.CountAsync(a => a.AssetId == id && a.CheckedInAt == null)));
    }

    // 4. Check-in closes the current assignment and keeps the history
    [Fact]
    public async Task Check_in_closes_assignment_and_keeps_history()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var asset = await Fixtures.Asset(staff, await Fixtures.Category(await _f.Admin()));
        var emp = await Fixtures.Employee(staff);
        var id = asset.Id();
        await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-outs", Fixtures.CheckOut(emp));

        var back = await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-ins",
            new { conditionIn = "ปกติ", requestKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);

        var now = await (await staff.GetAsync($"/api/v1/assets/{id}")).Body();
        Assert.Equal("Available", now.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, now.GetProperty("holder").ValueKind);

        var history = await (await staff.GetAsync($"/api/v1/assets/{id}/history")).Body();
        Assert.Equal(1, history.GetProperty("assignments").GetArrayLength());

        // Not checked out any more -> a second check-in is refused
        var again = await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-ins", new { requestKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Damaged_return_opens_maintenance_and_closing_it_restores_availability()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var asset = await Fixtures.Asset(staff, await Fixtures.Category(await _f.Admin()));
        var emp = await Fixtures.Employee(staff);
        var id = asset.Id();
        await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-outs", Fixtures.CheckOut(emp));
        await staff.PostAsJsonAsync($"/api/v1/assets/{id}/check-ins",
            new { conditionIn = "จอแตก", needsMaintenance = true, requestKey = Guid.NewGuid().ToString("N") });

        Assert.Equal("Maintenance", (await (await staff.GetAsync($"/api/v1/assets/{id}")).Body()).GetProperty("status").GetString());
        var open = await (await staff.GetAsync("/api/v1/maintenance?open=true")).Body();
        var record = open.GetProperty("items").EnumerateArray().First(r => r.GetProperty("assetId").GetGuid() == id);

        var close = await staff.PostAsJsonAsync($"/api/v1/maintenance/{record.Id()}/close", new { resolution = "เปลี่ยนจอ", cost = 3500, outcome = "Available" });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal("Available", (await (await staff.GetAsync($"/api/v1/assets/{id}")).Body()).GetProperty("status").GetString());
    }

    // 5. Assets with history are never deleted; status only changes via commands
    [Fact]
    public async Task Assets_cannot_be_deleted_and_status_cannot_be_patched()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var asset = await Fixtures.Asset(staff, await Fixtures.Category(await _f.Admin()));
        var id = asset.Id();

        var del = await staff.DeleteAsync($"/api/v1/assets/{id}");
        Assert.True(del.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);

        // "status" in the body is ignored: the request type has no such field
        var patch = await staff.PatchAsJsonAsync($"/api/v1/assets/{id}", new { version = 0, name = "Renamed", status = "Retired" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Available", (await patch.Body()).GetProperty("status").GetString());

        var stale = await staff.PatchAsJsonAsync($"/api/v1/assets/{id}", new { version = 0, name = "Stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Retire_follows_the_state_rules()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var cat = await Fixtures.Category(await _f.Admin());
        var emp = await Fixtures.Employee(staff);

        var inUse = (await Fixtures.Asset(staff, cat)).Id();
        await staff.PostAsJsonAsync($"/api/v1/assets/{inUse}/check-outs", Fixtures.CheckOut(emp));
        var tooEarly = await staff.PostAsJsonAsync($"/api/v1/assets/{inUse}/retire", new { version = 1, reason = "Lost" });
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

        var free = (await Fixtures.Asset(staff, cat)).Id();
        var retired = await staff.PostAsJsonAsync($"/api/v1/assets/{free}/retire", new { version = 0, reason = "Disposed" });
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        Assert.Equal("Retired", (await retired.Body()).GetProperty("status").GetString());

        var issue = await staff.PostAsJsonAsync($"/api/v1/assets/{free}/check-outs", Fixtures.CheckOut(emp));
        Assert.Equal(HttpStatusCode.Conflict, issue.StatusCode);
    }

    // 6. Important actions are audited with actor and time; 7. the log is append-only and clean
    [Fact]
    public async Task Role_changes_and_asset_actions_are_audited()
    {
        var admin = await _f.Admin();
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var asset = await Fixtures.Asset(staff, await Fixtures.Category(admin));

        var events = await _f.Db(db => db.AuditEvents.Where(e => e.TargetId == asset.Id().ToString()).ToListAsync());
        var created = Assert.Single(events, e => e.Action == "asset.create");
        Assert.NotNull(created.ActorUserId);
        Assert.True(created.OccurredAt > DateTime.UtcNow.AddMinutes(-5));

        var users = await (await admin.GetAsync("/api/v1/users")).Body();
        var target = users.EnumerateArray().First(u => u.GetProperty("role").GetString() == "ITStaff").Id();
        var changed = await admin.PatchAsJsonAsync($"/api/v1/users/{target}", new { role = "Auditor", confirmPassword = ApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.True(await _f.Db(db => db.AuditEvents.AnyAsync(e => e.Action == "user.update" && e.TargetId == target.ToString())));
    }

    [Fact]
    public async Task Audit_log_is_append_only_and_never_contains_secrets()
    {
        var admin = await _f.Admin();
        await _f.UserWithRole(Roles.ITStaff);   // creates a user with password "Passw0rd-12345"
        await _f.NewClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "admin", password = "wrong-password-999" });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _f.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE AuditEvents SET Action = 'tampered'")));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _f.Db(db => db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents")));

        var all = await _f.Db(async db => string.Join("\n", (await db.AuditEvents.ToListAsync()).Select(e => e.Metadata + e.Action)));
        Assert.DoesNotContain("Passw0rd-12345", all);
        Assert.DoesNotContain(ApiFactory.AdminPassword, all);
        Assert.DoesNotContain("wrong-password-999", all);

        // Auditor can read the log; ITStaff cannot
        var auditor = await _f.UserWithRole(Roles.Auditor);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/audit-events")).StatusCode);
        var staff = await _f.UserWithRole(Roles.ITStaff);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/audit-events")).StatusCode);
        _ = admin;
    }

    // 8. Search, filter and paging are server-side
    [Fact]
    public async Task Search_filter_and_paging_run_on_the_server()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var cat = await Fixtures.Category(await _f.Admin());
        for (var i = 1; i <= 12; i++) await Fixtures.Asset(staff, cat, $"PG-{i:000}");

        var page1 = await (await staff.GetAsync("/api/v1/assets?query=PG-&pageSize=10")).Body();
        var page2 = await (await staff.GetAsync("/api/v1/assets?query=PG-&pageSize=10&page=2")).Body();
        Assert.Equal(12, page1.GetProperty("total").GetInt32());
        Assert.Equal(10, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());

        var one = await (await staff.GetAsync("/api/v1/assets?query=PG-007")).Body();
        Assert.Equal(1, one.GetProperty("total").GetInt32());

        // LIKE wildcards in user input are treated literally
        var wildcard = await (await staff.GetAsync("/api/v1/assets?query=%25")).Body();
        Assert.Equal(0, wildcard.GetProperty("total").GetInt32());
    }

    // Account safety
    [Fact]
    public async Task Deactivating_a_user_ends_their_session_immediately()
    {
        var admin = await _f.Admin();
        var name = "tmp-" + Guid.NewGuid().ToString("N")[..8];
        var created = await admin.PostAsJsonAsync("/api/v1/users", new { username = name, displayName = name, role = "ITStaff", password = "Passw0rd-12345" });
        var userId = (await created.Body()).Id();
        var victim = await _f.LoginAs(name, "Passw0rd-12345");
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/assets")).StatusCode);

        var off = await admin.PatchAsJsonAsync($"/api/v1/users/{userId}", new { active = false, confirmPassword = ApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/v1/assets")).StatusCode);
    }

    [Fact]
    public async Task Role_change_needs_password_confirmation_and_last_admin_is_protected()
    {
        var admin = await _f.Admin();
        var me = (await (await admin.GetAsync("/api/v1/auth/me")).Body()).Id();

        var noConfirm = await admin.PatchAsJsonAsync($"/api/v1/users/{me}", new { role = "Auditor" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noConfirm.StatusCode);

        // Another admin exists in the shared DB only by chance, so check the guard against a fresh factory
        using var solo = new ApiFactory();
        var soloAdmin = await solo.Admin();
        var soloId = (await (await soloAdmin.GetAsync("/api/v1/auth/me")).Body()).Id();
        var demote = await soloAdmin.PatchAsJsonAsync($"/api/v1/users/{soloId}", new { role = "Auditor", confirmPassword = ApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
    }

    [Fact]
    public async Task Repeated_login_failures_lock_the_account()
    {
        var admin = await _f.Admin();
        var name = "lock-" + Guid.NewGuid().ToString("N")[..8];
        await admin.PostAsJsonAsync("/api/v1/users", new { username = name, displayName = name, role = "Auditor", password = "Passw0rd-12345" });

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await _f.NewClient().PostAsJsonAsync("/api/v1/auth/login", new { username = name, password = "bad-password-1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);

        // Even the right password is refused while locked
        var right = await _f.NewClient().PostAsJsonAsync("/api/v1/auth/login", new { username = name, password = "Passw0rd-12345" });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task Dashboard_counts_match_assets()
    {
        var staff = await _f.UserWithRole(Roles.ITStaff);
        var cat = await Fixtures.Category(await _f.Admin());
        await Fixtures.Asset(staff, cat);
        var s = await (await staff.GetAsync("/api/v1/dashboard/summary")).Body();
        Assert.Equal(s.GetProperty("available").GetInt32() + s.GetProperty("inUse").GetInt32()
            + s.GetProperty("maintenance").GetInt32() + s.GetProperty("retired").GetInt32(), s.GetProperty("total").GetInt32());
    }
}
