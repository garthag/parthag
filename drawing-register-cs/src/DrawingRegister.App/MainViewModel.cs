using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using DrawingRegister.Core;

namespace DrawingRegister.App;

/// <summary>What the view model needs from the window (dialogs, pickers, messages).</summary>
public interface IUiService
{
    Project? EditProject(Project project, bool isNew);
    Drawing? EditDrawing(Drawing drawing);
    IReadOnlyList<string> PickPdfFiles();
    string? PickFolder();
    string? PickCsvSaveLocation(string suggestedName);
    bool Confirm(string message, string title);
    void ShowError(string message);
    void ShowImportResult(ImportSummary summary);
}

public sealed class MainViewModel : ObservableObject
{
    public const string AllOption = "(All)";

    private readonly ProjectRepository _projects;
    private readonly DrawingRepository _drawings;
    private readonly ImportService _import;
    private readonly IUiService _ui;
    private Project? _selectedProject;
    private Drawing? _selectedDrawing;
    private Revision? _selectedRevision;
    private string _searchText = "";
    private string _disciplineFilter = AllOption;
    private string _statusFilter = AllOption;
    private string _statusText = "Ready";
    private bool _isBusy;
    private double _progress;
    private string? _previewPath;

    public MainViewModel(AppPaths paths, Database db, IUiService ui)
    {
        Paths = paths;
        _projects = new ProjectRepository(db);
        _drawings = new DrawingRepository(db);
        _import = new ImportService(db, paths);
        _ui = ui;

        AddProjectCommand = new RelayCommand(AddProject);
        EditProjectCommand = new RelayCommand(EditProject, () => SelectedProject is not null);
        DeleteProjectCommand = new RelayCommand(DeleteProject, () => SelectedProject is not null);
        UploadFilesCommand = new AsyncCommand(UploadFilesAsync, CanUpload);
        UploadFolderCommand = new AsyncCommand(UploadFolderAsync, CanUpload);
        EditDrawingCommand = new RelayCommand(EditDrawing, () => SelectedDrawing is not null);
        DeleteDrawingCommand = new RelayCommand(DeleteDrawing, () => SelectedDrawing is not null);
        OpenPdfCommand = new RelayCommand(OpenPdf, () => PreviewPath is not null);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => PreviewPath is not null);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => SelectedProject is not null && Drawings.Count > 0);
        OpenDataFolderCommand = new RelayCommand(() => OpenShell(Paths.Root));
        RefreshCommand = new RelayCommand(Refresh);
    }

    public AppPaths Paths { get; }
    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<Drawing> Drawings { get; } = [];
    public ObservableCollection<Revision> Revisions { get; } = [];
    public IReadOnlyList<string> DisciplineOptions { get; } = [AllOption, .. FileNameParser.Disciplines];
    public IReadOnlyList<string> StatusOptions { get; } = [AllOption, .. DrawingStatus.All];

    public ICommand AddProjectCommand { get; }
    public ICommand EditProjectCommand { get; }
    public ICommand DeleteProjectCommand { get; }
    public ICommand UploadFilesCommand { get; }
    public ICommand UploadFolderCommand { get; }
    public ICommand EditDrawingCommand { get; }
    public ICommand DeleteDrawingCommand { get; }
    public ICommand OpenPdfCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (Set(ref _selectedProject, value))
            {
                LoadDrawings();
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(ProjectHeader));
            }
        }
    }

    public bool HasProject => SelectedProject is not null;

    public string ProjectHeader => SelectedProject is null
        ? "Select or add a project"
        : $"{SelectedProject.Display}{(string.IsNullOrEmpty(SelectedProject.Client) ? "" : "  ·  " + SelectedProject.Client)}";

    public Drawing? SelectedDrawing
    {
        get => _selectedDrawing;
        set
        {
            if (Set(ref _selectedDrawing, value))
            {
                LoadRevisions();
                OnPropertyChanged(nameof(HasDrawing));
            }
        }
    }

    public bool HasDrawing => SelectedDrawing is not null;

    public Revision? SelectedRevision
    {
        get => _selectedRevision;
        set
        {
            if (Set(ref _selectedRevision, value))
            {
                PreviewPath = value?.File is { } f ? Paths.AbsoluteStoredPath(f.StoredPath) : null;
            }
        }
    }

    /// <summary>Absolute path of the PDF shown in the preview pane.</summary>
    public string? PreviewPath
    {
        get => _previewPath;
        private set => Set(ref _previewPath, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                LoadDrawings();
            }
        }
    }

    public string DisciplineFilter
    {
        get => _disciplineFilter;
        set
        {
            if (Set(ref _disciplineFilter, value))
            {
                LoadDrawings();
            }
        }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (Set(ref _statusFilter, value))
            {
                LoadDrawings();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public void LoadProjects(long? selectId = null)
    {
        var keep = selectId ?? SelectedProject?.Id;
        Projects.Clear();
        foreach (var p in _projects.List())
        {
            Projects.Add(p);
        }

        SelectedProject = Projects.FirstOrDefault(p => p.Id == keep) ?? Projects.FirstOrDefault();
        StatusText = Projects.Count == 0 ? "No projects yet – click \"Add project\" to start." : StatusText;
    }

    public void LoadDrawings(long? selectId = null)
    {
        var keep = selectId ?? SelectedDrawing?.Id;
        Drawings.Clear();
        if (SelectedProject is null)
        {
            SelectedDrawing = null;
            return;
        }

        var query = new DrawingQuery(
            SelectedProject.Id,
            SearchText,
            DisciplineFilter == AllOption ? null : DisciplineFilter,
            StatusFilter == AllOption ? null : StatusFilter);
        foreach (var d in _drawings.List(query))
        {
            Drawings.Add(d);
        }

        SelectedDrawing = Drawings.FirstOrDefault(d => d.Id == keep);
        StatusText = $"{Drawings.Count} drawing(s) in {SelectedProject.Code}";
    }

    private void LoadRevisions()
    {
        Revisions.Clear();
        if (SelectedDrawing is null)
        {
            SelectedRevision = null;
            return;
        }

        foreach (var r in _drawings.Revisions(SelectedDrawing.Id))
        {
            Revisions.Add(r);
        }

        SelectedRevision = Revisions.FirstOrDefault();
    }

    private void Refresh() => LoadProjects();

    private void AddProject()
    {
        var saved = EditUntilValid(new Project(), p => _ui.EditProject(p, isNew: true), _projects.Save);
        if (saved is null)
        {
            return;
        }

        LoadProjects(saved.Id);
        StatusText = $"Project {saved.Code} created – now upload drawings.";
    }

    private void EditProject()
    {
        if (SelectedProject is null)
        {
            return;
        }

        var copy = _projects.Get(SelectedProject.Id);
        var saved = copy is null ? null : EditUntilValid(copy, p => _ui.EditProject(p, isNew: false), _projects.Save);
        if (saved is not null)
        {
            LoadProjects(saved.Id);
        }
    }

    private void DeleteProject()
    {
        if (SelectedProject is not { } p ||
            !_ui.Confirm($"Delete project {p.Code} and all {p.DrawingCount} drawing(s) in it?\nThis cannot be undone.", "Delete project"))
        {
            return;
        }

        PreviewPath = null;
        _import.DeleteStoredFiles(_projects.Delete(p.Id));
        LoadProjects();
        StatusText = $"Project {p.Code} deleted.";
    }

    private bool CanUpload() => SelectedProject is not null && !IsBusy;

    private Task UploadFilesAsync()
    {
        var files = _ui.PickPdfFiles();
        return files.Count == 0 ? Task.CompletedTask : ImportAsync(files.Select(f => new ImportSource(f)).ToList());
    }

    private Task UploadFolderAsync()
    {
        var folder = _ui.PickFolder();
        return folder is null ? Task.CompletedTask : ImportPathsAsync([folder]);
    }

    /// <summary>Import dropped or picked paths; folders are scanned recursively.</summary>
    public Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var sources = new List<ImportSource>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                sources.AddRange(ImportService.CollectFolder(path));
            }
            else if (File.Exists(path))
            {
                sources.Add(new ImportSource(path));
            }
        }

        if (sources.Count == 0)
        {
            _ui.ShowError("No PDF files were found in the selection.");
            return Task.CompletedTask;
        }

        return ImportAsync(sources);
    }

    private async Task ImportAsync(List<ImportSource> sources)
    {
        if (SelectedProject is not { } project)
        {
            _ui.ShowError("Select or add a project first.");
            return;
        }

        IsBusy = true;
        Progress = 0;
        StatusText = $"Uploading {sources.Count} file(s) to {project.Code}…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress = p.Done * 100.0 / p.Total;
            StatusText = $"Uploading {p.Done} of {p.Total} to {project.Code}…";
        });
        try
        {
            var summary = await Task.Run(() => _import.Import(project.Id, sources, progress));
            var lastDrawing = summary.Items.LastOrDefault(i => i.DrawingId is not null)?.DrawingId;
            LoadProjects(project.Id);
            LoadDrawings(lastDrawing);
            StatusText = $"Upload finished: {summary.Created} new, {summary.NewRevisions} new revision(s), " +
                         $"{summary.Duplicates} duplicate(s), {summary.Failed} failed.";
            _ui.ShowImportResult(summary);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private void EditDrawing()
    {
        if (SelectedDrawing is null)
        {
            return;
        }

        var current = _drawings.Get(SelectedDrawing.Id);
        var saved = current is null ? null : EditUntilValid(current, _ui.EditDrawing, _drawings.Update);
        if (saved is not null)
        {
            LoadProjects(SelectedProject?.Id);
            LoadDrawings(saved.Id);
        }
    }

    /// <summary>Show the edit dialog until the save succeeds or the user cancels; keeps their input on errors.</summary>
    private T? EditUntilValid<T>(T initial, Func<T, T?> edit, Func<T, T> save)
        where T : class
    {
        var draft = initial;
        while (true)
        {
            var edited = edit(draft);
            if (edited is null)
            {
                return null;
            }

            try
            {
                return save(edited);
            }
            catch (ValidationException ex)
            {
                _ui.ShowError(ex.Message);
                draft = edited;
            }
        }
    }

    private void DeleteDrawing()
    {
        if (SelectedDrawing is not { } d ||
            !_ui.Confirm($"Delete drawing {d.DrawingNumber} with all {d.RevisionCount} revision(s)?", "Delete drawing"))
        {
            return;
        }

        PreviewPath = null;
        _import.DeleteStoredFiles(_drawings.Delete(d.Id));
        LoadProjects(SelectedProject?.Id);
        StatusText = $"Drawing {d.DrawingNumber} deleted.";
    }

    private void OpenPdf()
    {
        if (PreviewPath is not null)
        {
            OpenShell(PreviewPath);
        }
    }

    private void ShowInFolder()
    {
        if (PreviewPath is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{PreviewPath}\"") { UseShellExecute = true });
        }
    }

    private void ExportCsv()
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        var path = _ui.PickCsvSaveLocation($"{project.Code}-drawing-register.csv");
        if (path is null)
        {
            return;
        }

        CsvExporter.Export(project, Drawings, path);
        StatusText = $"Exported {Drawings.Count} drawing(s) to {path}";
    }

    private static void OpenShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
