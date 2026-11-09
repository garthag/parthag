# Third-party software

CONSTRUCT DPR is licensed under Apache-2.0 (see LICENSE). It uses:

| Component | Used for | Licence |
|---|---|---|
| .NET 10, ASP.NET Core, Blazor, WPF, .NET MAUI | Runtime, server, apps | MIT |
| Entity Framework Core, Microsoft.Data.Sqlite, ASP.NET Core Identity, SignalR | Database, sign-in, live updates | MIT |
| Npgsql + Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL driver | PostgreSQL Licence |
| SQLite (via SQLitePCLRaw) | Local database | Public domain / Apache-2.0 |
| QuestPDF | PDF reports | QuestPDF Community licence: free for individuals, non-profits, open-source projects and organisations with under USD 1 million annual gross revenue; others need a commercial licence (questpdf.com/license) |
| ClosedXML (+ DocumentFormat.OpenXml) | Excel export and import | MIT |
| xUnit, Microsoft.AspNetCore.Mvc.Testing | Tests (not shipped) | Apache-2.0 / MIT |

Services you may choose: Supabase or Neon (PostgreSQL hosting), Render or Azure (server hosting). Each has its own terms. Components of the earlier editions are listed in legacy/THIRD_PARTY_LICENSES.md.
