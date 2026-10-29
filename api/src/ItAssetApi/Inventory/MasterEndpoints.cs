using ItAssetApi.Audit;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Inventory;

public record MasterRequest(string? Name, string? Description, bool? Active);
public record EmployeeRequest(string? EmployeeNumber, string? DisplayName, string? Department, string? Email, bool? Active);

public static class MasterEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // Categories and locations share the same shape; deactivation is blocked while non-retired assets use them
        MapNamed<Category>(api, "categories", "Category", (db, id) => db.Assets.AnyAsync(a => a.CategoryId == id && a.Status != AssetStatus.Retired));
        MapNamed<Location>(api, "locations", "Location", (db, id) => db.Assets.AnyAsync(a => a.LocationId == id && a.Status != AssetStatus.Retired));
        MapEmployees(api);
    }

    private static void MapNamed<T>(RouteGroupBuilder api, string path, string type, Func<AppDb, Guid, Task<bool>> inUse)
        where T : class, IMaster, new()
    {
        var g = api.MapGroup("/" + path);

        g.MapGet("/", async (AppDb db, bool? includeInactive) =>
            Results.Ok(await db.Set<T>().AsNoTracking()
                .Where(x => includeInactive == true || x.Active).OrderBy(x => x.Name).ToListAsync()));

        g.MapPost("/", async (MasterRequest req, AppDb db, AuditService audit) =>
        {
            if (Check(req.Name, req.Description) is { } err) return err;
            var item = new T { Name = req.Name!.Trim(), Description = req.Description?.Trim() };
            db.Set<T>().Add(item);
            audit.Add($"{type.ToLower()}.create", type, item.Id.ToString(), new { name = item.Name });
            if (await db.SaveOrConflict($"ชื่อ {item.Name} มีอยู่แล้ว") is { } conflict) return conflict;
            return Results.Created($"/api/v1/{path}/{item.Id}", item);
        }).RequireAuthorization(Policies.SettingsManage);

        g.MapPatch("/{id:guid}", async (Guid id, MasterRequest req, AppDb db, AuditService audit) =>
        {
            var item = await db.Set<T>().FindAsync(id);
            if (item is null) return Problems.NotFound("ข้อมูล");
            if (req.Name is not null || req.Description is not null)
                if (Check(req.Name ?? item.Name, req.Description) is { } err) return err;
            if (req.Active == false && item.Active && await inUse(db, id))
                return Problems.Conflict("ปิดใช้งานไม่ได้ เพราะยังมีอุปกรณ์ที่ใช้อยู่");

            var from = new { item.Name, item.Active };
            if (req.Name is not null) item.Name = req.Name.Trim();
            if (req.Description is not null) item.Description = req.Description.Trim();
            if (req.Active is not null) item.Active = req.Active.Value;
            audit.Add($"{type.ToLower()}.update", type, id.ToString(), new { from, to = new { item.Name, item.Active } });
            if (await db.SaveOrConflict($"ชื่อ {item.Name} มีอยู่แล้ว") is { } conflict) return conflict;
            return Results.Ok(item);
        }).RequireAuthorization(Policies.SettingsManage);
    }

    private static IResult? Check(string? name, string? description)
    {
        var errors = new Errors();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) errors.Add("name", "กรุณากรอกชื่อ (ไม่เกิน 100 ตัวอักษร)");
        if (description?.Length > 500) errors.Add("description", "คำอธิบายต้องไม่เกิน 500 ตัวอักษร");
        return errors.Any ? errors.ToResult() : null;
    }

    private static void MapEmployees(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/employees");

        g.MapGet("/", async (AppDb db, HttpContext ctx, bool? includeInactive) =>
        {
            // Email is only needed by people who manage employees
            var canSeeEmail = Policies.RolesFor[Policies.EmployeeManage].Any(ctx.User.IsInRole);
            var items = await db.Employees.AsNoTracking()
                .Where(e => includeInactive == true || e.Active).OrderBy(e => e.DisplayName).ToListAsync();
            return Results.Ok(items.Select(e => new
            {
                e.Id, e.EmployeeNumber, e.DisplayName, e.Department, e.Active,
                Email = canSeeEmail ? e.Email : null,
            }));
        });

        g.MapPost("/", async (EmployeeRequest req, AppDb db, AuditService audit) =>
        {
            if (Check(req, requireAll: true) is { } err) return err;
            var emp = new Employee
            {
                EmployeeNumber = req.EmployeeNumber!.Trim(), DisplayName = req.DisplayName!.Trim(),
                Department = req.Department?.Trim(), Email = req.Email?.Trim(),
            };
            db.Employees.Add(emp);
            audit.Add("employee.create", "Employee", emp.Id.ToString(), new { emp.EmployeeNumber });
            if (await db.SaveOrConflict($"รหัสพนักงาน {emp.EmployeeNumber} มีอยู่แล้ว") is { } conflict) return conflict;
            return Results.Created($"/api/v1/employees/{emp.Id}", emp);
        }).RequireAuthorization(Policies.EmployeeManage);

        g.MapPatch("/{id:guid}", async (Guid id, EmployeeRequest req, AppDb db, AuditService audit) =>
        {
            var emp = await db.Employees.FindAsync(id);
            if (emp is null) return Problems.NotFound("พนักงาน");
            if (Check(req, requireAll: false) is { } err) return err;
            if (req.Active == false && emp.Active && await db.Assignments.AnyAsync(a => a.EmployeeId == id && a.CheckedInAt == null))
                return Problems.Conflict("ปิดใช้งานไม่ได้ เพราะพนักงานยังถืออุปกรณ์อยู่ กรุณารับคืนก่อน");

            if (req.EmployeeNumber is not null) emp.EmployeeNumber = req.EmployeeNumber.Trim();
            if (req.DisplayName is not null) emp.DisplayName = req.DisplayName.Trim();
            if (req.Department is not null) emp.Department = req.Department.Trim();
            if (req.Email is not null) emp.Email = req.Email.Trim();
            if (req.Active is not null) emp.Active = req.Active.Value;
            emp.UpdatedAt = DateTime.UtcNow;
            audit.Add("employee.update", "Employee", id.ToString(), new { emp.Active });
            if (await db.SaveOrConflict($"รหัสพนักงาน {emp.EmployeeNumber} มีอยู่แล้ว") is { } conflict) return conflict;
            return Results.Ok(emp);
        }).RequireAuthorization(Policies.EmployeeManage);
    }

    private static IResult? Check(EmployeeRequest req, bool requireAll)
    {
        var errors = new Errors();
        if (requireAll && string.IsNullOrWhiteSpace(req.EmployeeNumber)) errors.Add("employeeNumber", "กรุณากรอกรหัสพนักงาน");
        if (requireAll && string.IsNullOrWhiteSpace(req.DisplayName)) errors.Add("displayName", "กรุณากรอกชื่อ-สกุล");
        if (req.EmployeeNumber is not null && string.IsNullOrWhiteSpace(req.EmployeeNumber)) errors.Add("employeeNumber", "รหัสพนักงานห้ามว่าง");
        if (req.DisplayName is not null && string.IsNullOrWhiteSpace(req.DisplayName)) errors.Add("displayName", "ชื่อ-สกุลห้ามว่าง");
        if (!string.IsNullOrWhiteSpace(req.Email) && !req.Email.Contains('@')) errors.Add("email", "รูปแบบอีเมลไม่ถูกต้อง");
        return errors.Any ? errors.ToResult() : null;
    }
}
