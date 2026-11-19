namespace NovaEnhance;

/// <summary>↔ Every list and grid in Nova: columns you can stretch (drag the edge · double-click it to fit), widths remembered per table. Designed by A.HAMROUNI</summary>
static class GridCols
{
    static string Key(string name) => "win_cols_" + name;

    static int[] Load(string name)
    {
        try { return Store.Str(Key(name), "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); } catch { return Array.Empty<int>(); }
    }

    /// <summary>A details ListView: its columns can be resized (built in), the widths are restored and saved.</summary>
    public static T Remember<T>(T lv, string name) where T : ListView
    {
        bool restoring = false;
        void Restore()
        {
            var w = Load(name);
            restoring = true;
            for (int i = 0; i < lv.Columns.Count && i < w.Length; i++) if (w[i] >= 24) lv.Columns[i].Width = w[i];
            restoring = false;
        }
        lv.HandleCreated += (_, _) => Restore();
        if (lv.IsHandleCreated) Restore();
        lv.ColumnWidthChanged += (_, _) =>
        {
            if (restoring || lv.Columns.Count == 0) return;
            Store.Set(Key(name), string.Join(",", lv.Columns.Cast<ColumnHeader>().Select(c => c.Width)));
        };
        return lv;
    }

    /// <summary>A DataGridView: every column can be resized by the user (drag · double-click the edge to fit), widths restored and saved.</summary>
    public static DataGridView Remember(DataGridView g, string name)
    {
        g.AllowUserToResizeColumns = true;
        bool restoring = false;
        void Restore()
        {
            var w = Load(name);
            restoring = true;
            // Columns that size themselves can't be stretched: fit them once, then they are yours to stretch.
            if (g.AutoSizeColumnsMode != DataGridViewAutoSizeColumnsMode.None && g.AutoSizeColumnsMode != DataGridViewAutoSizeColumnsMode.Fill && g.Rows.Count > 0)
            {
                try { g.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells); } catch { }
                g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            }
            for (int i = 0; i < g.Columns.Count; i++)
            {
                var c = g.Columns[i];
                c.Resizable = DataGridViewTriState.True;
                if (i < w.Length && w[i] >= 24) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; c.Width = w[i]; }
            }
            restoring = false;
        }
        g.HandleCreated += (_, _) => Restore();
        g.DataBindingComplete += (_, _) => Restore();
        bool once = false;
        g.VisibleChanged += (_, _) => { if (!once && g.Visible && g.Rows.Count > 0) { once = true; Restore(); } };
        if (g.IsHandleCreated) Restore();
        g.ColumnWidthChanged += (_, e) =>
        {
            if (restoring) return;
            Store.Set(Key(name), string.Join(",", g.Columns.Cast<DataGridViewColumn>().Select(c => c.Width)));
        };
        return g;
    }
}
