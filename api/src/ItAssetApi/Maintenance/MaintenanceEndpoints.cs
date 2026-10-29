using ItAssetApi.Audit;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Inventory;
using ItAssetApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Maintenance;

public record OpenMaintenanceRequest(string? Provider, string? Issue);
public record CloseMaintenanceRequest(string? Resolution, decimal? Cost, string? Outcome, string? RetireReason);

public static class MaintenanceEndpoints
{
    public static void Map(RouteGroupBuilder assets, RouteGroupBuilder api)
    {
        // Available -> Maintenance. An asset that is checked out must be checked in first.
        assets.MapPost("/{id:guid}/maintenance", async (Guid id, OpenMaintenanceRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var asset = await db.Assets.FindAsync(id);
            if (asset is null) return Problems.NotFound("อุปกรณ์");
            if (string.IsNullOrWhiteSpace(req.Issue)) return Errors.Single("issue", "กรุณากรอกอาการ/ปัญหา");
            if (asset.Status != AssetStatus.Available)
                return Problems.Conflict($"ส่งซ่อมไม่ได้ เพราะสถานะปัจจุบันคือ {asset.Status} (ต้องเป็น Available)");

            var actor = ctx.User.UserId();
            var record = new MaintenanceRecord { AssetId = id, ReportedBy = actor, Provider = req.Provider?.Trim(), Issue = req.Issue.Trim() };
            db.MaintenanceRecords.Add(record);
            asset.Status = AssetStatus.Maintenance;
            AssetEndpoints.Touch(asset, actor);
            audit.Add("maintenance.open", "Asset", id.ToString(), new { maintenanceId = record.Id });
            if (await db.SaveOrConflict("อุปกรณ์นี้มีงานซ่อมที่ยังเปิดอยู่แล้ว") is { } conflict) return conflict;
            return Results.Created($"/api/v1/maintenance/{record.Id}", record);
        }).RequireAuthorization(Policies.AssetWrite);

        var m = api.MapGroup("/maintenance");

        m.MapGet("/", async (AppDb db, bool? open, int? page, int? pageSize) =>
        {
            var q = db.MaintenanceRecords.AsNoTracking().AsQueryable();
            if (open == true) q = q.Where(r => r.ClosedAt == null);
            if (open == false) q = q.Where(r => r.ClosedAt != null);
            var (p, size) = Paging.Normalize(page, pageSize);
            var total = await q.CountAsync();
            var items = await q.OrderByDescending(r => r.OpenedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items, total, page = p, pageSize = size });
        });

        // Close: Maintenance -> Available or Retired
        m.MapPost("/{id:guid}/close", async (Guid id, CloseMaintenanceRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var record = await db.MaintenanceRecords.FindAsync(id);
            if (record is null) return Problems.NotFound("งานซ่อม");
            if (record.ClosedAt is not null) return Problems.Conflict("งานซ่อมนี้ปิดไปแล้ว");

            var errors = new Errors();
            if (req.Outcome is not ("Available" or "Retired")) errors.Add("outcome", "ผลลัพธ์ต้องเป็น Available หรือ Retired");
            if (req.Outcome == "Retired" && (req.RetireReason is null || !AssetEndpoints.RetireReasons.Contains(req.RetireReason)))
                errors.Add("retireReason", $"ต้องระบุเหตุผลเลิกใช้งาน: {string.Join(", ", AssetEndpoints.RetireReasons)}");
            if (req.Cost is < 0) errors.Add("cost", "ค่าใช้จ่ายต้องไม่ติดลบ");
            if (errors.Any) return errors.ToResult();

            var asset = await db.Assets.FirstAsync(a => a.Id == record.AssetId);
            var actor = ctx.User.UserId();
            record.ClosedAt = DateTime.UtcNow;
            record.Resolution = req.Resolution?.Trim();
            record.Cost = req.Cost;
            if (req.Outcome == "Retired") AssetEndpoints.Retire(asset, req.RetireReason!, actor);
            else { asset.Status = AssetStatus.Available; AssetEndpoints.Touch(asset, actor); }

            audit.Add("maintenance.close", "Asset", asset.Id.ToString(), new { maintenanceId = id, outcome = req.Outcome });
            if (await db.SaveOrConflict("ไม่สามารถปิดงานซ่อมได้") is { } conflict) return conflict;
            return Results.Ok(record);
        }).RequireAuthorization(Policies.AssetWrite);
    }
}
