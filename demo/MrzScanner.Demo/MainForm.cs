using System.Text;
using Dissimilis.MrzScanner;

namespace MrzScanner.Demo;

internal sealed class MainForm : Form
{
    private readonly Button _openButton;
    private readonly Label _statusLabel;
    private readonly PictureBox _pictureBox;
    private readonly TextBox _details;
    private readonly IMrzScanner _reader = new Dissimilis.MrzScanner.MrzScanner();

    public MainForm()
    {
        Text = "MRZ Reader Demo";
        ClientSize = new Size(1100, 640);
        MinimumSize = new Size(700, 400);
        AllowDrop = true;

        _openButton = new Button
        {
            Text = "Open image...",
            AutoSize = true,
            Padding = new Padding(6, 2, 6, 2),
        };
        _openButton.Click += async (_, _) => await PickAndReadAsync();

        _statusLabel = new Label
        {
            Text = "Open a document image, or drop one here.",
            AutoSize = true,
            Padding = new Padding(10, 8, 0, 0),
        };

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };
        topPanel.Controls.Add(_openButton);
        topPanel.Controls.Add(_statusLabel);

        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(30, 30, 30),
        };

        _details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 10f),
            BorderStyle = BorderStyle.None,
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 620,
        };
        split.Panel1.Controls.Add(_pictureBox);
        split.Panel2.Controls.Add(_details);

        Controls.Add(split);
        Controls.Add(topPanel);

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += async (_, e) =>
        {
            // The button doubles as the busy flag; ignore drops while reading
            // so overlapping reads cannot finish out of order.
            if (_openButton.Enabled &&
                e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await ReadFileAsync(files[0]);
            }
        };
    }

    private async Task PickAndReadAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a document image",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await ReadFileAsync(dialog.FileName);
    }

    private async Task ReadFileAsync(string path)
    {
        _openButton.Enabled = false;
        _statusLabel.Text = "Reading...";
        _details.Text = string.Empty;

        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var image = Image.FromStream(stream))
            {
                _pictureBox.Image?.Dispose();
                _pictureBox.Image = new Bitmap(image);
            }
        }
        catch (Exception)
        {
            _pictureBox.Image?.Dispose();
            _pictureBox.Image = null;
        }

        try
        {
            MrzResult result = await _reader.ReadAsync(path);
            _details.Text = Describe(result);
            _statusLabel.Text = result.MrzFound
                ? result.IsValid ? "MRZ read, all check digits valid." : "MRZ read with issues."
                : "No MRZ found in this image.";
        }
        catch (Exception ex)
        {
            _details.Text = ex.Message;
            _statusLabel.Text = "Could not read the file.";
        }
        finally
        {
            _openButton.Enabled = true;
        }
    }

    private static string Describe(MrzResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MRZ found:   {result.MrzFound}");
        sb.AppendLine($"Valid:       {result.IsValid}");
        sb.AppendLine($"Confidence:  {result.Confidence:F2}");
        sb.AppendLine();

        if (result.Document is not null)
        {
            MrzDocument doc = result.Document;
            sb.AppendLine($"Type:        {doc.Type}");
            sb.AppendLine($"Number:      {doc.DocumentNumber}");
            sb.AppendLine($"Surname:     {doc.PrimaryIdentifier}");
            sb.AppendLine($"Given names: {doc.SecondaryIdentifier}");
            sb.AppendLine($"Issuer:      {doc.IssuingCountry}");
            sb.AppendLine($"Nationality: {doc.Nationality}");
            sb.AppendLine($"Sex:         {doc.Sex}");
            sb.AppendLine($"Born:        {doc.BirthDate}");
            sb.AppendLine($"Expires:     {doc.ExpiryDate}");
            if (doc.OptionalData1.Length > 0)
                sb.AppendLine($"Optional 1:  {doc.OptionalData1}");
            if (doc.OptionalData2.Length > 0)
                sb.AppendLine($"Optional 2:  {doc.OptionalData2}");
            sb.AppendLine();
        }

        sb.AppendLine("Check digits");
        sb.AppendLine($"  Number:    {result.Checks.DocumentNumber}");
        sb.AppendLine($"  Birth:     {result.Checks.BirthDate}");
        sb.AppendLine($"  Expiry:    {result.Checks.ExpiryDate}");
        sb.AppendLine($"  Optional:  {result.Checks.OptionalData}");
        sb.AppendLine($"  Composite: {result.Checks.Composite}");

        if (result.Raw is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Raw MRZ");
            foreach (string line in result.Raw.Lines)
                sb.AppendLine("  " + line);
        }

        if (result.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Issues");
            foreach (MrzIssue issue in result.Issues)
                sb.AppendLine("  " + issue);
        }
        return sb.ToString();
    }
}
