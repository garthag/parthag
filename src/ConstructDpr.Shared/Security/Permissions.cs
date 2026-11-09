namespace ConstructDpr.Shared.Security;

public enum ProjectRole
{
    ProjectAdmin, ProjectManager, ConstructionManager, PlanningEngineer, SiteEngineer, Supervisor,
    Storekeeper, QaQcEngineer, SafetyEngineer, CommercialEngineer, ClientReviewer, Integration, Auditor,
}

public enum Module { Project, Members, Masters, Schedule, Boq, Dpr, Reports }

[Flags]
public enum Act
{
    None = 0, Create = 1, View = 2, Edit = 4, Submit = 8, Approve = 16, Reject = 32, Export = 64, Delete = 128,
    All = Create | View | Edit | Submit | Approve | Reject | Export | Delete,
}

/// <summary>
/// Role × module × action matrix. The server enforces it on every call; the apps only use it to hide buttons.
/// Letters: C create · V view · E edit · S submit · A approve · R reject · X export · D delete.
/// </summary>
public static class Permissions
{
    private static readonly Module[] Order = [Module.Project, Module.Members, Module.Masters, Module.Schedule, Module.Boq, Module.Dpr, Module.Reports];

    private static readonly Dictionary<ProjectRole, string[]> Matrix = new()
    {
        //                                     project  members  masters  schedule  boq       dpr       reports
        [ProjectRole.ProjectAdmin]        = ["*",     "*",     "*",     "*",      "*",      "*",      "*"],
        [ProjectRole.ProjectManager]      = ["CVEX",  "CVED",  "CVEXD", "CVESARX","CVESARX","CVESARX","CVX"],
        [ProjectRole.ConstructionManager] = ["V",     "V",     "CVEX",  "VEX",    "VX",     "CVESARX","CVX"],
        [ProjectRole.PlanningEngineer]    = ["V",     "V",     "CVEXD", "CVESX",  "VX",     "CVESX",  "CVX"],
        [ProjectRole.SiteEngineer]        = ["V",     "V",     "V",     "V",      "V",      "CVESX",  "VX"],
        [ProjectRole.Supervisor]          = ["V",     "V",     "V",     "V",      "",       "CVES",   "V"],
        [ProjectRole.Storekeeper]         = ["V",     "V",     "V",     "",       "",       "CVS",    "V"],
        [ProjectRole.QaQcEngineer]        = ["V",     "V",     "V",     "V",      "",       "VS",     "VX"],
        [ProjectRole.SafetyEngineer]      = ["V",     "V",     "V",     "V",      "",       "VS",     "VX"],
        [ProjectRole.CommercialEngineer]  = ["V",     "V",     "V",     "V",      "CVESX",  "VX",     "VX"],
        [ProjectRole.ClientReviewer]      = ["V",     "",      "V",     "V",      "V",      "VARX",   "VX"],
        [ProjectRole.Integration]         = ["VX",    "V",     "CVEX",  "CVEX",   "CVEX",   "VX",     "VX"],
        [ProjectRole.Auditor]             = ["VX",    "V",     "VX",    "VX",     "VX",     "VX",     "VX"],
    };

    public static Act For(ProjectRole role, Module module)
    {
        var code = Matrix[role][Array.IndexOf(Order, module)];
        if (code == "*") return Act.All;
        var a = Act.None;
        foreach (var ch in code)
            a |= ch switch
            {
                'C' => Act.Create, 'V' => Act.View, 'E' => Act.Edit, 'S' => Act.Submit, 'A' => Act.Approve,
                'R' => Act.Reject, 'X' => Act.Export, 'D' => Act.Delete, _ => Act.None,
            };
        return a;
    }

    public static bool Can(ProjectRole role, Module module, Act action) => (For(role, module) & action) == action;

    public static string Label(ProjectRole r) => r switch
    {
        ProjectRole.ProjectAdmin => "Project administrator",
        ProjectRole.ProjectManager => "Project manager",
        ProjectRole.ConstructionManager => "Construction manager",
        ProjectRole.PlanningEngineer => "Planning engineer",
        ProjectRole.SiteEngineer => "Site engineer",
        ProjectRole.Supervisor => "Supervisor",
        ProjectRole.Storekeeper => "Storekeeper",
        ProjectRole.QaQcEngineer => "QA/QC engineer",
        ProjectRole.SafetyEngineer => "Safety engineer",
        ProjectRole.CommercialEngineer => "Commercial engineer",
        ProjectRole.ClientReviewer => "Client reviewer",
        ProjectRole.Integration => "Integration (API account)",
        _ => "Read-only auditor",
    };
}

/// <summary>Thrown when the signed-in person's role does not allow an action.</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>Thrown when a record was changed on another device since it was loaded.</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>Thrown when a record does not exist or is not visible to the caller.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Business rule violations (overrun, missing reason, wrong status, ...). Shown to the user as-is.</summary>
public sealed class WorkflowException(string message) : Exception(message);
