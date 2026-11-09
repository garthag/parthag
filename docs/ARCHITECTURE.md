# Architecture

```
   Android (MAUI Blazor Hybrid)     Browser (Blazor WebAssembly PWA)     Windows (WPF)
             │  ConstructDpr.UI screens (shared)  │                         │
             └──────────── ConstructDpr.Client: RemoteApi (HTTPS) + LiveClient (SignalR) ──┘          │ offline mode
                                       │                                                     LocalApi on SQLite
                              ConstructDpr.Server (ASP.NET Core)
                     Identity bearer tokens · REST /api · SignalR /hubs/live · serves the web app
                                       │
                         LocalApi  ── the rules: roles, versions, workflow ──  ConstructDpr.Core
                                       │                 │
                            EF Core (PostgreSQL "cdpr" / SQLite)   QuestPDF · ClosedXML (ConstructDpr.Reports)
                                       │
                 ConstructDpr.Shared: entities · CPM · Gantt layout · progress maths · permissions · IConstructDprApi
```

## Key decisions

* **One contract, two implementations.** `IConstructDprApi` (Shared) is implemented by `LocalApi` (Core, in-process over a database) and `RemoteApi` (Client, over HTTPS). The server is a thin layer that exposes LocalApi for the signed-in person. The offline Windows app runs LocalApi directly on SQLite, so the same rules apply everywhere.
* **Rules live on the server.** Overrun checks, self-approval, rejection reasons, immutable approvals, BOQ limits, circular-logic detection and permissions are all in `LocalApi`/`DprService`. The apps compute numbers while you type only for convenience; the server recomputes them on every save.
* **Optimistic concurrency.** Every record has a `Version`. A save from a stale copy is refused with a clear message instead of silently overwriting newer work from another device.
* **Live sync.** After every committed change the server sends a `LiveChange` to the project's SignalR group. Apps reload the affected screen, and after a reconnect they reload everything they may have missed.
* **Exact numbers.** Quantities and money are `decimal`, stored as `numeric(20,6)` in PostgreSQL or exact text in SQLite.
* **Tamper evidence.** An approved report stores a SHA-256 hash of its content. Every view verifies it.
* **Own schema.** On PostgreSQL all tables are in schema `cdpr`, so an existing database (e.g. Supabase from the earlier edition) can be reused safely.
* **Shared screens.** The web and Android apps render the same Razor components (`ConstructDpr.UI`). The Windows app keeps native WPF screens, including a DrawingContext-based Gantt control. Both Gantt charts draw the geometry from `GanttLayout` (Shared), which the PDF report uses too.

## Request flow (approve a report)

1. The reviewer taps **Approve** and RemoteApi sends `POST /api/projects/{p}/dprs/{d}/approve {accepted}` with a bearer token.
2. The server's ApiFactory resolves the person, and `LocalApi.ApproveDprAsync` checks DPR approve permission.
3. In one transaction it sets the accepted quantities, validates (overrun, self-approval), stores the snapshot hash, updates activity progress and actual dates, recalculates CPM and writes the audit entry.
4. The server sends `changed {projectId, entity: "dpr", action: "approve"}` to the project group, and every open dashboard, list and Gantt chart reloads.
