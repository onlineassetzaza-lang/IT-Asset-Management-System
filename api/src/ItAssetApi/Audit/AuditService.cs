using System.Text.Json;
using ItAssetApi.Data;
using ItAssetApi.Domain;
using ItAssetApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Audit;

/// <summary>
/// Stages audit events on the current DbContext so they commit in the same SaveChanges as the
/// business change. Callers must pass allow-listed metadata only (no credentials or free text).
/// </summary>
public class AuditService(AppDb db, IHttpContextAccessor http)
{
    public void Add(string action, string targetType, string? targetId, object? metadata = null,
        string outcome = "success", Guid? actor = null)
    {
        var ctx = http.HttpContext;
        var actorId = actor ?? ctx?.User.UserId();
        db.AuditEvents.Add(new AuditEvent
        {
            ActorUserId = actorId == Guid.Empty ? null : actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Outcome = outcome,
            RequestId = ctx?.TraceIdentifier ?? "",
            SourceIp = ctx?.Connection.RemoteIpAddress?.ToString(),
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
        });
    }
}

public static class AuditEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/audit-events", async (AppDb db, Guid? actor, string? action, DateTime? from, DateTime? to,
            string? targetId, int? page, int? pageSize) =>
        {
            var q = db.AuditEvents.AsNoTracking().AsQueryable();
            if (actor is not null) q = q.Where(e => e.ActorUserId == actor);
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(e => e.Action == action);
            if (from is not null) q = q.Where(e => e.OccurredAt >= from.Value.ToUniversalTime());
            if (to is not null) q = q.Where(e => e.OccurredAt <= to.Value.ToUniversalTime());
            if (!string.IsNullOrWhiteSpace(targetId)) q = q.Where(e => e.TargetId == targetId);

            var (p, size) = Paging.Normalize(page, pageSize);
            var total = await q.CountAsync();
            var items = await q.OrderByDescending(e => e.Id).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items, total, page = p, pageSize = size });
        }).RequireAuthorization(Policies.AuditRead);
    }
}
