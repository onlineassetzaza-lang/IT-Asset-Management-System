using ItAssetApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi;

public static class Paging
{
    public static (int Page, int Size) Normalize(int? page, int? size) =>
        (Math.Max(page ?? 1, 1), Math.Clamp(size ?? 10, 1, 100));
}

public static class Problems
{
    public static IResult Conflict(string detail) => Results.Problem(title: "Conflict", detail: detail, statusCode: 409);
    public static IResult NotFound(string what) => Results.Problem(title: "Not Found", detail: $"ไม่พบ{what}", statusCode: 404);
    public static IResult Forbidden(string detail) => Results.Problem(title: "Forbidden", detail: detail, statusCode: 403);
}

/// <summary>Collects field validation errors and returns a 422 problem document.</summary>
public class Errors
{
    private readonly Dictionary<string, string[]> _errors = [];
    public bool Any => _errors.Count > 0;
    public Errors Add(string field, string message) { _errors[field] = [message]; return this; }
    public IResult ToResult() => Results.ValidationProblem(_errors, statusCode: 422);
    public static IResult Single(string field, string message) => new Errors().Add(field, message).ToResult();
}

public static class DbExtensions
{
    /// <summary>Saves; maps optimistic-concurrency and unique-constraint failures to 409 instead of 500.</summary>
    public static async Task<IResult?> SaveOrConflict(this AppDb db, string constraintMessage)
    {
        try { await db.SaveChangesAsync(); return null; }
        catch (DbUpdateConcurrencyException)
        {
            return Problems.Conflict("ข้อมูลถูกแก้ไขโดยผู้อื่นแล้ว กรุณาโหลดข้อมูลใหม่แล้วลองอีกครั้ง");
        }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return Problems.Conflict(constraintMessage);
        }
    }
}
