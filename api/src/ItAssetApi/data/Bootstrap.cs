using System.Security.Cryptography;
using ItAssetApi.Domain;
using Microsoft.AspNetCore.Identity;

namespace ItAssetApi.Data;

public static class Bootstrap
{
    /// <summary>
    /// First run only: creates the initial Admin and default categories. The Admin password comes from
    /// ITAM_ADMIN_PASSWORD; if unset a random one is generated and logged exactly once. No default
    /// credential is ever compiled in.
    /// </summary>
    public static void Seed(AppDb db, IPasswordHasher<User> hasher, IConfiguration config, ILogger logger)
    {
        if (!db.Users.Any())
        {
            var username = (config["ITAM_ADMIN_USERNAME"] ?? "admin").Trim().ToLowerInvariant();
            var password = config["ITAM_ADMIN_PASSWORD"];
            var generated = string.IsNullOrEmpty(password);
            if (generated) password = RandomNumberGenerator.GetHexString(20, lowercase: false) + "a1";

            var admin = new User { Username = username, DisplayName = "ผู้ดูแลระบบ", Role = Roles.Admin };
            admin.PasswordHash = hasher.HashPassword(admin, password!);
            db.Users.Add(admin);
            db.SaveChanges();

            if (generated)
                logger.LogWarning("Initial admin created. Username: {Username}  Password: {Password}  (shown once — change it after first login)", username, password);
            else
                logger.LogInformation("Initial admin '{Username}' created from configuration.", username);
        }

        if (!db.Categories.Any())
        {
            db.Categories.AddRange(new[] { "Notebook", "Monitor", "Mouse", "Keyboard", "Printer", "Other" }
                .Select(n => new Category { Name = n }));
            db.SaveChanges();
        }
    }
}
