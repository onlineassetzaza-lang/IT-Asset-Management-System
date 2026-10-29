# IT Asset Management — API

ASP.NET Core 8 (minimal API) + EF Core + SQLite. Modular monolith: Auth, Inventory, Assignments, Maintenance, Audit.

## Run

```powershell
cd api/src/ItAssetApi
$env:ITAM_ADMIN_PASSWORD = "1234"   # optional; if unset a random one is logged once on first start
$env:Security__RequireHttpsCookie = "false"                # local http only; keep true behind HTTPS
dotnet run --urls http://localhost:5080
```

The API also serves `../../web` (same origin, so no CORS). Database: `storage/itam.db` (override with `ConnectionStrings__Default`).
First start creates the admin (`ITAM_ADMIN_USERNAME`, default `admin`) and default categories. No credentials are compiled in.

## Test

```powershell
cd api
dotnet test      # 17 tests mapped to the MVP acceptance criteria
```

## Asset state rules

| From | Command | To | Notes |
|---|---|---|---|
| Available | check-out | InUse | creates the assignment in the same transaction |
| InUse | check-in | Available | closes the assignment |
| InUse | check-in (`needsMaintenance`) | Maintenance | also opens a maintenance record |
| Available | open maintenance | Maintenance | InUse must be checked in first |
| Maintenance | close maintenance | Available / Retired | Retired needs a reason |
| Available | retire | Retired | terminal; reason required |

`PATCH /assets/{id}` cannot change status. Assets are never deleted.

## Roles

| Permission | Admin | ITStaff | Auditor |
|---|:-:|:-:|:-:|
| Read assets/dashboard/employees | ✅ | ✅ | ✅ |
| Create/update/retire, check-out/in, maintenance | ✅ | ✅ | — |
| Manage employees | ✅ | ✅ | — |
| Manage categories/locations | ✅ | — | — |
| Manage users | ✅ | — | — |
| Read audit log | ✅ | — | ✅ |

## Security notes

- Cookie session (`HttpOnly`, `SameSite=Strict`, `Secure`); every state-changing `/api` call needs `X-Requested-With: itam`.
- Sessions are re-validated each request: deactivation, role or password change take effect immediately.
- Lockout after 5 failed logins (15 min). Password policy: ≥10 chars with letters and digits.
- Role change / deactivation / password reset need the acting admin's password again.
- Audit log is append-only (SQLite triggers) and stores allow-listed metadata only.
- Not yet implemented: MFA/SSO, EF migrations (schema is created with `EnsureCreated`), backup job.
