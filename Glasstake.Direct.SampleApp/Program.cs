using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glasstake.Direct.SampleApp;

/// <summary>
/// "Invoices": a main window with a menu, an Export… button, a customer field and a Refresh button, and an Export
/// dialog with a format combo box, a file name and Save. With "--v2" it plays the next release: the Export button
/// sits on the right, the "Customer" label reads "Client", and Refresh is gone.
/// </summary>
public static class Program
{
    public const string Title = "Glasstake Sample – Invoices";

    [STAThread]
    public static int Main(string[] args)
    {
        var v2 = args.Contains("--v2");
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new MainWindow(v2));
    }
}

internal sealed class MainWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBox _customer;

    public MainWindow(bool v2)
    {
        Title = Program.Title + (v2 ? " (v2)" : "");
        Width = 640;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.WhiteSmoke;

        var root = new DockPanel();

        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        var exportItem = new MenuItem { Header = "_Export…" };
        AutomationProperties.SetAutomationId(exportItem, "MenuExport");
        exportItem.Click += (_, _) => Export();
        var exit = new MenuItem { Header = "E_xit" };
        exit.Click += (_, _) => Close();
        file.Items.Add(exportItem);
        file.Items.Add(new Separator());
        file.Items.Add(exit);
        menu.Items.Add(file);
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        _status = new TextBlock { Margin = new Thickness(12, 6, 12, 10), Text = "Ready" };
        AutomationProperties.SetAutomationId(_status, "StatusText");
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var toolbar = new DockPanel { Margin = new Thickness(12), LastChildFill = false };
        DockPanel.SetDock(toolbar, Dock.Top);

        var export = new Button { Content = "Export…", Padding = new Thickness(14, 4, 14, 4), MinWidth = 90 };
        AutomationProperties.SetAutomationId(export, "ExportButton");
        export.Click += (_, _) => Export();

        //v2 moves the button from the left to the right end of the toolbar.
        DockPanel.SetDock(export, v2 ? Dock.Right : Dock.Left);
        toolbar.Children.Add(export);

        if (!v2)
        {
            var refresh = new Button { Content = "Refresh", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(refresh, "RefreshButton");
            refresh.Click += (_, _) => _status.Text = "Refreshed";
            DockPanel.SetDock(refresh, Dock.Left);
            toolbar.Children.Add(refresh);
        }

        root.Children.Add(toolbar);

        var form = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        var label = new Label { Content = v2 ? "Client" : "Customer", Padding = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(label, "CustomerLabel");
        _customer = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 3, 4, 3) };
        AutomationProperties.SetAutomationId(_customer, "CustomerBox");
        AutomationProperties.SetLabeledBy(_customer, label);
        label.Target = _customer;
        form.Children.Add(label);
        form.Children.Add(_customer);
        root.Children.Add(form);

        Content = root;
    }

    private void Export()
    {
        var dialog = new ExportDialog(_customer.Text) { Owner = this };

        if (dialog.ShowDialog() == true)
            _status.Text = $"Saved {dialog.FileName}.{dialog.Format.ToLowerInvariant()}";
    }
}

internal sealed class ExportDialog : Window
{
    private readonly ComboBox _format;
    private readonly TextBox _name;

    public string Format => (_format.SelectedItem as ComboBoxItem)?.Content as string ?? "PDF";

    public string FileName => _name.Text;

    public ExportDialog(string customer)
    {
        Title = "Export";
        Width = 360;
        Height = 230;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };

        var formatLabel = new Label { Content = "Format", Padding = new Thickness(0, 0, 0, 4) };
        _format = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        _format.Items.Add(new ComboBoxItem { Content = "PDF" });
        _format.Items.Add(new ComboBoxItem { Content = "PNG" });
        _format.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_format, "FormatBox");
        AutomationProperties.SetLabeledBy(_format, formatLabel);

        var nameLabel = new Label { Content = "File name", Padding = new Thickness(0, 10, 0, 4) };
        _name = new TextBox { Text = string.IsNullOrWhiteSpace(customer) ? "invoice" : "invoice-" + customer.Trim().ToLowerInvariant().Replace(' ', '-'), Padding = new Thickness(4, 3, 4, 3) };
        AutomationProperties.SetAutomationId(_name, "FileNameBox");
        AutomationProperties.SetLabeledBy(_name, nameLabel);

        var save = new Button { Content = "Save", IsDefault = true, Width = 90, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(4) };
        AutomationProperties.SetAutomationId(save, "SaveButton");
        save.Click += (_, _) => DialogResult = true;

        panel.Children.Add(formatLabel);
        panel.Children.Add(_format);
        panel.Children.Add(nameLabel);
        panel.Children.Add(_name);
        panel.Children.Add(save);
        Content = panel;
    }
}
