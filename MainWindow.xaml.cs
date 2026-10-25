using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwitchPortViewer;

public partial class MainWindow : Window
{
    private static readonly Brush White = Brushes.White;
    private static readonly Brush Muted = Brush("#A9BBCC");
    private static readonly Brush Dark = Brush("#142539");
    private static readonly Brush Green = Brush("#39BEA9");
    private static readonly Brush Blue = Brush("#5A9BE8");
    private static readonly Brush Purple = Brush("#B998F3");
    private static readonly Brush Orange = Brush("#F5B66E");
    private static readonly Brush Gray = Brush("#9CAABC");

    public MainWindow()
    {
        InitializeComponent();
        ShowEmpty();
        var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (path is not null) Loaded += (_, _) => LoadFile(path);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "開啟交換器配置檔",
            Filter = "交換器配置檔 (*.pcc;*.cfg;*.conf;*.txt;*.log)|*.pcc;*.cfg;*.conf;*.txt;*.log|所有檔案 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) LoadFile(dialog.FileName);
    }

    private void LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到指定的配置檔。", path);
            if (new FileInfo(path).Length > 5_000_000) throw new IOException("配置檔超過 5 MB。請選擇純文字交換器配置檔。");
            // Most ProCurve exports are ASCII; UTF-8 and Windows Big5 filenames/content are both supported.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var bytes = File.ReadAllBytes(path);
            var content = DecodeText(bytes);
            var config = SwitchConfigParser.Parse(content);
            Render(config);
            FileLabel.Text = $"{Path.GetFileName(path)}  ·  {path}";
            NoteLabel.Text = "PVID 優先採用配置檔明示值；未明示時依交換器格式的預設值或 untagged VLAN 推定。配置檔無法提供即時連線 Up/Down、流量或 PoE 狀態。" +
                (config.Warnings.Count > 0 ? $" 解析提醒：{string.Join("；", config.Warnings.Take(5))}" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "無法載入配置檔", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(950).GetString(bytes); }
    }

    private void ShowEmpty()
    {
        SummaryPanel.Children.Clear();
        SummaryPanel.Children.Add(Text("選擇配置檔後，這裡會顯示主機名稱、VLAN 與埠設定。", 15, Dark));
        PanelTitle.Text = "實體埠面板";
        PortGroups.Children.Clear(); VlanPanel.Children.Clear(); TaggedPanel.Children.Clear(); DetailsPanel.Children.Clear();
    }

    private void Render(SwitchConfig config)
    {
        SummaryPanel.Children.Clear();
        AddSummary("交換器", config.Hostname);
        AddSummary("型號", config.Model);
        AddSummary("格式", config.Format);
        AddSummary("埠數", config.Ports.Count.ToString());
        AddSummary("VLAN 數", config.Vlans.Count.ToString());
        AddSummary("帶 Tag 埠", config.Ports.Count(p => p.TaggedVlans.Count > 0).ToString());
        AddSummary("聚合成員", config.Ports.Count(p => p.Trunk is not null).ToString());
        PanelTitle.Text = $"{config.Hostname} · 實體埠面板";
        PortGroups.Children.Clear(); VlanPanel.Children.Clear(); TaggedPanel.Children.Clear(); DetailsPanel.Children.Clear();
        for (int start = 1; start <= config.Ports.Count; start += 24)
        {
            int end = Math.Min(start + 23, config.Ports.Count);
            PortGroups.Children.Add(Text($"埠 {start}–{end}", 13, Muted));
            var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 17) };
            AddRows(rows, config.Ports.Where(p => p.Number >= start && p.Number <= end).ToList());
            PortGroups.Children.Add(rows);
        }
        RenderVlanTable(config);
        var tagged = config.Ports.Where(p => p.TaggedVlans.Count > 0).ToList();
        if (tagged.Count == 0) TaggedPanel.Children.Add(Text("沒有帶 Tagged VLAN 的埠。", 14, Brush("#607187")));
        foreach (var port in tagged) TaggedPanel.Children.Add(TaggedCard(port));
        foreach (var port in config.Ports) DetailsPanel.Children.Add(DetailCard(port, config));
    }

    private void RenderVlanTable(SwitchConfig config)
    {
        VlanPanel.Children.Add(VlanRow("VLAN", "名稱", "Untagged 埠", "Tagged 埠", true));
        foreach (var (id, name) in config.Vlans)
        {
            var untagged = config.Ports.Where(p => p.UntaggedVlans.Contains(id)).Select(p => p.Number);
            var tagged = config.Ports.Where(p => p.TaggedVlans.Contains(id)).Select(p => p.Number);
            VlanPanel.Children.Add(VlanRow(id.ToString(), name, FormatPorts(untagged), FormatPorts(tagged), false));
        }
    }

    private static UIElement VlanRow(string id, string name, string untagged, string tagged, bool header)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] values = [id, name, untagged, tagged];
        for (int i = 0; i < values.Length; i++)
        {
            var cell = Text(values[i], header ? 12 : 13, header ? White : Dark, header ? FontWeights.Bold : FontWeights.Normal);
            cell.Margin = new Thickness(8, 10, 8, 10);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return new Border { Background = header ? Brush("#28415C") : White, CornerRadius = new CornerRadius(5), Child = grid };
    }

    private static UIElement TaggedCard(PortInfo port)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text($"PORT {port.Number:00}  ·  PVID {port.Pvid?.ToString() ?? "—"}", 14, Dark, FontWeights.Bold));
        panel.Children.Add(Text($"Tagged：{FormatVlans(port.TaggedVlans)}", 12, Brush("#607187")));
        return new Border { Width = 286, Padding = new Thickness(13), Margin = new Thickness(0, 0, 12, 12), Background = White, BorderBrush = Blue, BorderThickness = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(8), Child = panel };
    }

    private void AddSummary(string label, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 35, 0), MinWidth = 105 };
        panel.Children.Add(Text(label, 12, Brush("#607187")));
        panel.Children.Add(Text(value, 17, Dark, FontWeights.Bold));
        SummaryPanel.Children.Add(panel);
    }

    private static void AddRows(Panel target, List<PortInfo> ports)
    {
        var first = ports.Min(p => p.Number);
        AddRow(target, ports.Where(p => (p.Number - first) % 2 == 0));
        AddRow(target, ports.Where(p => (p.Number - first) % 2 != 0));
    }

    private static void AddRow(Panel target, IEnumerable<PortInfo> ports)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        foreach (var port in ports) row.Children.Add(PortTile(port));
        target.Children.Add(row);
    }

    private static UIElement PortTile(PortInfo port)
    {
        var color = PortColor(port);
        var frame = new Border
        {
            Width = 88, Height = 88, Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(8), BorderBrush = color, BorderThickness = new Thickness(2),
            Background = Brush("#26384C"), ToolTip = $"埠 {port.Number} · {port.Type} · PVID {port.Pvid?.ToString() ?? "—"} · Tagged {FormatVlans(port.TaggedVlans)}"
        };
        var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(Text(port.Number.ToString("00"), 20, White, FontWeights.Bold, TextAlignment.Center));
        inner.Children.Add(new Border { Background = color, Width = 38, Height = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 3, 0, 6) });
        inner.Children.Add(Text(port.Disabled ? "停用" : port.Trunk is not null ? port.Trunk : $"PVID {port.Pvid?.ToString() ?? "—"}", 11, Muted, null, TextAlignment.Center));
        frame.Child = inner;
        return frame;
    }

    private static UIElement DetailCard(PortInfo port, SwitchConfig config)
    {
        var color = PortColor(port);
        var border = new Border
        {
            Width = 286, MinHeight = 160, Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(15), Background = White, CornerRadius = new CornerRadius(10),
            BorderBrush = Brush("#E0E6EF"), BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        var type = Text(port.Type, 12, color, FontWeights.SemiBold);
        DockPanel.SetDock(type, Dock.Right);
        header.Children.Add(type);
        header.Children.Add(Text($"PORT {port.Number:00}", 17, Dark, FontWeights.Bold));
        stack.Children.Add(header);
        stack.Children.Add(Text($"PVID：{(port.Pvid is int id ? $"{id}  {config.Vlans.GetValueOrDefault(id)}" : "—（無 untagged VLAN）")}", 13, Dark));
        stack.Children.Add(Text($"Untagged：{FormatVlans(port.UntaggedVlans)}", 13, Dark));
        stack.Children.Add(Text($"802.1Q Tagged：{FormatVlans(port.TaggedVlans)}", 13, Dark));
        stack.Children.Add(Text($"聚合：{(port.Trunk is null ? "—" : $"{port.Trunk} / {port.TrunkMode}")}", 12, Brush("#607187")));
        if (port.Description is not null) stack.Children.Add(Text($"描述：{port.Description}", 12, Brush("#607187")));
        stack.Children.Add(Text($"設定：{(port.Disabled ? "管理停用" : "管理啟用 / 未明示停用")}{(port.SpeedDuplex is null ? "" : $" · {port.SpeedDuplex}")}", 12, Brush("#607187")));
        border.Child = stack;
        return border;
    }

    private static Brush PortColor(PortInfo port) => port.Disabled ? Gray : port.Trunk is not null ? Purple : port.TaggedVlans.Count > 0 ? Blue : port.Pvid is null ? Orange : Green;
    private static string FormatVlans(IEnumerable<int> vlans) => vlans.Any() ? string.Join(", ", vlans) : "—";
    private static string FormatPorts(IEnumerable<int> ports)
    {
        var numbers = ports.OrderBy(x => x).ToArray();
        if (numbers.Length == 0) return "—";
        var parts = new List<string>();
        int start = numbers[0], end = start;
        foreach (var number in numbers.Skip(1))
        {
            if (number == end + 1) end = number;
            else { parts.Add(start == end ? start.ToString() : $"{start}–{end}"); start = end = number; }
        }
        parts.Add(start == end ? start.ToString() : $"{start}–{end}");
        return $"{string.Join(", ", parts)}  ({numbers.Length})";
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static TextBlock Text(string value, double size, Brush color, FontWeight? weight = null, TextAlignment alignment = TextAlignment.Left) =>
        new() { Text = value, FontSize = size, Foreground = color, FontWeight = weight ?? FontWeights.Normal, TextAlignment = alignment, TextWrapping = TextWrapping.Wrap };
}
