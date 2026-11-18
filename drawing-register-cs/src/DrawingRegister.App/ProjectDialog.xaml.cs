using System.Windows;
using DrawingRegister.Core;

namespace DrawingRegister.App;

public partial class ProjectDialog : Window
{
    private readonly Project _project;

    public ProjectDialog(Project project, bool isNew)
    {
        InitializeComponent();
        _project = project;
        Title = isNew ? "Add project" : $"Edit project {project.Code}";
        Heading.Text = isNew ? "Add project" : "Edit project";
        SaveButton.Content = isNew ? "Create project" : "Save";
        StatusBox.ItemsSource = ProjectStatus.All;
        CodeBox.Text = project.Code;
        NameBox.Text = project.Name;
        ClientBox.Text = project.Client ?? "";
        LocationBox.Text = project.Location ?? "";
        DescriptionBox.Text = project.Description ?? "";
        StatusBox.SelectedItem = ProjectStatus.All.Contains(project.Status) ? project.Status : ProjectStatus.Active;
        StartBox.SelectedDate = project.StartDate;
        EndBox.SelectedDate = project.EndDate;
        Loaded += (_, _) => (isNew ? CodeBox : NameBox).Focus();
    }

    public Project? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var code = CodeBox.Text.Trim();
        if (code.Length < 2 || code.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            errors.Add("Project code needs at least 2 characters: letters, digits, '-' or '_' (e.g. NRP-2026).");
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            errors.Add("Project name is required.");
        }

        if (StartBox.SelectedDate is { } s && EndBox.SelectedDate is { } end && end < s)
        {
            errors.Add("End date must be on or after the start date.");
        }

        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join("\n", errors);
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new Project
        {
            Id = _project.Id,
            Code = code,
            Name = NameBox.Text.Trim(),
            Client = ClientBox.Text,
            Location = LocationBox.Text,
            Description = DescriptionBox.Text,
            Status = StatusBox.SelectedItem as string ?? ProjectStatus.Active,
            StartDate = StartBox.SelectedDate,
            EndDate = EndBox.SelectedDate,
        };
        DialogResult = true;
    }
}
