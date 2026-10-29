using System.Text.RegularExpressions;
using ItAssetApi.Audit;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Inventory;

public record HolderDto(Guid EmployeeId, string EmployeeName, DateTime CheckedOutAt, DateTime? DueAt);
public record AssetDto(Guid Id, string AssetTag, string Name, Guid CategoryId, string CategoryName, string? SerialNumber,
    DateOnly? PurchaseDate, AssetStatus Status, Guid? LocationId, string? LocationName, string? Notes, int Version,
    string? RetireReason, DateTime? RetiredAt, HolderDto? Holder);

public record CreateAssetRequest(string? AssetTag, string? Name, Guid? CategoryId, string? SerialNumber,
    DateOnly? PurchaseDate, Guid? LocationId, string? Notes);
/// <summary>Status is deliberately absent: it only changes through business commands. Null = unchanged, "" = clear.</summary>
public record UpdateAssetRequest(int? Version, string? Name, Guid? CategoryId, string? SerialNumber,
    DateOnly? PurchaseDate, Guid? LocationId, string? Notes);
public record RetireRequest(int? Version, string? Reason);

public static partial class AssetEndpoints
{
    public static readonly string[] RetireReasons = ["Lost", "Stolen", "Disposed", "EndOfLife", "Other"];

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._-]{1,31}$")]
    private static partial Regex TagPattern();

    /// <summary>Loads assets with category, location and current holder (two queries, no join projection).</summary>
    public static async Task<List<AssetDto>> ToDtos(AppDb db, IQueryable<Asset> assets)
    {
        var rows = await assets.AsNoTracking().Include(a => a.Category).Include(a => a.Location).ToListAsync();
        var ids = rows.Select(a => a.Id).ToList();
        var holders = await db.Assignments.AsNoTracking().Include(a => a.Employee)
            .Where(a => a.CheckedInAt == null && ids.Contains(a.AssetId)).ToDictionaryAsync(a => a.AssetId);
        return rows.Select(a => new AssetDto(a.Id, a.AssetTag, a.Name, a.CategoryId, a.Category.Name, a.SerialNumber,
            a.PurchaseDate, a.Status, a.LocationId, a.Location?.Name, a.Notes, a.Version, a.RetireReason, a.RetiredAt,
            holders.TryGetValue(a.Id, out var h) ? new HolderDto(h.EmployeeId, h.Employee.DisplayName, h.CheckedOutAt, h.DueAt) : null)).ToList();
    }

    public static async Task<AssetDto?> LoadDto(AppDb db, Guid id) =>
        (await ToDtos(db, db.Assets.Where(a => a.Id == id))).FirstOrDefault();

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/assets");

        g.MapGet("/", async (AppDb db, string? query, AssetStatus? status, Guid? categoryId, Guid? locationId,
            int? page, int? pageSize) =>
        {
            var q = db.Assets.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var text = query.Trim().ToLower();   // literal, case-insensitive match (no LIKE wildcards)
                q = q.Where(a => a.AssetTag.ToLower().Contains(text) || a.Name.ToLower().Contains(text));
            }
            if (status is not null) q = q.Where(a => a.Status == status);
            if (categoryId is not null) q = q.Where(a => a.CategoryId == categoryId);
            if (locationId is not null) q = q.Where(a => a.LocationId == locationId);

            var (p, size) = Paging.Normalize(page, pageSize);
            var total = await q.CountAsync();
            var items = await ToDtos(db, q.OrderBy(a => a.AssetTag).Skip((p - 1) * size).Take(size));
            return Results.Ok(new { items, total, page = p, pageSize = size });
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDb db) =>
            await LoadDto(db, id) is { } dto ? Results.Ok(dto) : Problems.NotFound("อุปกรณ์"));

        g.MapPost("/", async (CreateAssetRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var errors = new Errors();
            var tag = (req.AssetTag ?? "").Trim().ToUpperInvariant();
            if (!TagPattern().IsMatch(tag)) errors.Add("assetTag", "รหัสอุปกรณ์ใช้ A-Z 0-9 . _ - ยาว 2-32 ตัวอักษร");
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) errors.Add("name", "กรุณากรอกชื่ออุปกรณ์ (ไม่เกิน 200 ตัวอักษร)");
            if (req.CategoryId is null || !await db.Categories.AnyAsync(c => c.Id == req.CategoryId && c.Active))
                errors.Add("categoryId", "กรุณาเลือกหมวดหมู่ที่ใช้งานอยู่");
            if (req.LocationId is not null && !await db.Locations.AnyAsync(l => l.Id == req.LocationId && l.Active))
                errors.Add("locationId", "สถานที่ไม่ถูกต้อง");
            if (errors.Any) return errors.ToResult();

            var actor = ctx.User.UserId();
            var asset = new Asset
            {
                AssetTag = tag, Name = req.Name!.Trim(), CategoryId = req.CategoryId!.Value,
                SerialNumber = NullIfBlank(req.SerialNumber), PurchaseDate = req.PurchaseDate,
                LocationId = req.LocationId, Notes = NullIfBlank(req.Notes),
                CreatedBy = actor, UpdatedBy = actor,
            };
            db.Assets.Add(asset);
            audit.Add("asset.create", "Asset", asset.Id.ToString(), new { assetTag = tag });
            if (await db.SaveOrConflict("รหัสอุปกรณ์หรือ Serial Number นี้มีอยู่ในระบบแล้ว") is { } conflict) return conflict;
            return Results.Created($"/api/v1/assets/{asset.Id}", await LoadDto(db, asset.Id));
        }).RequireAuthorization(Policies.AssetWrite);

        g.MapPatch("/{id:guid}", async (Guid id, UpdateAssetRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var asset = await db.Assets.FindAsync(id);
            if (asset is null) return Problems.NotFound("อุปกรณ์");
            if (req.Version is null) return Errors.Single("version", "ต้องส่ง version ล่าสุดมาด้วย");
            if (req.Version != asset.Version) return Problems.Conflict("ข้อมูลถูกแก้ไขโดยผู้อื่นแล้ว กรุณาโหลดข้อมูลใหม่แล้วลองอีกครั้ง");
            if (asset.Status == AssetStatus.Retired) return Problems.Conflict("อุปกรณ์ที่เลิกใช้งานแล้วแก้ไขไม่ได้");

            var errors = new Errors();
            if (req.Name is not null && (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)) errors.Add("name", "ชื่ออุปกรณ์ไม่ถูกต้อง");
            if (req.CategoryId is not null && !await db.Categories.AnyAsync(c => c.Id == req.CategoryId && c.Active)) errors.Add("categoryId", "หมวดหมู่ไม่ถูกต้อง");
            if (req.LocationId is not null && !await db.Locations.AnyAsync(l => l.Id == req.LocationId && l.Active)) errors.Add("locationId", "สถานที่ไม่ถูกต้อง");
            if (errors.Any) return errors.ToResult();

            // Audit records which fields changed (and old/new for non-sensitive ids), never free-text values
            var changed = new List<string>();
            var meta = new Dictionary<string, object?>();
            if (req.Name is not null && req.Name.Trim() != asset.Name) { asset.Name = req.Name.Trim(); changed.Add("name"); }
            if (req.CategoryId is not null && req.CategoryId != asset.CategoryId) { meta["categoryId"] = new { from = asset.CategoryId, to = req.CategoryId }; asset.CategoryId = req.CategoryId.Value; changed.Add("categoryId"); }
            if (req.LocationId is not null && req.LocationId != asset.LocationId) { meta["locationId"] = new { from = asset.LocationId, to = req.LocationId }; asset.LocationId = req.LocationId; changed.Add("locationId"); }
            if (req.SerialNumber is not null && NullIfBlank(req.SerialNumber) != asset.SerialNumber) { asset.SerialNumber = NullIfBlank(req.SerialNumber); changed.Add("serialNumber"); }
            if (req.PurchaseDate is not null && req.PurchaseDate != asset.PurchaseDate) { asset.PurchaseDate = req.PurchaseDate; changed.Add("purchaseDate"); }
            if (req.Notes is not null && NullIfBlank(req.Notes) != asset.Notes) { asset.Notes = NullIfBlank(req.Notes); changed.Add("notes"); }

            if (changed.Count > 0)
            {
                meta["fields"] = changed;
                asset.Version++;
                asset.UpdatedAt = DateTime.UtcNow;
                asset.UpdatedBy = ctx.User.UserId();
                audit.Add("asset.update", "Asset", id.ToString(), meta);
                if (await db.SaveOrConflict("Serial Number นี้มีอยู่ในระบบแล้ว") is { } conflict) return conflict;
            }
            return Results.Ok(await LoadDto(db, id));
        }).RequireAuthorization(Policies.AssetWrite);

        // Assets with history are never deleted; retiring is the only way to take one out of service
        g.MapPost("/{id:guid}/retire", async (Guid id, RetireRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var asset = await db.Assets.FindAsync(id);
            if (asset is null) return Problems.NotFound("อุปกรณ์");
            if (req.Reason is null || !RetireReasons.Contains(req.Reason))
                return Errors.Single("reason", $"เหตุผลต้องเป็นหนึ่งใน {string.Join(", ", RetireReasons)}");
            if (req.Version is null) return Errors.Single("version", "ต้องส่ง version ล่าสุดมาด้วย");
            if (req.Version != asset.Version) return Problems.Conflict("ข้อมูลถูกแก้ไขโดยผู้อื่นแล้ว กรุณาโหลดข้อมูลใหม่แล้วลองอีกครั้ง");
            if (asset.Status != AssetStatus.Available)
                return Problems.Conflict("เลิกใช้งานได้เฉพาะอุปกรณ์ที่ว่าง (Available) — ถ้ากำลังถูกยืมให้รับคืนก่อน ถ้ากำลังซ่อมให้ปิดงานซ่อมโดยเลือก Retired");

            Retire(asset, req.Reason, ctx.User.UserId());
            audit.Add("asset.retire", "Asset", id.ToString(), new { reason = req.Reason });
            if (await db.SaveOrConflict("ไม่สามารถบันทึกได้") is { } conflict) return conflict;
            return Results.Ok(await LoadDto(db, id));
        }).RequireAuthorization(Policies.AssetWrite);

        g.MapGet("/{id:guid}/history", async (Guid id, AppDb db) =>
        {
            if (!await db.Assets.AnyAsync(a => a.Id == id)) return Problems.NotFound("อุปกรณ์");
            var assignments = (await db.Assignments.AsNoTracking().Include(a => a.Employee).Where(a => a.AssetId == id)
                .OrderByDescending(a => a.CheckedOutAt).ToListAsync()).Select(Assignments.AssignmentEndpoints.ToDto);
            var maintenance = await db.MaintenanceRecords.AsNoTracking().Where(m => m.AssetId == id)
                .OrderByDescending(m => m.OpenedAt).ToListAsync();
            return Results.Ok(new { assignments, maintenance });
        });

        Assignments.AssignmentEndpoints.Map(g);
        Maintenance.MaintenanceEndpoints.Map(g, api);
    }

    public static void Retire(Asset asset, string reason, Guid actor)
    {
        asset.Status = AssetStatus.Retired;
        asset.RetireReason = reason;
        asset.RetiredAt = DateTime.UtcNow;
        Touch(asset, actor);
    }

    /// <summary>Marks an asset changed: bumps the concurrency token and stamps who/when.</summary>
    public static void Touch(Asset asset, Guid actor)
    {
        asset.Version++;
        asset.UpdatedAt = DateTime.UtcNow;
        asset.UpdatedBy = actor;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
