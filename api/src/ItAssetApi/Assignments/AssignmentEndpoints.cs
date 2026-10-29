using ItAssetApi.Audit;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Inventory;
using ItAssetApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Assignments;

public record CheckOutRequest(Guid? EmployeeId, DateTime? DueAt, string? ConditionOut, string? RequestKey);
public record CheckInRequest(string? ConditionIn, bool? NeedsMaintenance, string? Notes, string? RequestKey);

public record AssignmentDto(Guid Id, Guid AssetId, Guid EmployeeId, string EmployeeName, DateTime CheckedOutAt,
    DateTime? DueAt, DateTime? CheckedInAt, string? ConditionOut, string? ConditionIn, string? Notes);

public static class AssignmentEndpoints
{
    public static AssignmentDto ToDto(Assignment a) => new(a.Id, a.AssetId, a.EmployeeId, a.Employee.DisplayName,
        a.CheckedOutAt, a.DueAt, a.CheckedInAt, a.ConditionOut, a.ConditionIn, a.Notes);

    private static bool ValidKey(string? key) => key is { Length: >= 8 and <= 64 };

    public static void Map(RouteGroupBuilder assets)
    {
        // Check-out: Available -> InUse, creating the assignment in the same transaction
        assets.MapPost("/{id:guid}/check-outs", async (Guid id, CheckOutRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var actor = ctx.User.UserId();
            if (!ValidKey(req.RequestKey)) return Errors.Single("requestKey", "ต้องส่ง requestKey ยาว 8-64 ตัวอักษร");

            // Retried request: return the original result instead of creating a second assignment
            var replay = await db.Assignments.Include(a => a.Employee)
                .FirstOrDefaultAsync(a => a.CheckedOutBy == actor && a.OutRequestKey == req.RequestKey);
            if (replay is not null)
                return replay.AssetId == id && replay.EmployeeId == req.EmployeeId
                    ? Results.Ok(ToDto(replay))
                    : Problems.Conflict("requestKey นี้ถูกใช้กับคำขออื่นแล้ว");

            var asset = await db.Assets.FindAsync(id);
            if (asset is null) return Problems.NotFound("อุปกรณ์");
            if (asset.Status != AssetStatus.Available)
                return Problems.Conflict($"จ่ายอุปกรณ์ไม่ได้ เพราะสถานะปัจจุบันคือ {asset.Status}");

            var employee = req.EmployeeId is null ? null : await db.Employees.FindAsync(req.EmployeeId);
            if (employee is not { Active: true }) return Errors.Single("employeeId", "กรุณาเลือกพนักงานที่ใช้งานอยู่");
            if (req.DueAt is not null && req.DueAt.Value.ToUniversalTime() <= DateTime.UtcNow)
                return Errors.Single("dueAt", "วันกำหนดคืนต้องเป็นเวลาในอนาคต");

            var assignment = new Assignment
            {
                AssetId = id, EmployeeId = employee.Id, Employee = employee, CheckedOutBy = actor,
                DueAt = req.DueAt?.ToUniversalTime(), ConditionOut = req.ConditionOut?.Trim(), OutRequestKey = req.RequestKey,
            };
            db.Assignments.Add(assignment);
            asset.Status = AssetStatus.InUse;
            AssetEndpoints.Touch(asset, actor);
            audit.Add("asset.check_out", "Asset", id.ToString(), new { employeeId = employee.Id, assignmentId = assignment.Id });

            // A concurrent check-out loses on the asset version check or the one-active-assignment index
            if (await db.SaveOrConflict("อุปกรณ์ถูกจ่ายไปแล้วโดยรายการอื่น") is { } conflict) return conflict;
            return Results.Created($"/api/v1/assets/{id}/history", ToDto(assignment));
        }).RequireAuthorization(Policies.AssetWrite);

        // Check-in: InUse -> Available, or Maintenance (with a maintenance record) when the item is damaged
        assets.MapPost("/{id:guid}/check-ins", async (Guid id, CheckInRequest req, AppDb db, AuditService audit, HttpContext ctx) =>
        {
            var actor = ctx.User.UserId();
            if (!ValidKey(req.RequestKey)) return Errors.Single("requestKey", "ต้องส่ง requestKey ยาว 8-64 ตัวอักษร");

            var replay = await db.Assignments.Include(a => a.Employee)
                .FirstOrDefaultAsync(a => a.CheckedInBy == actor && a.InRequestKey == req.RequestKey);
            if (replay is not null)
                return replay.AssetId == id ? Results.Ok(ToDto(replay)) : Problems.Conflict("requestKey นี้ถูกใช้กับคำขออื่นแล้ว");

            var asset = await db.Assets.FindAsync(id);
            if (asset is null) return Problems.NotFound("อุปกรณ์");
            var assignment = await db.Assignments.Include(a => a.Employee)
                .FirstOrDefaultAsync(a => a.AssetId == id && a.CheckedInAt == null);
            if (assignment is null || asset.Status != AssetStatus.InUse)
                return Problems.Conflict("อุปกรณ์นี้ไม่ได้ถูกยืมอยู่ จึงรับคืนไม่ได้");

            var needsMaintenance = req.NeedsMaintenance == true;
            assignment.CheckedInAt = DateTime.UtcNow;
            assignment.CheckedInBy = actor;
            assignment.ConditionIn = req.ConditionIn?.Trim();
            assignment.Notes = req.Notes?.Trim();
            assignment.InRequestKey = req.RequestKey;

            asset.Status = needsMaintenance ? AssetStatus.Maintenance : AssetStatus.Available;
            AssetEndpoints.Touch(asset, actor);
            if (needsMaintenance)
            {
                db.MaintenanceRecords.Add(new MaintenanceRecord
                {
                    AssetId = id, ReportedBy = actor,
                    Issue = FirstNonBlank(req.ConditionIn, req.Notes) ?? "ชำรุดเมื่อรับคืน",
                });
            }
            audit.Add("asset.check_in", "Asset", id.ToString(),
                new { assignmentId = assignment.Id, employeeId = assignment.EmployeeId, toMaintenance = needsMaintenance });

            if (await db.SaveOrConflict("ไม่สามารถบันทึกการรับคืนได้") is { } conflict) return conflict;
            return Results.Ok(ToDto(assignment));
        }).RequireAuthorization(Policies.AssetWrite);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
