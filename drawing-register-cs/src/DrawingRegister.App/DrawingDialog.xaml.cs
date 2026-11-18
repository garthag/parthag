using System.Windows;
using DrawingRegister.Core;

namespace DrawingRegister.App;

public partial class DrawingDialog : Window
{
    private readonly Drawing _drawing;

    public DrawingDialog(Drawing drawing)
    {
        InitializeComponent();
        _drawing = drawing;
        Title = $"Edit drawing {drawing.DrawingNumber}";
        DisciplineBox.ItemsSource = FileNameParser.Disciplines;
        StatusBox.ItemsSource = DrawingStatus.All;
        NumberBox.Text = drawing.DrawingNumber;
        TitleBox.Text = drawing.Title;
        DisciplineBox.Text = drawing.Discipline ?? "";
        StatusBox.SelectedItem = drawing.Status;
        AreaBox.Text = drawing.Area ?? "";
        TypeBox.Text = drawing.DrawingType ?? "";
        RemarksBox.Text = drawing.Remarks ?? "";
        Loaded += (_, _) => TitleBox.Focus();
    }

    public Drawing? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NumberBox.Text))
        {
            ErrorText.Text = "Drawing number is required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new Drawing
        {
            Id = _drawing.Id,
            ProjectId = _drawing.ProjectId,
            DrawingNumber = NumberBox.Text.Trim(),
            Title = TitleBox.Text.Trim(),
            Discipline = DisciplineBox.Text,
            Status = StatusBox.SelectedItem as string ?? _drawing.Status,
            Area = AreaBox.Text,
            DrawingType = TypeBox.Text,
            Remarks = RemarksBox.Text,
        };
        DialogResult = true;
    }
}
