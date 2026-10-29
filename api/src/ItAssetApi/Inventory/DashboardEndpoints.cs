using ItAssetApi.Data;
using ItAssetApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Inventory;

public static class DashboardEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/dashboard/summary", async (AppDb db) =>
        {
            var byStatus = await db.Assets.AsNoTracking().GroupBy(a => a.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
            int Count(AssetStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

            var byCategory = await db.Assets.AsNoTracking().Where(a => a.Status != AssetStatus.Retired)
                .GroupBy(a => a.Category.Name).Select(g => new { Category = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ToListAsync();

            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                total = byStatus.Sum(x => x.Count),
                available = Count(AssetStatus.Available),
                inUse = Count(AssetStatus.InUse),
                maintenance = Count(AssetStatus.Maintenance),
                retired = Count(AssetStatus.Retired),
                overdue = await db.Assignments.CountAsync(a => a.CheckedInAt == null && a.DueAt != null && a.DueAt < now),
                openMaintenance = await db.MaintenanceRecords.CountAsync(r => r.ClosedAt == null),
                byCategory,
            });
        });
    }
}
