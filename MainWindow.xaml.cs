using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EigongCustomizer;

public class ConfigData
{
    public string BodyPath { get; set; } = "";
    public string EffectPath { get; set; } = "";
    public string OtherPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Language { get; set; } = "en";
}

// ===== 匯出 / 匯入用的純資料格式（不含圖片資料）=====
public class RuleDto
{
    public bool Enabled { get; set; } = true;
    public bool Exclude { get; set; } = false;
    public int SrcR { get; set; }
    public int SrcG { get; set; }
    public int SrcB { get; set; }
    public int TgtR { get; set; }
    public int TgtG { get; set; }
    public int TgtB { get; set; }
    public double Tol { get; set; } = 100;
    public bool UsesTexture { get; set; } = false;
    public string? TextureName { get; set; }
}

public class PresetData
{
    public int Version { get; set; } = 1;
    public double Bright { get; set; } = 1.1;
    public List<RuleDto> Rules { get; set; } = new();
}

public class ColorRule : INotifyPropertyChanged
{
    public bool Enabled { get; set; } = true;
    public bool Exclude { get; set; } = false;   // true = 保護色，符合的像素完全不處理

    public int SrcR { get; set; } = 180;
    public int SrcG { get; set; } = 25;
    public int SrcB { get; set; } = 25;
    public int TgtR { get; set; } = 0;
    public int TgtG { get; set; } = 180;
    public int TgtB { get; set; } = 255;
    public double Tol { get; set; } = 100;

    // ===== 貼圖 =====
    public byte[]? TexPixels;         // Bgra32 像素（演算法用）
    public int TexW, TexH;
    public BitmapSource? TexBitmap;   // 清單縮圖用
    public string? TexName;           // 目前貼圖的檔名（匯出用）
    public string? PendingTexName;    // 匯入後「需要使用者重新選圖」的提示檔名
    public bool UseTexture => TexPixels != null;

    public void SetTexture(byte[] fileBytes, string? name = null)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(fileBytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();

        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);

        TexW = w; TexH = h; TexPixels = px; TexBitmap = bmp;
        TexName = name;
        PendingTexName = null;
    }

    public void ClearTexture()
    {
        TexPixels = null; TexBitmap = null; TexW = TexH = 0;
        TexName = null; PendingTexName = null;
    }

    public Brush SrcBrush => new SolidColorBrush(Color.FromRgb((byte)SrcR, (byte)SrcG, (byte)SrcB));
    public Brush TgtBrush => TexBitmap != null
        ? new ImageBrush(TexBitmap) { Stretch = Stretch.UniformToFill }
        : new SolidColorBrush(Color.FromRgb((byte)TgtR, (byte)TgtG, (byte)TgtB));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SrcBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TgtBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Exclude)));
    }

    public RuleDto ToDto() => new()
    {
        Enabled = Enabled,
        Exclude = Exclude,
        SrcR = SrcR,
        SrcG = SrcG,
        SrcB = SrcB,
        TgtR = TgtR,
        TgtG = TgtG,
        TgtB = TgtB,
        Tol = Tol,
        UsesTexture = UseTexture || PendingTexName != null,
        TextureName = TexName ?? PendingTexName
    };

    public static ColorRule FromDto(RuleDto d)
    {
        var r = new ColorRule
        {
            Enabled = d.Enabled,
            Exclude = d.Exclude,
            SrcR = Math.Clamp(d.SrcR, 0, 255),
            SrcG = Math.Clamp(d.SrcG, 0, 255),
            SrcB = Math.Clamp(d.SrcB, 0, 255),
            TgtR = Math.Clamp(d.TgtR, 0, 255),
            TgtG = Math.Clamp(d.TgtG, 0, 255),
            TgtB = Math.Clamp(d.TgtB, 0, 255),
            Tol = Math.Clamp(d.Tol, 10, 250)
        };
        if (d.UsesTexture)
            r.PendingTexName = string.IsNullOrWhiteSpace(d.TextureName) ? "?" : d.TextureName;
        return r;
    }
}

public partial class MainWindow : Window
{
    private string currentActiveDir = "";
    private byte[]? originalPixelBuffer;
    private int imgWidth, imgHeight, imgStride;
    private WriteableBitmap? previewBitmap;

    private ConfigData config = new();
    private readonly string configFile = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private bool isEnglish = false;

    private readonly ObservableCollection<ColorRule> rules = new();
    private bool loading = false;
    private bool showOriginal = false;
    private readonly Random rng = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        ApplyLanguage();

        RuleList.ItemsSource = rules;
        rules.Add(new ColorRule());
        RuleList.SelectedIndex = 0;
    }

    #region 設定檔儲存與讀取 (Settings)
    private void LoadConfig()
    {
        if (File.Exists(configFile))
        {
            try
            {
                string json = File.ReadAllText(configFile);
                config = JsonSerializer.Deserialize<ConfigData>(json) ?? new ConfigData();
            }
            catch { }
        }

        if (string.IsNullOrEmpty(config.OutputPath))
            config.OutputPath = Path.Combine(AppContext.BaseDirectory, "Exported_Custom");

        isEnglish = config.Language == "en";
        PathBodyBox.Text = config.BodyPath;
        PathEffectBox.Text = config.EffectPath;
        PathOtherBox.Text = config.OtherPath;
        PathOutputBox.Text = config.OutputPath;
    }

    private void SaveConfig()
    {
        config.BodyPath = PathBodyBox.Text;
        config.EffectPath = PathEffectBox.Text;
        config.OtherPath = PathOtherBox.Text;
        config.OutputPath = PathOutputBox.Text;
        config.Language = isEnglish ? "en" : "zh";
        File.WriteAllText(configFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
    #endregion

    #region 資料夾瀏覽與載入
    private void BrowseBody_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = PathBodyBox.Text };
        if (dialog.ShowDialog() == true)
        {
            PathBodyBox.Text = dialog.FolderName;
            SaveConfig();
        }
    }

    private void BrowseEffect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = PathEffectBox.Text };
        if (dialog.ShowDialog() == true)
        {
            PathEffectBox.Text = dialog.FolderName;
            SaveConfig();
        }
    }

    private void BrowseOther_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = PathOtherBox.Text };
        if (dialog.ShowDialog() == true)
        {
            PathOtherBox.Text = dialog.FolderName;
            SaveConfig();
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = PathOutputBox.Text };
        if (dialog.ShowDialog() == true)
        {
            PathOutputBox.Text = dialog.FolderName;
            SaveConfig();
        }
    }

    // 開啟輸出資料夾
    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        string dir = PathOutputBox.Text;
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(string.IsNullOrEmpty(currentActiveDir) ? AppContext.BaseDirectory : currentActiveDir, "Exported_Custom");

        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    private void BtnLoadBody_Click(object sender, RoutedEventArgs e) => LoadFolder(PathBodyBox.Text);
    private void BtnLoadEffect_Click(object sender, RoutedEventArgs e) => LoadFolder(PathEffectBox.Text);
    private void BtnLoadOther_Click(object sender, RoutedEventArgs e) => LoadFolder(PathOtherBox.Text);

    private void LoadFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show(isEnglish ? $"Directory not found:\n{path}" : $"找不到資料夾：\n{path}");
            return;
        }

        currentActiveDir = path;
        var files = Directory.GetFiles(path, "*.png").Select(Path.GetFileName).ToList();
        FileListBox.ItemsSource = files;
        if (files.Count > 0) FileListBox.SelectedIndex = 0;
        StatusText.Text = isEnglish ? $"Loaded {files.Count} images." : $"已載入 {files.Count} 張圖片。";
    }

    private void FileListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem == null) return;
        string fullPath = Path.Combine(currentActiveDir, FileListBox.SelectedItem.ToString()!);
        LoadOriginalImage(fullPath);
        ProcessPreview();
    }
    #endregion

    #region 影像載入
    private void LoadOriginalImage(string filePath)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource = new Uri(filePath);
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();

        var formatted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        imgWidth = formatted.PixelWidth;
        imgHeight = formatted.PixelHeight;
        imgStride = imgWidth * 4;

        originalPixelBuffer = new byte[imgHeight * imgStride];
        formatted.CopyPixels(originalPixelBuffer, imgStride, 0);

        previewBitmap = new WriteableBitmap(imgWidth, imgHeight, 96, 96, PixelFormats.Bgra32, null);
        PreviewImage.Source = previewBitmap;
    }
    #endregion

    #region 規則 / 滴管 / 對比 / 取代演算法
    private void UpdateSliderLabels()
    {
        if (TxtTol == null || TxtBright == null) return;
        TxtTol.Text = isEnglish ? "Color tolerance:" : "色彩容許範圍:";
        TxtBright.Text = isEnglish ? "Brightness gain:" : "光影增益倍率:";
    }

    // 數字輸入框：按 Enter 立即套用
    private void NumBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
        }
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
        if (loading) return;
        OnSlidersChanged();
    }

    private void OnSlidersChanged()
    {
        if (RuleList?.SelectedItem is ColorRule r)
        {
            r.SrcR = (int)SliderSrcR.Value; r.SrcG = (int)SliderSrcG.Value; r.SrcB = (int)SliderSrcB.Value;
            r.TgtR = (int)SliderTgtR.Value; r.TgtG = (int)SliderTgtG.Value; r.TgtB = (int)SliderTgtB.Value;
            r.Tol = SliderTol.Value;
            r.Notify();
        }
        RefreshTargetPreview();
        ProcessPreview();
    }

    private void RefreshTargetPreview()
    {
        if (TargetColorPreview == null || TxtTexInfo == null) return;
        if (RuleList?.SelectedItem is ColorRule r)
        {
            TargetColorPreview.Fill = r.TgtBrush;
            if (r.UseTexture)
                TxtTexInfo.Text = isEnglish
                    ? $"Texture: {r.TexName} ({r.TexW}×{r.TexH}), color sliders ignored"
                    : $"使用貼圖：{r.TexName}（{r.TexW}×{r.TexH}），下方顏色滑桿無效";
            else if (r.PendingTexName != null)
                TxtTexInfo.Text = isEnglish
                    ? $"Please re-select the image: {r.PendingTexName}"
                    : $"請重新選擇圖片：{r.PendingTexName}";
            else
                TxtTexInfo.Text = "";
        }
        else
        {
            TargetColorPreview.Fill = new SolidColorBrush(Color.FromRgb(
                (byte)SliderTgtR.Value, (byte)SliderTgtG.Value, (byte)SliderTgtB.Value));
        }
    }

    private void RuleList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RuleList.SelectedItem is not ColorRule r) return;
        loading = true;
        SliderSrcR.Value = r.SrcR; SliderSrcG.Value = r.SrcG; SliderSrcB.Value = r.SrcB;
        SliderTgtR.Value = r.TgtR; SliderTgtG.Value = r.TgtG; SliderTgtB.Value = r.TgtB;
        SliderTol.Value = r.Tol;
        ChkExclude.IsChecked = r.Exclude;
        loading = false;
        RefreshTargetPreview();
        ProcessPreview();
    }

    private void RuleCheck_Click(object sender, RoutedEventArgs e) => ProcessPreview();

    private void ChkExclude_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is ColorRule r)
        {
            r.Exclude = ChkExclude.IsChecked == true;
            r.Notify();
            ProcessPreview();
        }
    }

    // ===== 隨機目標色（HSV，避免髒灰色）=====
    private void BtnRandom_Click(object sender, RoutedEventArgs e)
    {
        double h = rng.NextDouble() * 360.0;
        double s = 0.55 + rng.NextDouble() * 0.45;
        double v = 0.75 + rng.NextDouble() * 0.25;
        var (r, g, b) = HsvToRgb(h, s, v);

        loading = true;
        SliderTgtR.Value = r; SliderTgtG.Value = g; SliderTgtB.Value = b;
        loading = false;
        OnSlidersChanged();
    }

    private static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r1, g1, b1;
        if (h < 60) (r1, g1, b1) = (c, x, 0);
        else if (h < 120) (r1, g1, b1) = (x, c, 0);
        else if (h < 180) (r1, g1, b1) = (0, c, x);
        else if (h < 240) (r1, g1, b1) = (0, x, c);
        else if (h < 300) (r1, g1, b1) = (x, 0, c);
        else (r1, g1, b1) = (c, 0, x);
        return ((int)Math.Round((r1 + m) * 255), (int)Math.Round((g1 + m) * 255), (int)Math.Round((b1 + m) * 255));
    }

    // ===== 貼圖 =====
    private void BtnTexture_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not ColorRule r) return;
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog() != true) return;

        try { r.SetTexture(File.ReadAllBytes(dlg.FileName), Path.GetFileName(dlg.FileName)); }
        catch
        {
            MessageBox.Show(isEnglish ? "Cannot read this image." : "無法讀取這張圖片。");
            return;
        }
        r.Notify();
        RefreshTargetPreview();
        ProcessPreview();
    }

    private void BtnClearTexture_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not ColorRule r) return;
        if (!r.UseTexture && r.PendingTexName == null) return;
        r.ClearTexture();
        r.Notify();
        RefreshTargetPreview();
        ProcessPreview();
    }

    // ===== 調整順序 =====
    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not ColorRule r) return;
        int idx = rules.IndexOf(r);
        if (idx <= 0) return;
        rules.Move(idx, idx - 1);
        RuleList.SelectedItem = r;
        ProcessPreview();
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not ColorRule r) return;
        int idx = rules.IndexOf(r);
        if (idx < 0 || idx >= rules.Count - 1) return;
        rules.Move(idx, idx + 1);
        RuleList.SelectedItem = r;
        ProcessPreview();
    }

    // ===== 匯出 / 匯入設定 =====
    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "(*.json)|*.json",
            FileName = ".json"
        };
        if (dlg.ShowDialog() != true) return;

        var data = new PresetData
        {
            Bright = SliderBright.Value,
            Rules = rules.Select(r => r.ToDto()).ToList()
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        StatusText.Text = isEnglish ? $"Preset exported:\n{dlg.FileName}" : $"設定已匯出：\n{dlg.FileName}";
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "(*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;

        PresetData? data;
        try
        {
            data = JsonSerializer.Deserialize<PresetData>(File.ReadAllText(dlg.FileName));
            if (data == null) throw new Exception("empty");
        }
        catch
        {
            MessageBox.Show(isEnglish ? "Invalid preset file." : "無法讀取這個設定檔。");
            return;
        }

        rules.Clear();
        foreach (var d in data.Rules) rules.Add(ColorRule.FromDto(d));
        if (rules.Count == 0) rules.Add(new ColorRule());

        SliderBright.Value = Math.Clamp(data.Bright, 0.5, 3.0);
        RuleList.SelectedIndex = 0;

        int need = rules.Count(x => x.PendingTexName != null);
        string extra = need > 0
            ? (isEnglish ? $" {need} rule(s) need an image selected." : $" 其中 {need} 條規則需要重新選擇圖片。")
            : "";
        StatusText.Text = (isEnglish ? $"Preset imported ({rules.Count} rules)." : $"已匯入設定（{rules.Count} 條規則）。") + extra;
    }

    private void BtnCompare_Down(object sender, MouseButtonEventArgs e)
    {
        showOriginal = true;
        ProcessPreview();
    }

    private void BtnCompare_Up(object sender, MouseEventArgs e)
    {
        if (!showOriginal) return;
        showOriginal = false;
        ProcessPreview();
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        var r = new ColorRule
        {
            SrcR = (int)SliderSrcR.Value,
            SrcG = (int)SliderSrcG.Value,
            SrcB = (int)SliderSrcB.Value,
            TgtR = (int)SliderTgtR.Value,
            TgtG = (int)SliderTgtG.Value,
            TgtB = (int)SliderTgtB.Value,
            Tol = SliderTol.Value
        };
        rules.Add(r);
        RuleList.SelectedItem = r;
        BtnPicker.IsChecked = true;
        BtnPicker_Click(BtnPicker, new RoutedEventArgs());
    }

    private void BtnDelRule_Click(object sender, RoutedEventArgs e)
    {
        if (rules.Count <= 1 || RuleList.SelectedItem is not ColorRule r) return;
        int idx = rules.IndexOf(r);
        rules.Remove(r);
        RuleList.SelectedIndex = Math.Min(idx, rules.Count - 1);
    }

    private void BtnPicker_Click(object sender, RoutedEventArgs e)
    {
        PreviewBorder.Cursor = BtnPicker.IsChecked == true ? Cursors.Cross : Cursors.Arrow;
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (BtnPicker.IsChecked != true) return;
        PreviewBorder.CaptureMouse();
        PickColor(e.GetPosition(PreviewImage));
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (PreviewBorder.IsMouseCaptured) PickColor(e.GetPosition(PreviewImage));
    }

    private void Preview_MouseUp(object sender, MouseButtonEventArgs e) => PreviewBorder.ReleaseMouseCapture();

    private void PickColor(Point p)
    {
        if (originalPixelBuffer == null || imgWidth == 0) return;

        double scale = Math.Min(PreviewImage.ActualWidth / imgWidth, PreviewImage.ActualHeight / imgHeight);
        double offX = (PreviewImage.ActualWidth - imgWidth * scale) / 2;
        double offY = (PreviewImage.ActualHeight - imgHeight * scale) / 2;
        int x = (int)Math.Floor((p.X - offX) / scale);
        int y = (int)Math.Floor((p.Y - offY) / scale);
        if (x < 0 || y < 0 || x >= imgWidth || y >= imgHeight) return;

        int i = y * imgStride + x * 4;
        if (originalPixelBuffer[i + 3] == 0) return;

        loading = true;
        SliderSrcB.Value = originalPixelBuffer[i];
        SliderSrcG.Value = originalPixelBuffer[i + 1];
        SliderSrcR.Value = originalPixelBuffer[i + 2];
        loading = false;
        OnSlidersChanged();
    }

    private record struct PreparedRule(double Nr, double Ng, double Nb, double TolN,
                                       int TgtR, int TgtG, int TgtB, bool Exclude,
                                       byte[]? Tex, int TexW, int TexH);

    // 只取「已勾選」的規則，並保持清單順序（上面優先）
    private PreparedRule[] PrepareRules() => rules.Where(r => r.Enabled).Select(r =>
    {
        int m = Math.Max(1, Math.Max(r.SrcR, Math.Max(r.SrcG, r.SrcB)));
        return new PreparedRule(r.SrcR / (double)m, r.SrcG / (double)m, r.SrcB / (double)m,
                                r.Tol / 250.0, r.TgtR, r.TgtG, r.TgtB, r.Exclude,
                                r.TexPixels, r.TexW, r.TexH);
    }).ToArray();

    private static void ApplyRules(byte[] src, byte[] dst, PreparedRule[] rs, double bright, int width)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            byte b = src[i], g = src[i + 1], r = src[i + 2], a = src[i + 3];
            dst[i] = b; dst[i + 1] = g; dst[i + 2] = r; dst[i + 3] = a;
            if (a == 0) continue;

            int max = Math.Max(r, Math.Max(g, b));
            if (max < 6) continue;

            double nr = r / (double)max, ng = g / (double)max, nb = b / (double)max;

            // 第一輪：符合任何「保護色」→ 整個像素原樣保留
            bool protectedPx = false;
            for (int k = 0; k < rs.Length; k++)
            {
                if (!rs[k].Exclude) continue;
                double dr = nr - rs[k].Nr, dg = ng - rs[k].Ng, db = nb - rs[k].Nb;
                if (dr * dr + dg * dg + db * db <= rs[k].TolN * rs[k].TolN) { protectedPx = true; break; }
            }
            if (protectedPx) continue;

            // 第二輪：依清單順序，第一條符合的替換規則勝出
            int best = -1;
            for (int k = 0; k < rs.Length; k++)
            {
                if (rs[k].Exclude) continue;
                double dr = nr - rs[k].Nr, dg = ng - rs[k].Ng, db = nb - rs[k].Nb;
                if (dr * dr + dg * dg + db * db <= rs[k].TolN * rs[k].TolN) { best = k; break; }
            }
            if (best < 0) continue;

            double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            lum = Math.Clamp(Math.Pow(lum, 0.5) * bright, 0.0, 1.0);

            var rule = rs[best];
            if (rule.Tex != null)
            {
                // 貼圖模式：依像素座標平鋪取色，再乘上明暗
                int p = i >> 2;
                int x = p % width, y = p / width;
                int t = ((y % rule.TexH) * rule.TexW + (x % rule.TexW)) * 4;
                dst[i] = (byte)Math.Clamp(rule.Tex[t] * lum, 0, 255);
                dst[i + 1] = (byte)Math.Clamp(rule.Tex[t + 1] * lum, 0, 255);
                dst[i + 2] = (byte)Math.Clamp(rule.Tex[t + 2] * lum, 0, 255);
            }
            else
            {
                dst[i] = (byte)Math.Clamp(rule.TgtB * lum, 0, 255);
                dst[i + 1] = (byte)Math.Clamp(rule.TgtG * lum, 0, 255);
                dst[i + 2] = (byte)Math.Clamp(rule.TgtR * lum, 0, 255);
            }
        }
    }

    private void ProcessPreview()
    {
        if (originalPixelBuffer == null || previewBitmap == null) return;

        if (showOriginal)
        {
            previewBitmap.WritePixels(new Int32Rect(0, 0, imgWidth, imgHeight), originalPixelBuffer, imgStride, 0);
            return;
        }

        var buf = new byte[originalPixelBuffer.Length];
        ApplyRules(originalPixelBuffer, buf, PrepareRules(), SliderBright.Value, imgWidth);
        previewBitmap.WritePixels(new Int32Rect(0, 0, imgWidth, imgHeight), buf, imgStride, 0);
    }
    #endregion

    #region 重設
    // 只重設容許度與光影增益，顏色、保護模式、啟用狀態都不動
    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        SliderTol.Value = 100;
        SliderBright.Value = 1.1;
    }
    #endregion

    #region 批次處理與多國語言切換
    private async void BtnBatchProcess_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(currentActiveDir)) return;

        string outDir = PathOutputBox.Text;
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(currentActiveDir, "Exported_Custom");

        Directory.CreateDirectory(outDir);

        var files = Directory.GetFiles(currentActiveDir, "*.png");
        var prepared = PrepareRules();
        double brightMult = SliderBright.Value;

        StatusText.Text = isEnglish ? "Processing in parallel, please wait..." : "正在高速並行批次處理中...";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(() =>
        {
            Parallel.ForEach(files, file =>
            {
                string filename = Path.GetFileName(file);
                string destFile = Path.Combine(outDir, filename);

                var decoder = new PngBitmapDecoder(new Uri(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var formatted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

                int w = formatted.PixelWidth;
                int h = formatted.PixelHeight;
                int stride = w * 4;
                byte[] raw = new byte[h * stride];
                formatted.CopyPixels(raw, stride, 0);

                ApplyRules(raw, raw, prepared, brightMult, w);

                var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, raw, stride);
                using var stream = new FileStream(destFile, FileMode.Create);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(stream);
            });
        });

        sw.Stop();
        string msg = isEnglish
            ? $"Done! Processed {files.Length} images in {sw.ElapsedMilliseconds / 1000.0:F2}s.\nSaved to: {outDir}"
            : $"完成！共處理 {files.Length} 張圖片，耗時: {sw.ElapsedMilliseconds / 1000.0:F2} 秒。\n輸出於：{outDir}";

        StatusText.Text = msg;
        MessageBox.Show(msg, isEnglish ? "Success" : "成功");
    }

    private void BtnLang_Click(object sender, RoutedEventArgs e)
    {
        isEnglish = !isEnglish;
        ApplyLanguage();
        SaveConfig();
    }

    private void ApplyLanguage()
    {
        BtnLang.Content = isEnglish ? "中文" : "English";

        TxtBodyPath.Text = isEnglish ? "Eigong Body Directory:" : "易公本體資料夾:";
        TxtEffectPath.Text = isEnglish ? "Attack Effect Directory:" : "攻擊特效資料夾:";
        TxtOtherPath.Text = isEnglish ? "Other Directory:" : "其他資料夾:";
        TxtOutputPath.Text = isEnglish ? "Custom Output Directory:" : "自訂輸出資料夾:";
        BtnLoadBody.Content = isEnglish ? "Load" : "載入";
        BtnLoadEffect.Content = isEnglish ? "Load" : "載入";
        BtnLoadOther.Content = isEnglish ? "Load" : "載入";
        BtnOpenOutput.ToolTip = isEnglish ? "Open output folder" : "開啟輸出資料夾";

        TxtSec1.Text = isEnglish ? "1. Source Color Sample" : "1. 原圖色彩取樣";
        BtnPicker.Content = isEnglish ? "🎯 Eyedropper (click/drag on preview)" : "🎯 滴管（在預覽圖上點選 / 拖曳）";
        BtnCompare.Content = isEnglish ? "👁 Hold to show original" : "👁 按住顯示原圖";
        TxtPriorityHint.Text = isEnglish ? "(Higher in the list = higher priority)" : "（清單越上面優先權越高）";
        BtnAddRule.Content = isEnglish ? "＋ Add Color" : "＋ 新增顏色";
        BtnDelRule.Content = isEnglish ? "－ Remove" : "－ 刪除選取";
        BtnExport.Content = isEnglish ? "💾 Export Preset" : "💾 匯出設定";
        BtnImport.Content = isEnglish ? "📂 Import Preset" : "📂 匯入設定";
        ChkExclude.Content = isEnglish ? "🛡 Exclude mode: protect this color from replacement" : "🛡 排除模式：保護這個顏色，不被替換";

        // 規則清單裡的「保護」標籤（DynamicResource，會即時更新）
        Resources["ProtectText"] = isEnglish ? "  🛡 Protected" : "  🛡 保護";

        TxtSec2.Text = isEnglish ? "2. Target Color (New)" : "2. 替換目標色彩";
        BtnRandom.Content = isEnglish ? "🎲 Random Color" : "🎲 隨機顏色";
        BtnTexture.Content = isEnglish ? "🖼 Use image as base" : "🖼 使用圖片當基底";
        TxtSec3.Text = isEnglish ? "3. Tolerance & Shading" : "3. 容許度與光影調整";
        BtnReset.Content = isEnglish ? "↺ Reset Defaults" : "↺ 恢復預設值";
        BtnBatch.Content = isEnglish ? "⚡ Batch Process & Export All ⚡" : "⚡ 批次處理並輸出全部圖片 ⚡";

        UpdateSliderLabels();
        RefreshTargetPreview();

        if (StatusText.Text == "就緒" || StatusText.Text == "Ready")
            StatusText.Text = isEnglish ? "Ready" : "就緒";

        Title = isEnglish ? "Nine Sols - Eigong Skin/Effect Customizer" : "九日 - 易公皮膚/特效修改工具";
    }
    #endregion
}