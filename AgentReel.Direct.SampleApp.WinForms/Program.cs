using System.Drawing.Drawing2D;

namespace AgentReel.Direct.SampleApp.WinForms;

/// <summary>
/// "Invoices" in WinForms: a menu, an Export… button, a Refresh button, an icon-only Print button, a customer field and
/// an Export dialog (format combo box, file name, Save). WinForms exposes a control's Name as its automation id.
/// With "--v2" it plays the next release: Export sits on the right, "Customer" reads "Client", Refresh is gone, and the
/// Print button got a new Name and accessible name, so only its picture identifies it.
/// </summary>
public static class Program
{
    public const string Title = "Glasstake Sample – Invoices (WinForms)";

    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Contains("--v2")));
    }
}

internal sealed class MainForm : Form
{
    private readonly Label _status;
    private readonly TextBox _customer;

    public MainForm(bool v2)
    {
        Text = Program.Title + (v2 ? " (v2)" : "");
        ClientSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9f);

        var menu = new MenuStrip { Name = "MainMenu" };
        var file = new ToolStripMenuItem("&File") { Name = "MenuFile" };
        var exportItem = new ToolStripMenuItem("&Export…") { Name = "MenuExport" };
        exportItem.Click += (_, _) => Export();
        var exit = new ToolStripMenuItem("E&xit") { Name = "MenuExit" };
        exit.Click += (_, _) => Close();
        file.DropDownItems.AddRange([exportItem, new ToolStripSeparator(), exit]);
        menu.Items.Add(file);

        var export = new Button { Name = "ExportButton", Text = "Export…", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        export.Click += (_, _) => Export();

        var print = new Button
        {
            //v2 renamed the control and its accessible name; the icon is the same.
            Name = v2 ? "btnPrintInvoice" : "PrintButton",
            AccessibleName = v2 ? "Send to printer" : "Print",
            Image = PrinterIcon(),
            Size = new Size(36, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            //Room around it, so its baseline picture shows only the button and plain background.
            Margin = new Padding(16, 3, 3, 3)
        };
        print.FlatAppearance.BorderColor = Color.Silver;
        print.Click += (_, _) => _status!.Text = "Printed";

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 10, 12, 10) };

        if (v2)
        {
            //The next release moves Export to the right end and Print next to it.
            export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            print.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            toolbar.Controls.Add(export);
            toolbar.Controls.Add(print);
            toolbar.Layout += (_, _) =>
            {
                export.Location = new Point(toolbar.ClientSize.Width - 12 - export.Width, 10);
                print.Location = new Point(export.Left - 8 - print.Width, 10);
            };
        }
        else
        {
            var refresh = new Button { Name = "RefreshButton", Text = "Refresh", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            refresh.Click += (_, _) => _status!.Text = "Refreshed";

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            flow.Controls.AddRange([export, refresh, print]);
            toolbar.Controls.Add(flow);
        }

        var label = new Label { Name = "CustomerLabel", Text = v2 ? "Client" : "Customer", AutoSize = true, Location = new Point(12, 4), TabIndex = 0 };
        _customer = new TextBox { Name = "CustomerBox", Width = 260, Location = new Point(12, 24), TabIndex = 1 };

        var form = new Panel { Dock = DockStyle.Fill };
        form.Controls.Add(label);
        form.Controls.Add(_customer);

        _status = new Label { Name = "StatusText", Text = "Ready", Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(12, 0, 12, 6) };

        //Docked controls lay out in reverse order of adding: fill first, then the edges.
        Controls.Add(form);
        Controls.Add(toolbar);
        Controls.Add(_status);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private void Export()
    {
        using var dialog = new ExportDialog(_customer.Text);

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _status.Text = $"Saved {dialog.FileName}.{dialog.Format.ToLowerInvariant()}";
    }

    /// <summary>
    /// A small printer drawn in code, so the sample needs no resource files.
    /// </summary>
    private static Bitmap PrinterIcon()
    {
        var icon = new Bitmap(22, 22);

        using var g = Graphics.FromImage(icon);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var dark = new SolidBrush(Color.FromArgb(0x33, 0x41, 0x55));
        using var paper = new SolidBrush(Color.White);
        using var pen = new Pen(Color.FromArgb(0x33, 0x41, 0x55), 1.5f);

        g.FillRectangle(paper, 6, 2, 10, 7);
        g.DrawRectangle(pen, 6, 2, 10, 7);
        g.FillRectangle(dark, 2, 8, 18, 8);
        g.FillRectangle(paper, 6, 13, 10, 7);
        g.DrawRectangle(pen, 6, 13, 10, 7);
        g.FillEllipse(Brushes.LimeGreen, 16, 10, 3, 3);

        return icon;
    }
}

internal sealed class ExportDialog : Form
{
    private readonly ComboBox _format;
    private readonly TextBox _name;

    public string Format => _format.SelectedItem as string ?? "PDF";

    public string FileName => _name.Text;

    public ExportDialog(string customer)
    {
        Text = "Export";
        ClientSize = new Size(340, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);

        var formatLabel = new Label { Text = "Format", AutoSize = true, Location = new Point(16, 14), TabIndex = 0 };
        _format = new ComboBox { Name = "FormatBox", DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Location = new Point(16, 34), TabIndex = 1 };
        _format.Items.AddRange(["PDF", "PNG"]);
        _format.SelectedIndex = 0;

        var nameLabel = new Label { Text = "File name", AutoSize = true, Location = new Point(16, 68), TabIndex = 2 };
        _name = new TextBox
        {
            Name = "FileNameBox",
            Text = string.IsNullOrWhiteSpace(customer) ? "invoice" : "invoice-" + customer.Trim().ToLowerInvariant().Replace(' ', '-'),
            Width = 308,
            Location = new Point(16, 88),
            TabIndex = 3
        };

        var save = new Button { Name = "SaveButton", Text = "Save", DialogResult = DialogResult.OK, Size = new Size(90, 30), Location = new Point(234, 140), TabIndex = 4 };
        AcceptButton = save;

        Controls.AddRange([formatLabel, _format, nameLabel, _name, save]);
    }
}
