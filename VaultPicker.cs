using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class Entry {
    public string Full, Relative, Name;
    public bool Folder;
    public Entry(string full, string relative, bool folder) {
        Full = full; Relative = relative; Name = Path.GetFileName(full); Folder = folder;
    }
}

static class Indexer {
    static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "node_modules", "bin", "obj", "__pycache__", "dist", "build"
    };
    public static List<Entry> Scan(string root, out int skipped) {
        var result = new List<Entry>(); var todo = new Stack<string>(); skipped = 0;
        todo.Push(root); root = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        while (todo.Count > 0) {
            var dir = todo.Pop();
            try {
                foreach (var p in Directory.GetFileSystemEntries(dir)) {
                    try {
                        var name = Path.GetFileName(p); var attr = File.GetAttributes(p);
                        if (name.StartsWith(".") || (attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                        bool folder = (attr & FileAttributes.Directory) != 0;
                        if (folder && Skip.Contains(name)) continue;
                        if (!folder) result.Add(new Entry(p, p.Substring(root.Length), false));
                        if (folder) todo.Push(p);
                    } catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; }
                }
            } catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; }
        }
        return result;
    }
    public static int Score(Entry e, string query) {
        int total = 0;
        foreach (var term in query.ToLowerInvariant().Split(new char[] {' '}, StringSplitOptions.RemoveEmptyEntries)) {
            string n = e.Name.ToLowerInvariant(), r = e.Relative.ToLowerInvariant().Replace('\\', '/');
            string t = term.Replace('\\', '/');
            if (n == t || Path.GetFileNameWithoutExtension(n) == t) total += 0;
            else if (n.StartsWith(t)) total += 10;
            else if (n.Contains(t)) total += 25;
            else if (r.Contains(t)) total += 50;
            else {
                int pos = 0, first = -1, last = 0;
                foreach (char c in t) { int found = r.IndexOf(c, pos); if (found < 0) return -1; if (first < 0) first = found; last = found; pos = found + 1; }
                total += 100 + last - first;
            }
        }
        return total;
    }
    public static string PathText(IEnumerable<Entry> entries, bool absolute = false) {
        return string.Join(Environment.NewLine, entries.Select(e => absolute ? e.Full : e.Relative.Replace('\\', '/')));
    }
    public static bool CanCopy(string root, string full) {
        try {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(full);
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return false;
            // Recheck every component in case an indexed folder became a junction after scanning.
            string component = file;
            while (true) {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(component.TrimEnd(Path.DirectorySeparatorChar), prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
                component = Path.GetDirectoryName(component);
                if (component == null) return false;
            }
            return true;
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
}

sealed class Picker : Form {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    const int Hotkey = 7421;
    TextBox search = new TextBox(); ListView list = new ListView();
    Label status = new Label(); NotifyIcon tray; System.Windows.Forms.Timer debounce;
    List<Entry> entries = new List<Entry>(); DateTime scanned = DateTime.MinValue;
    bool scanning, quitting; string root; int skipped;
    public bool HotkeyRegistered { get; private set; }
    public Picker(string vault) {
        root = vault;
        Text = "Obsidian 文件快选器 · Ctrl+Alt+Shift+O"; Size = new Size(920, 590); MinimumSize = new Size(700, 430);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(245, 247, 251); KeyPreview = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "只搜索 " + new DirectoryInfo(root).Name + " 库内文件    ·    Enter 复制基于库的相对路径", Dock = DockStyle.Fill }, 0, 0);
        search.Dock = DockStyle.Fill; search.Font = new Font(Font.FontFamily, 13); search.AccessibleName = "搜索名称或路径";
        layout.Controls.Add(search, 0, 1);
        list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.MultiSelect = true;
        list.Columns.Add("名称", 260); list.Columns.Add("类型", 80); list.Columns.Add("库内位置", 480);
        layout.Controls.Add(list, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        AddButton(buttons, "复制相对路径 ↵", () => Copy());
        AddButton(buttons, "复制绝对路径", () => Copy(true));
        AddButton(buttons, "刷新 F5", RefreshIndex); layout.Controls.Add(buttons, 0, 3);
        status.Dock = DockStyle.Fill; status.ForeColor = Color.FromArgb(75, 85, 100); layout.Controls.Add(status, 0, 4);
        debounce = new System.Windows.Forms.Timer { Interval = 110 };
        debounce.Tick += (s, e) => { debounce.Stop(); Render(); };
        search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
        KeyDown += KeysPressed;
        list.DoubleClick += (s, e) => Copy();
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Obsidian 文件快选器 · Ctrl+Alt+Shift+O", Visible = true };
        var menu = new ContextMenuStrip(); menu.Items.Add("搜索 Ctrl+Alt+Shift+O", null, (s, e) => OpenPicker());
        menu.Items.Add("刷新索引", null, (s, e) => RefreshIndex());
        menu.Items.Add("退出", null, (s, e) => { quitting = true; Close(); });
        tray.ContextMenuStrip = menu; tray.DoubleClick += (s, e) => OpenPicker();
        FormClosing += (s, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        var handle = Handle;
        HotkeyRegistered = RegisterHotKey(handle, Hotkey, 0x4000 | 0x0002 | 0x0001 | 0x0004, (uint)Keys.O);
        if (!HotkeyRegistered) tray.ShowBalloonTip(6000, "快捷键被占用", "Ctrl+Alt+Shift+O 注册失败，请双击托盘图标打开搜索。", ToolTipIcon.Warning);
        RefreshIndex();
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == Hotkey) OpenPicker();
        base.WndProc(ref m);
    }
    void AddButton(Control parent, string text, Action action) {
        var b = new Button { Text = text, AutoSize = true, Height = 32, BackColor = Color.White, FlatStyle = FlatStyle.Flat };
        b.Click += (s, e) => action(); parent.Controls.Add(b);
    }
    public void OpenPicker() {
        Show(); WindowState = FormWindowState.Normal; Activate(); SetForegroundWindow(Handle); search.Focus(); search.SelectAll();
        if ((DateTime.Now - scanned).TotalSeconds > 60) RefreshIndex();
    }
    public void PreviewForTest(string output) {
        entries = Indexer.Scan(root, out skipped);
        search.Text = "Obsidian文件快选器"; debounce.Stop(); Render();
        Show(); PerformLayout();
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(output); }
        quitting = true; Close();
    }
    async void RefreshIndex() {
        if (scanning) return; scanning = true; status.Text = "正在索引文件名与路径…";
        try {
            int omitted = 0;
            var found = await Task.Run(() => Indexer.Scan(root, out omitted));
            if (IsDisposed) return;
            entries = found; skipped = omitted; scanned = DateTime.Now; Render();
        } catch (Exception ex) { if (!IsDisposed) status.Text = "索引失败：" + ex.Message; }
        finally { scanning = false; }
    }
    void Render() {
        string query = search.Text.Trim();
        var hits = entries.Where(e => !e.Folder)
            .Select(e => new { E = e, S = Indexer.Score(e, query) }).Where(x => x.S >= 0)
            .OrderBy(x => x.S).ThenBy(x => x.E.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        list.BeginUpdate(); list.Items.Clear();
        foreach (var x in hits.Take(300)) {
            var e = x.E; var row = new ListViewItem(e.Name) { Tag = e };
            row.SubItems.Add(e.Folder ? "文件夹" : Path.GetExtension(e.Full)); row.SubItems.Add(e.Relative); list.Items.Add(row);
        }
        if (list.Items.Count > 0) { list.Items[0].Selected = true; list.Items[0].Focused = true; }
        list.EndUpdate();
        status.Text = string.Format("{0} 个匹配 / {1} 个库内文件{2}    ·    Esc 收起，F5 刷新\nEnter 或双击复制路径；Ctrl / Shift + 点击可多选，每条路径占一行", hits.Count, entries.Count, hits.Count > 300 ? "（显示前 300 个）" : "");
        if (skipped > 0) status.Text += "；" + skipped + " 项无法访问";
    }
    List<Entry> Selected() { return list.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag).ToList(); }
    void Copy(bool absolute = false) {
        if (debounce.Enabled) { debounce.Stop(); Render(); }
        var selected = Selected(); if (selected.Count == 0) { status.Text = "请先选择一个结果。"; return; }
        if (selected.Any(e => e.Folder || !Indexer.CanCopy(root, e.Full))) { status.Text = "只能复制本库中仍存在的普通文件路径，请按 F5 刷新。"; return; }
        try {
            Clipboard.SetText(Indexer.PathText(selected, absolute));
            Hide();
        } catch (ExternalException) { status.Text = "剪贴板正在被其他应用使用，请再试一次。"; }
    }
    void KeysPressed(object sender, KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape) { Hide(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.F5) { RefreshIndex(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter) { Copy(); e.SuppressKeyPress = true; }
        else if (e.Control && e.Shift && e.KeyCode == Keys.C) { Copy(); e.SuppressKeyPress = true; }
        else if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && search.Focused) {
            if (list.Items.Count > 0) {
                int current = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : 0;
                int next = Math.Max(0, Math.Min(list.Items.Count - 1, current + (e.KeyCode == Keys.Down ? 1 : -1)));
                foreach (ListViewItem i in list.SelectedItems) i.Selected = false;
                list.Items[next].Selected = true; list.Items[next].Focused = true; list.Items[next].EnsureVisible();
            }
            e.SuppressKeyPress = true;
        }
    }
    protected override void Dispose(bool disposing) {
        if (disposing) {
            if (IsHandleCreated && HotkeyRegistered) { UnregisterHotKey(Handle, Hotkey); HotkeyRegistered = false; }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (debounce != null) { debounce.Dispose(); debounce = null; }
        }
        base.Dispose(disposing);
    }
}

static class Program {
    static string ResolveRoot() {
        string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vault.local.txt");
        if (File.Exists(config)) return Path.GetFullPath(File.ReadAllText(config).Trim());
        using (var choose = new FolderBrowserDialog { Description = "选择要搜索的 Obsidian 库根目录（只需设置一次）", ShowNewFolderButton = false }) {
            if (choose.ShowDialog() != DialogResult.OK) return null;
            File.WriteAllText(config, choose.SelectedPath); return choose.SelectedPath;
        }
    }
    [STAThread] static int Main(string[] args) {
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--preview-test")) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string previewRoot = ResolveRoot(); if (previewRoot == null) return 0;
            using (var preview = new Picker(previewRoot)) {
                preview.Location = new Point(-3000, -3000); preview.StartPosition = FormStartPosition.Manual;
                preview.PreviewForTest(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"));
            }
            return 0;
        }
        bool fresh;
        using (var mutex = new Mutex(true, "Local\\ObsidianVaultPicker", out fresh)) {
            if (!fresh) { using (var signal = EventWaitHandle.OpenExisting("Local\\ObsidianVaultPickerOpen")) signal.Set(); return 0; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = ResolveRoot(); if (root == null) return 0;
            if (!Directory.Exists(root)) { MessageBox.Show("找不到库目录：" + root); return 1; }
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ObsidianVaultPickerOpen"))
            using (var picker = new Picker(Path.GetFullPath(root))) {
                var poll = new System.Windows.Forms.Timer { Interval = 250 };
                poll.Tick += (s, e) => { if (signal.WaitOne(0)) picker.OpenPicker(); };
                poll.Start(); if (args.Contains("--show")) picker.OpenPicker();
                // Application.Run(form) shows the form unconditionally. A context keeps startup in the tray.
                var context = new ApplicationContext(); picker.FormClosed += (s, e) => context.ExitThread();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime-status.txt"), "Started " + DateTime.Now.ToString("s") + "\nHotkeyRegistered=" + picker.HotkeyRegistered + "\nScope=" + root + "\nMode=CopyVaultRelativeOrAbsoluteFilePaths");
                Application.Run(context); poll.Dispose();
            }
            mutex.ReleaseMutex(); return 0;
        }
    }
    static int SelfTest() {
        string temp = Path.Combine(Path.GetTempPath(), "VaultPicker-test-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.txt");
        try {
            Directory.CreateDirectory(Path.Combine(temp, "项目 空格")); Directory.CreateDirectory(Path.Combine(temp, ".obsidian"));
            File.WriteAllText(Path.Combine(temp, "项目 空格", "中文笔记.md"), "fixture"); File.WriteAllText(Path.Combine(temp, ".obsidian", "hidden.json"), "{}");
            File.WriteAllText(Path.Combine(temp, "AGENTS.md"), "fixture");
            int skipped; var index = Indexer.Scan(temp, out skipped);
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception(name); };
            var note = index.Single(e => e.Name == "中文笔记.md");
            check(index.Count == 2 && index.All(e => !e.Folder), "index files only, exclude folders and hidden");
            check(Indexer.Score(note, "中文笔记") == 0, "exact title");
            check(Indexer.Score(note, "项目 笔记") >= 0, "multiple terms");
            check(Indexer.Score(note, "中记") >= 100, "ordered fuzzy matching");
            check(Indexer.Score(note, "不存在") == -1, "no match");
            check(Indexer.Score(note, "项目 空格/中文") >= 0, "path separators");
            check(Indexer.PathText(new[] {note}) == "项目 空格/中文笔记.md", "vault-relative Unicode path with forward slashes");
            check(Indexer.CanCopy(temp, note.Full), "allow file inside vault");
            check(!Indexer.CanCopy(temp, Path.Combine(temp, "项目 空格")), "reject directory");
            check(!Indexer.CanCopy(temp, Path.Combine(temp, "项目 空格", "missing.md")), "reject missing file");
            check(!Indexer.CanCopy(temp, Path.Combine(temp + "-other", "outside.md")), "reject sibling prefix");
            check(!Indexer.CanCopy(temp, Path.Combine(temp, "..", "outside.md")), "reject traversal");
            check(!Indexer.CanCopy(temp + "-other", note.Full), "reject existing file outside configured vault");
            check(Indexer.PathText(new[] {note, note}) == "项目 空格/中文笔记.md" + Environment.NewLine + "项目 空格/中文笔记.md", "multiselect relative newline text");
            check(Indexer.PathText(new[] {note}, true) == note.Full, "absolute Unicode path");
            check(Indexer.PathText(new[] {note, note}, true) == note.Full + Environment.NewLine + note.Full, "multiselect absolute newline text");
            int vaultSkipped; var vault = Indexer.Scan(temp, out vaultSkipped);
            check(vault.Any(e => e.Relative == "AGENTS.md"), "vault root file index");
            check(Indexer.PathText(new[] {vault.Single(e => e.Relative == "AGENTS.md")}) == "AGENTS.md", "root file relative path");
            check(vault.All(e => !e.Folder && Indexer.CanCopy(temp, e.Full)), "all indexed paths are vault files");
            File.WriteAllText(output, "PASS: 19 checks\nFixture files=" + vault.Count + "\nSkipped=" + vaultSkipped); return 0;
        } catch (Exception ex) { File.WriteAllText(output, "FAIL: " + ex.ToString()); return 1; }
        finally {
            string resolved = Path.GetFullPath(temp);
            string allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("VaultPicker-test-") && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
