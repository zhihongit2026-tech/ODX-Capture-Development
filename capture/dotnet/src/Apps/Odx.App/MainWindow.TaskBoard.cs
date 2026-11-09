using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Odx.Capture;
using Odx.Core;

namespace Odx.App;

public sealed class TaskBoardEntry : INotifyPropertyChanged
{
    public string Code { get; init; } = "";
    public string Part { get; init; } = "";
    public string Scope { get; init; } = "";
    public bool OnTbrain { get; set; }
    public string RemoteStatus { get; set; } = "";
    private bool _recorded;
    public bool IsRecorded
    {
        get => _recorded;
        set
        {
            if (_recorded == value) return;
            _recorded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecorded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }
    public string Key => Scope + "|" + Code + "|" + Part;
    public string DisplayId => Code + (Part.Length > 0 ? " (" + Part + ")" : "");
    public string StatusText => (OnTbrain ? RemoteStatus switch
    {
        "Claimed" => "● Claimed · cần quay",
        "Submitted" => "● Submitted · đã nộp trên Tbrain",
        "Accepted" => "● Accepted · Tbrain đã chấp nhận",
        "Reviewed" => "● Reviewed · đã được xem xét",
        "" => "● Có trên Tbrain · chưa rõ trạng thái",
        _ => "● " + RemoteStatus
    } : "○ Chưa xác nhận trên Tbrain") + (IsRecorded ? "  ·  ✓ Đã quay" : "");
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private sealed class TaskWebSnapshot
    {
        public string Project { get; set; } = "";
        public string Path { get; set; } = "";
        public List<TaskWebRow> Tasks { get; set; } = [];
    }
    private sealed class TaskWebRow
    {
        public string Id { get; set; } = "";
        public string Part { get; set; } = "";
        public string Status { get; set; } = "";
    }

    // Read only: scans visible Task row labels on the currently signed-in Tbrain page.
    // No cookies, OAuth tokens, passwords, account information or network calls.
    private const string TbrainTaskReadScript = """
    (() => {
      if (location.hostname.toLowerCase() !== 'work.tbrain.ai') return {project:'',path:'',tasks:[]};
      const matchId = /\b([A-Z][A-Z0-9]{1,10}-\d{1,6}-S\d{1,3})\b/i;
      const statusRE = /\b(Claimed|Submitted|Accepted|Reviewed|Rejected|Available|Draft)\b/i;
      const tasks = [], seen = new Set();
      function collect(node) {
        const txt = (node.innerText || '').replace(/\s+/g, ' ').trim();
        if (txt.length < 6 || txt.length > 400) return;
        const id = txt.match(matchId);
        if (!id) return;
        const part = txt.match(/\(P(\d{1,2})\)/i) || txt.match(/#p(\d{1,2})/i);
        const partName = part ? 'P' + part[1] : '';
        const key = id[1].toUpperCase() + '|' + partName;
        if (seen.has(key)) return;
        seen.add(key);
        const state = txt.match(statusRE);
        tasks.push({id:id[1].toUpperCase(),part:partName,status:state?state[1]:''});
      }
      for (const node of document.querySelectorAll('tbody tr, [role="row"], table tr')) collect(node);
      if (!tasks.length) {
        // Fallback for Tbrain layouts composed of divs instead of table rows.
        const nodes = [...document.querySelectorAll('a,li,div')].filter(el => {
          const t = el.innerText || '';
          return t.length > 8 && t.length < 240 && matchId.test(t) && statusRE.test(t);
        });
        nodes.sort((a,b)=>(a.innerText || '').length - (b.innerText || '').length);
        for (const node of nodes.slice(0,300)) collect(node);
      }
      const h1 = document.querySelector('main h1') || document.querySelector('h1');
      return {project:(h1?.textContent || '').trim().slice(0,100),
              path:location.pathname, tasks:tasks.slice(0,300)};
    })()
    """;

    private static readonly Regex TaskCodePattern = new(
        @"^(?<stem>[A-Z][A-Z0-9]{1,10}-\d{1,6})-S(?<index>\d{1,3})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly ObservableCollection<TaskBoardEntry> _taskEntries = [];
    private readonly Dictionary<string, string> _localTaskMarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _taskBoardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _taskScope = "";
    private string _taskStem = "";
    private string _selectedTaskCode = "";
    private string _selectedTaskPart = "";
    private string _selectedTaskScope = "";
    private string _pinnedTaskKey = "";
    private string _pinnedTaskCode = "";
    private bool _taskBoardStarted;
    private bool _taskSyncInProgress;
    private bool _taskRebinding;
    private bool _taskManualEditing;
    private DateTime _lastTaskSync = DateTime.MinValue;
    private ICaptureRun? _observedCapture;

    private static string LocalMarksPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OdxCapture", "TaskBoard", "completed.json");

    private void TaskBoard_Loaded(object sender, RoutedEventArgs e)
    {
        if (_taskBoardStarted) return;
        _taskBoardStarted = true;
        LoadTaskMarks();
        TaskListBox.ItemsSource = _taskEntries;
        _taskBoardTimer.Tick += (_, _) =>
        {
            UpdateTaskRecordingDisplay();
            if ((DateTime.UtcNow - _lastTaskSync).TotalSeconds >= 5)
                _ = TaskSyncAsync(false);
        };
        _taskBoardTimer.Start();
        _ = TaskSyncAsync(false);
    }

    private void LoadTaskMarks()
    {
        try
        {
            if (!File.Exists(LocalMarksPath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LocalMarksPath));
            if (saved is null) return;
            foreach (var pair in saved) _localTaskMarks[pair.Key] = pair.Value;
        }
        catch (Exception)
        {
            TaskLocalStatus.Text = "Không đọc được dấu tích cục bộ; chưa tự đánh dấu Task nào.";
        }
    }

    private void SaveTaskMarks()
    {
        try
        {
            var folder = Path.GetDirectoryName(LocalMarksPath)!;
            Directory.CreateDirectory(folder);
            string temp = LocalMarksPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_localTaskMarks));
            File.Move(temp, LocalMarksPath, true);
        }
        catch (Exception)
        {
            TaskLocalStatus.Text = "Không lưu được dấu tích Task; kiểm tra quyền ghi dữ liệu.";
        }
    }

    private void TaskSync_Click(object sender, RoutedEventArgs e) => _ = TaskSyncAsync(true);

    private async Task TaskSyncAsync(bool manual)
    {
        if (_taskSyncInProgress) return;
        _lastTaskSync = DateTime.UtcNow;
        var web = TbrainBrowser.CoreWebView2;
        if (web is null || !Uri.TryCreate(web.Source, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("work.tbrain.ai", StringComparison.OrdinalIgnoreCase))
        {
            if (manual) TaskSyncStatus.Text = "Hãy mở trang Tasks của một dự án Tbrain trong khung bên phải.";
            return;
        }

        _taskSyncInProgress = true;
        try
        {
            string raw = await web.ExecuteScriptAsync(TbrainTaskReadScript);
            var snapshot = JsonSerializer.Deserialize<TaskWebSnapshot>(
                raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (snapshot?.Tasks is not { Count: > 0 })
            {
                if (manual || _taskEntries.Count == 0)
                    TaskSyncStatus.Text = "Chưa đọc được Task. Hãy mở dự án → tab Tasks rồi bấm Đồng bộ.";
                return;
            }
            ApplyTaskSnapshot(snapshot);
        }
        catch (Exception)
        {
            if (manual) TaskSyncStatus.Text = "Không đọc được danh sách Task từ trang hiện tại. Vẫn có thể nhập mã thủ công.";
        }
        finally { _taskSyncInProgress = false; }
    }

    private void ApplyTaskSnapshot(TaskWebSnapshot snapshot)
    {
        var parsed = snapshot.Tasks.Select(t => new
        {
            Item = t,
            Match = TaskCodePattern.Match(t.Id.Trim())
        }).Where(x => x.Match.Success && int.TryParse(x.Match.Groups["index"].Value, out int n)
            && n is > 0 and <= 999).ToList();
        if (parsed.Count == 0) return;

        var groups = parsed.GroupBy(x => x.Match.Groups["stem"].Value.ToUpperInvariant())
                           .OrderByDescending(g => g.Count()).ToList();
        string stem = groups[0].Key;
        var tasks = groups[0].ToList();
        int highest = tasks.Max(x => int.Parse(x.Match.Groups["index"].Value));
        int displayMax = Math.Min(highest, 200);
        string scope = (snapshot.Path + "|" + snapshot.Project + "|" + stem).ToUpperInvariant();
        var identified = tasks.GroupBy(x => x.Item.Id.ToUpperInvariant() + "|" + x.Item.Part.ToUpperInvariant())
                              .ToDictionary(g => g.Key, g => g.First().Item);

        _taskScope = scope;
        _taskStem = stem;
        var rows = new List<TaskBoardEntry>();
        for (int i = 1; i <= displayMax; i++)
        {
            string code = stem + "-S" + i.ToString("D2");
            var fromWeb = identified.Where(kv => kv.Value.Id.Equals(code, StringComparison.OrdinalIgnoreCase))
                                    .Select(kv => kv.Value).ToList();
            if (fromWeb.Count == 0)
                rows.Add(new TaskBoardEntry { Code = code, Scope = scope, IsRecorded = Marked(scope, code, "") });
            else foreach (var web in fromWeb)
            {
                string part = web.Part.ToUpperInvariant();
                rows.Add(new TaskBoardEntry
                {
                    Code = code, Part = part, Scope = scope, OnTbrain = true,
                    RemoteStatus = web.Status,
                    IsRecorded = Marked(scope, code, part)
                });
            }
        }

        // A selected local Task may be absent from the currently visible remote rows.
        if (_selectedTaskCode.Length > 0 && _selectedTaskCode.StartsWith(stem + "-S", StringComparison.OrdinalIgnoreCase)
            && !rows.Any(row => row.Code == _selectedTaskCode && row.Part == _selectedTaskPart))
            rows.Add(new TaskBoardEntry { Code = _selectedTaskCode, Part = _selectedTaskPart, Scope = scope,
                IsRecorded = Marked(scope, _selectedTaskCode, _selectedTaskPart) });

        _taskRebinding = true;
        try
        {
            _taskEntries.Clear();
            foreach (var row in rows.OrderBy(x => int.Parse(TaskCodePattern.Match(x.Code).Groups["index"].Value))
                                    .ThenBy(x => x.Part))
                _taskEntries.Add(row);
            RefreshTaskList();
            var selected = _taskEntries.FirstOrDefault(row => row.Code == _selectedTaskCode && row.Part == _selectedTaskPart);
            if (selected is not null) TaskListBox.SelectedItem = selected;
        }
        finally { _taskRebinding = false; }

        TaskSyncStatus.Text = $"{snapshot.Project} · {tasks.Count} Task xác nhận trên trang · hiển thị S01–S{displayMax:D2}"
            + (highest > displayMax ? " · dải dài: dùng nhập mã cho Task lớn hơn" : "");
        UpdateSelectedTaskLabel();
    }

    private bool Marked(string scope, string code, string part) =>
        _localTaskMarks.ContainsKey(scope + "|" + code + "|" + part);

    private void RefreshTaskList()
    {
        string query = TaskSearchBox.Text.Trim().ToUpperInvariant();
        string mode = (TaskFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var visible = _taskEntries.Where(row =>
            (query.Length == 0 || row.DisplayId.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (mode == "all" || (mode == "done" ? row.IsRecorded : !row.IsRecorded))).ToList();
        // Do not mutate the master list when switching filters.
        TaskListBox.ItemsSource = visible;
    }

    private void TaskSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_taskBoardStarted) RefreshTaskList();
    }

    private void TaskFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_taskBoardStarted) RefreshTaskList();
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_taskRebinding || TaskListBox.SelectedItem is not TaskBoardEntry entry) return;
        ChooseTask(entry);
    }

    private void ChooseTask(TaskBoardEntry entry)
    {
        _selectedTaskCode = entry.Code;
        _selectedTaskPart = entry.Part;
        _selectedTaskScope = entry.Scope;
        if (!_taskManualEditing) TaskManualBox.Text = entry.Code;
        UpdateSelectedTaskLabel();
        ApplyTaskToHostIfSafe();
    }

    private void TaskManual_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TaskUse_Click(sender, new RoutedEventArgs());
        }
    }

    private void TaskUse_Click(object sender, RoutedEventArgs e)
    {
        string code = TaskManualBox.Text.Trim().ToUpperInvariant();
        var match = TaskCodePattern.Match(code);
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, out int index) || index <= 0)
        {
            TaskLocalStatus.Text = "Task ID không hợp lệ. Ví dụ đúng: AMRF-124-S15.";
            return;
        }
        string scope = _taskScope.Length > 0 ? _taskScope : "MANUAL|" + match.Groups["stem"].Value;
        TaskBoardEntry? entry = _taskEntries.FirstOrDefault(t => t.Code == code && t.Scope == scope);
        if (entry is null)
        {
            entry = new TaskBoardEntry { Code = code, Scope = scope, IsRecorded = Marked(scope, code, "") };
            _taskEntries.Add(entry);
        }
        _taskManualEditing = true;
        try { ChooseTask(entry); }
        finally { _taskManualEditing = false; }
        RefreshTaskList();
        TaskListBox.SelectedItem = TaskListBox.Items.Cast<TaskBoardEntry>().FirstOrDefault(x => x.Key == entry.Key);
    }

    private void ApplyTaskToHostIfSafe()
    {
        // Room host alone chooses the next round's ID. Never rename an active round.
        if (_run is not null || _state is RunState.Countdown or RunState.Starting or RunState.Stopping)
        {
            TaskLocalStatus.Text = "Task mới đã chọn cho phiên sau; phiên quay hiện tại giữ nguyên Task cũ.";
            return;
        }
        if (_agent?.Room?.CurrentSession is not null) return;
        if (_agent is { IsHost: false }) return;
        TaskBox.Text = _selectedTaskCode;
    }

    private void UpdateSelectedTaskLabel()
    {
        TaskSelectedText.Text = _selectedTaskCode.Length == 0 ? "Chưa chọn Task"
            : "Task đã chọn: " + _selectedTaskCode
              + (_selectedTaskPart.Length == 0 ? "" : " (" + _selectedTaskPart + ")");
    }

    private void TaskRecorded_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: TaskBoardEntry entry }) return;
        entry.IsRecorded = !entry.IsRecorded;
        if (entry.IsRecorded) _localTaskMarks[entry.Key] = "manual";
        else _localTaskMarks.Remove(entry.Key);
        SaveTaskMarks();
        TaskLocalStatus.Text = entry.DisplayId + (entry.IsRecorded
            ? " · Đã đánh dấu quay xong thủ công."
            : " · Đã bỏ dấu tích, video gốc không bị xóa.");
        RefreshTaskList();
    }

    private void UpdateTaskRecordingDisplay()
    {
        if (_run is { } active && !ReferenceEquals(_observedCapture, active))
            ObserveCapture(active);
        if (_run is null)
        {
            TaskElapsedText.Text = "00:00:00";
            TaskRecordingHint.Text = "Sẵn sàng · chọn Task ID rồi bắt đầu ghi bằng nút hoặc F10.";
            ApplyTaskToHostIfSafe();
            return;
        }
        var elapsed = DateTime.Now - _runStarted;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        TaskElapsedText.Text = elapsed.ToString(@"hh\:mm\:ss");
        TaskRecordingHint.Text = _pinnedTaskCode.Length > 0
            ? "Đang ghi: " + _pinnedTaskCode + " · đổi lựa chọn không ảnh hưởng video hiện tại"
            : "Đang ghi · chưa chọn Task ID để đối chiếu.";
    }

    private void ObserveCapture(ICaptureRun run)
    {
        _observedCapture = run;
        _pinnedTaskCode = _selectedTaskCode;
        _pinnedTaskKey = _selectedTaskCode.Length == 0 ? ""
            : _selectedTaskScope + "|" + _selectedTaskCode + "|" + _selectedTaskPart;
        string pinnedKey = _pinnedTaskKey;
        string pinnedCode = _pinnedTaskCode;
        _ = run.Completion.ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } result)
                {
                    TaskLocalStatus.Text = pinnedCode + " · Không xác minh được kết quả ghi hình.";
                    return;
                }
                string video = Path.Combine(result.SessionDir, VideoFinalize.Output);
                bool verified = result.Ok && (result.Finalize is null || result.Finalize.Ok) &&
                    File.Exists(video) && new FileInfo(video).Length > 0;
                if (!verified)
                {
                    TaskLocalStatus.Text = pinnedCode + " · Chưa đánh dấu: video chưa được xác minh hợp lệ.";
                    return;
                }
                if (pinnedKey.Length == 0) return;
                _localTaskMarks[pinnedKey] = "verified";
                SaveTaskMarks();
                foreach (var item in _taskEntries.Where(e => e.Key.Equals(pinnedKey, StringComparison.OrdinalIgnoreCase)))
                    item.IsRecorded = true;
                TaskLocalStatus.Text = pinnedCode + " · ✓ Video đã lưu và xác minh, tự đánh dấu quay xong.";
                RefreshTaskList();
            });
        }, TaskScheduler.Default);
    }
}
