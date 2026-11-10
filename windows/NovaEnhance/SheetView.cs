using System.IO.Compression;
using System.Text;
using System.Xml;

namespace NovaEnhance;

/// <summary>One sheet of a workbook, as cells (row, column → text).</summary>
class SheetData
{
    public string Name = "";
    public readonly Dictionary<(int R, int C), string> Cells = new();
    public int Rows, Cols;
}

/// <summary>Reads .xlsx / .xlsm (directly), .csv, and .xls / .xlsb (through Excel, saved once as .xlsx).</summary>
static class SheetReader
{
    public static List<SheetData> Load(string path, int maxCells = 400_000)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".csv") return new() { Csv(path) };
        if (ext is ".xls" or ".xlsb" or ".ods")
        {
            var fi = new FileInfo(path);
            var dir = Path.Combine(Paths.Cache, "sheets");
            Directory.CreateDirectory(dir);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks)))[..20];
            var x = Path.Combine(dir, key + ".xlsx");
            if (!File.Exists(x))
            {
                bool ok = false;
                var t = new Thread(() => ok = OfficeConvert.Convert(path, x, "excel"));
                t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(TimeSpan.FromMinutes(2));
                if (!ok) throw new InvalidOperationException("Excel is needed to show old .xls / .xlsb files");
            }
            path = x;
        }
        return Xlsx(path, maxCells);
    }

    static SheetData Csv(string path)
    {
        var s = new SheetData { Name = Path.GetFileNameWithoutExtension(path) };
        int r = 0;
        foreach (var line in File.ReadLines(path).Take(20000))
        {
            r++;
            var cells = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (q) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') q = false; else sb.Append(ch); }
                else if (ch == '"') q = true;
                else if (ch == ',' || ch == ';' || ch == '\t') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            for (int c = 0; c < cells.Count; c++) if (cells[c].Length > 0) s.Cells[(r, c + 1)] = cells[c];
            s.Cols = Math.Max(s.Cols, cells.Count);
        }
        s.Rows = r;
        return s;
    }

    static List<SheetData> Xlsx(string path, int maxCells)
    {
        var res = new List<SheetData>();
        using var z = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var ss = z.GetEntry("xl/sharedStrings.xml");
        if (ss != null)
        {
            using var xr = XmlReader.Create(ss.Open(), new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Ignore });
            StringBuilder cur = null; bool inRph = false, moved = false;
            // ReadElementContentAsString already moves to the next node: don't skip it.
            while (moved || xr.Read())
            {
                moved = false;
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si") cur = new StringBuilder();
                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "rPh") inRph = !xr.IsEmptyElement;
                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "rPh") inRph = false;
                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t" && cur != null && !inRph) { cur.Append(xr.ReadElementContentAsString()); moved = true; }
                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "si") { shared.Add(cur?.ToString() ?? ""); cur = null; }
            }
        }
        // Sheet names and their files.
        var rels = new Dictionary<string, string>();
        var re = z.GetEntry("xl/_rels/workbook.xml.rels");
        if (re != null)
        {
            var d = new XmlDocument(); using (var st = re.Open()) d.Load(st);
            foreach (XmlElement e in d.GetElementsByTagName("Relationship"))
            {
                var tg = e.GetAttribute("Target").TrimStart('/');
                rels[e.GetAttribute("Id")] = tg.StartsWith("xl/") ? tg : "xl/" + tg;
            }
        }
        var wb = new XmlDocument(); using (var st = z.GetEntry("xl/workbook.xml")!.Open()) wb.Load(st);
        int total = 0;
        foreach (XmlElement sh in wb.GetElementsByTagName("sheet"))
        {
            var name = sh.GetAttribute("name");
            var rid = sh.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            if (!rels.TryGetValue(rid, out var target)) continue;
            var en = z.GetEntry(target);
            if (en == null) continue;
            var s = new SheetData { Name = name };
            using var xr = XmlReader.Create(en.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            string cref = null, type = null; int row = 0;
            bool moved2 = false;
            while ((moved2 || xr.Read()) && total < maxCells)
            {
                moved2 = false;
                if (xr.NodeType != XmlNodeType.Element) continue;
                if (xr.LocalName == "row") { if (int.TryParse(xr.GetAttribute("r"), out var rr)) row = rr; else row++; }
                else if (xr.LocalName == "c") { cref = xr.GetAttribute("r"); type = xr.GetAttribute("t"); }
                else if ((xr.LocalName == "v" || xr.LocalName == "t") && cref != null)
                {
                    var v = xr.ReadElementContentAsString();
                    moved2 = true;
                    if (type == "s" && int.TryParse(v, out var si)) v = si >= 0 && si < shared.Count ? shared[si] : "";
                    else if (type == "b") v = v == "1" ? "TRUE" : "FALSE";
                    var (r, c) = Ref(cref);
                    if (r == 0) r = row;
                    if (v.Length > 0 && r > 0 && c > 0) { s.Cells[(r, c)] = v; s.Rows = Math.Max(s.Rows, r); s.Cols = Math.Max(s.Cols, c); total++; }
                    if (xr.LocalName == "v") cref = null;
                }
            }
            res.Add(s);
        }
        return res;
    }

    public static (int R, int C) Ref(string a1)
    {
        int c = 0, i = 0;
        a1 = (a1 ?? "").Replace("$", "").ToUpperInvariant();
        while (i < a1.Length && char.IsLetter(a1[i])) { c = c * 26 + (a1[i] - 'A' + 1); i++; }
        int.TryParse(a1[i..], out var r);
        return (r, c);
    }

    public static string Col(int c) { var s = ""; while (c > 0) { var m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }
}

/// <summary>
/// 📊 Nova's own Excel view: the real grid with sheet tabs, every found cell in yellow (the current one in orange),
/// ◀ hit ▶ from cell to cell across the sheets. Instant, Excel doesn't need to be open. Designed by A.HAMROUNI
/// </summary>
class SheetView : UserControl
{
    readonly FlowLayoutPanel tabs;
    readonly DataGridView grid;
    readonly Label hitLbl, info;
    List<SheetData> book = new();
    int sheet;
    Matcher matcher;
    readonly List<(int S, int R, int C)> hits = new();
    int hitIndex = -1;
    int loadId;

    public SheetView()
    {
        BackColor = Color.FromArgb(0x0E, 0x11, 0x16);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 2), BackColor = BackColor };
        bar.Controls.Add(Btn("◀ hit", () => Go(hitIndex - 1)));
        hitLbl = Th.L("", 10, Th.Text, true); hitLbl.Margin = new Padding(4, 7, 4, 0);
        bar.Controls.Add(hitLbl);
        bar.Controls.Add(Btn("hit ▶", () => Go(hitIndex + 1)));
        info = Th.L("", 9, Th.Muted); info.Margin = new Padding(10, 8, 0, 0);
        bar.Controls.Add(info);
        tabs = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, MaximumSize = new Size(4000, 64), AutoScroll = true, Padding = new Padding(6, 2, 6, 4), BackColor = Color.FromArgb(0x0B, 0x0D, 0x12) };
        grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            BackgroundColor = Color.FromArgb(0x12, 0x15, 0x1C), GridColor = Color.FromArgb(0x2E, 0x35, 0x46), BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.CellSelect, RowHeadersWidth = 56, Font = Th.F(9.5f),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 24
        };
        grid.DefaultCellStyle.BackColor = Color.FromArgb(0x12, 0x15, 0x1C);
        grid.DefaultCellStyle.ForeColor = Th.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x2E, 0x2A, 0x55);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0x1D, 0x21, 0x30);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Th.Muted;
        grid.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(0x1D, 0x21, 0x30);
        grid.RowHeadersDefaultCellStyle.ForeColor = Th.Muted;
        grid.CellValueNeeded += (_, e) =>
        {
            if (sheet < 0 || sheet >= book.Count) return;
            e.Value = book[sheet].Cells.TryGetValue((e.RowIndex + 1, e.ColumnIndex + 1), out var v) ? v : "";
        };
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var k = hits.FindIndex(h => h.S == sheet && h.R == e.RowIndex + 1 && h.C == e.ColumnIndex + 1);
            if (k < 0) return;
            e.CellStyle.BackColor = k == hitIndex ? Color.FromArgb(0xFF, 0x9F, 0x2E) : Th.Yellow;
            e.CellStyle.ForeColor = Color.Black;
            e.CellStyle.SelectionBackColor = Color.FromArgb(0xFF, 0x9F, 0x2E);
            e.CellStyle.SelectionForeColor = Color.Black;
        };
        grid.RowPostPaint += (_, e) =>
            TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), Th.F(8.5f), new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth - 6, e.RowBounds.Height), Th.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F3 || (e.Control && e.KeyCode == Keys.Right)) { Go(hitIndex + (e.Shift ? -1 : 1)); e.Handled = true; }
        };
        Controls.Add(grid);
        Controls.Add(tabs);
        Controls.Add(bar);
    }

    static Button Btn(string s, Action a) { var b = Th.B(s, a); b.Font = Th.F(9); b.MinimumSize = new Size(0, 28); b.Margin = new Padding(2, 2, 2, 0); return b; }

    /// <summary>Opens the workbook; the found cells are marked, starting at the cell of the result (e.g. cell:BOQ!D45).</summary>
    public async void Load(string path, Matcher m, string startGo)
    {
        var my = ++loadId;
        matcher = m;
        hits.Clear(); hitIndex = -1; book = new();
        tabs.Controls.Clear(); grid.RowCount = 0; grid.ColumnCount = 0;
        hitLbl.Text = ""; info.Text = "Opening the workbook…";
        try
        {
            var b = await Task.Run(() => SheetReader.Load(LongPath.Usable(path)));
            if (my != loadId) return;
            book = b;
            await Task.Run(() =>
            {
                for (int s = 0; s < book.Count; s++)
                    foreach (var kv in book[s].Cells.OrderBy(k => k.Key.R).ThenBy(k => k.Key.C))
                        if (matcher != null && matcher.Find(kv.Value).Count > 0) hits.Add((s, kv.Key.R, kv.Key.C));
            });
            if (my != loadId) return;
            for (int s = 0; s < book.Count; s++)
            {
                var ss = s;
                var n = hits.Count(h => h.S == s);
                var t = Th.B(book[s].Name + (n > 0 ? $"  ({n})" : ""), () => ShowSheet(ss));
                t.Font = Th.F(9, n > 0); t.MinimumSize = new Size(0, 26); t.Margin = new Padding(2);
                tabs.Controls.Add(t);
            }
            // Start at the result's cell when known.
            int start = 0;
            if (!string.IsNullOrEmpty(startGo) && startGo.StartsWith("cell:") && startGo.Contains('!'))
            {
                var sh = startGo[5..startGo.LastIndexOf('!')];
                var (r, c) = SheetReader.Ref(startGo[(startGo.LastIndexOf('!') + 1)..]);
                var si = book.FindIndex(x => x.Name.Equals(sh, StringComparison.OrdinalIgnoreCase));
                var k = hits.FindIndex(h => h.S == si && h.R >= r);
                if (k >= 0) start = k;
            }
            info.Text = $"{book.Count} sheet{(book.Count == 1 ? "" : "s")} · {hits.Count} found cell{(hits.Count == 1 ? "" : "s")} · F3 = next";
            if (hits.Count > 0) Go(start); else ShowSheet(0);
        }
        catch (Exception e)
        {
            if (my != loadId) return;
            info.Text = "⚠ " + e.Message;
            Log.Warn("Preview · Excel", Path.GetFileName(path) + ": " + e.Message);
        }
    }

    void ShowSheet(int s)
    {
        if (s < 0 || s >= book.Count) return;
        sheet = s;
        var d = book[s];
        grid.RowCount = 0;
        var cols = Math.Min(Math.Max(d.Cols, 8), 250);
        grid.ColumnCount = cols;
        for (int c = 0; c < cols; c++) { grid.Columns[c].HeaderText = SheetReader.Col(c + 1); grid.Columns[c].Width = 110; grid.Columns[c].SortMode = DataGridViewColumnSortMode.NotSortable; }
        grid.RowCount = Math.Max(d.Rows, 30);
        // Wider columns where the text is long (first 300 rows).
        for (int c = 0; c < cols; c++)
        {
            var longest = d.Cells.Where(k => k.Key.C == c + 1 && k.Key.R <= 300).Select(k => k.Value.Length).DefaultIfEmpty(0).Max();
            grid.Columns[c].Width = Math.Clamp(longest * 7 + 16, 60, 420);
        }
        foreach (Button b in tabs.Controls) b.BackColor = Th.Card;
        if (s < tabs.Controls.Count) tabs.Controls[s].BackColor = Color.FromArgb(0x2E, 0x2A, 0x55);
        grid.Invalidate();
    }

    void Go(int i)
    {
        if (hits.Count == 0) return;
        hitIndex = ((i % hits.Count) + hits.Count) % hits.Count;
        var (s, r, c) = hits[hitIndex];
        if (s != sheet || grid.RowCount == 0) ShowSheet(s);
        hitLbl.Text = $"{hitIndex + 1} / {hits.Count}";
        if (r - 1 < grid.RowCount && c - 1 < grid.ColumnCount)
        {
            grid.CurrentCell = grid[c - 1, r - 1];
            grid.FirstDisplayedScrollingRowIndex = Math.Max(0, r - 6);
        }
        grid.Invalidate();
        grid.Focus();
    }
}
