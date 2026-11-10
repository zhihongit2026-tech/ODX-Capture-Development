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
    public string Key => Scope + "|" + Code;
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
                // Never leave tasks from a previous Tbrain page/project visible.
                _taskRebinding = true;
                try
                {
                    _taskEntries.Clear();
                    _taskScope = "";
                    _taskStem = "";
                    RefreshTaskList();
                    if (_run is null && _state == RunState.Idle)
                    {
                        _selectedTaskCode = "";
                        _selectedTaskPart = "";
                        _selectedTaskScope = "";
                        TaskManualBox.Text = "";
                        TaskBox.Text = "";
                        UpdateSelectedTaskLabel();
                    }
                }
                finally { _taskRebinding = false; }
                TaskSyncStatus.Text = "Trang Tbrain hiện tại chưa có danh sách Task. Mở dự án → tab Tasks rồi bấm Đồng bộ.";
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
        }).Where(x => x.Match.Success &&
            int.TryParse(x.Match.Groups["index"].Value, out int n) && n is > 0 and <= 999).ToList();
        if (parsed.Count == 0)
        {
            TaskSyncStatus.Text = "Không tìm thấy Task ID hợp lệ trên trang Tbrain hiện tại.";
            return;
        }

        // The list must contain only IDs actually returned by the currently open Tbrain project.
        // Do not synthesize missing Sxx IDs between the smallest and largest values.
        var stemGroup = parsed.GroupBy(x => x.Match.Groups["stem"].Value.ToUpperInvariant())
                              .OrderByDescending(g => g.Count()).First();
        string stem = stemGroup.Key;
        var tasks = stemGroup
            .GroupBy(x => x.Item.Id.Trim().ToUpperInvariant())
            .Select(g => g.First().Item)
            .OrderBy(item => int.Parse(TaskCodePattern.Match(item.Id).Groups["index"].Value))
            .ToList();

        string scope = (snapshot.Path + "|" + snapshot.Project + "|" + stem).ToUpperInvariant();
        _taskScope = scope;
        _taskStem = stem;
        var rows = tasks.Select(web => new TaskBoardEntry
        {
            Code = web.Id.Trim().ToUpperInvariant(),
            Part = web.Part.Trim().ToUpperInvariant(),
            Scope = scope,
            OnTbrain = true,
            RemoteStatus = web.Status.Trim(),
            IsRecorded = Marked(scope, web.Id.Trim().ToUpperInvariant(), web.Part.Trim().ToUpperInvariant())
        }).ToList();

        _taskRebinding = true;
        try
        {
            _taskEntries.Clear();
            foreach (var row in rows) _taskEntries.Add(row);
            RefreshTaskList();

            var selected = rows.FirstOrDefault(row =>
                row.Code.Equals(_selectedTaskCode, StringComparison.OrdinalIgnoreCase) &&
                !row.IsRecorded);
            if (selected is not null)
                TaskListBox.SelectedItem = TaskListBox.Items.Cast<TaskBoardEntry>()
                    .FirstOrDefault(item => item.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
            else if (_run is null && rows.Any(row =>
                         row.Code.Equals(_selectedTaskCode, StringComparison.OrdinalIgnoreCase) && row.IsRecorded))
            {
                _selectedTaskCode = "";
                _selectedTaskPart = "";
                _selectedTaskScope = "";
                TaskManualBox.Text = "";
                TaskBox.Text = "";
            }
        }
        finally { _taskRebinding = false; }

        int done = rows.Count(row => row.IsRecorded);
        TaskSyncStatus.Text = $"{snapshot.Project} · {rows.Count} Task đã đồng bộ · {rows.Count - done} chưa quay · {done} đã quay";
        UpdateSelectedTaskLabel();
    }

    private bool Marked(string scope, string code, string part) =>
        _localTaskMarks.ContainsKey(scope + "|" + code) ||
        _localTaskMarks.ContainsKey(scope + "|" + code + "|" + part) ||
        _localTaskMarks.Keys.Any(key => key.StartsWith(scope + "|" + code + "|", StringComparison.OrdinalIgnoreCase));

    private void RefreshTaskList()
    {
        string query = TaskSearchBox.Text.Trim().ToUpperInvariant();
        string mode = (TaskFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "pending";
        var visible = _taskEntries.Where(row =>
            (query.Length == 0 || row.DisplayId.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (mode == "done" ? row.IsRecorded : !row.IsRecorded)).ToList();
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
        if (!TaskCodePattern.IsMatch(code))
        {
            TaskLocalStatus.Text = "Task ID không hợp lệ. Ví dụ đúng: AMRF-124-S15.";
            return;
        }
        var entry = _taskEntries.FirstOrDefault(t =>
            t.Code.Equals(code, StringComparison.OrdinalIgnoreCase) &&
            t.Scope.Equals(_taskScope, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            TaskLocalStatus.Text = "Task ID này chưa có trong danh sách đã đồng bộ từ dự án Tbrain đang mở.";
            return;
        }
        if (entry.IsRecorded)
        {
            TaskLocalStatus.Text = "Task này đã đánh dấu hoàn thành. Chuyển sang bộ lọc Đã quay và bỏ tích nếu muốn quay lại.";
            return;
        }
        _taskManualEditing = true;
        try { ChooseTask(entry); }
        finally { _taskManualEditing = false; }
        RefreshTaskList();
        TaskListBox.SelectedItem = TaskListBox.Items.Cast<TaskBoardEntry>()
            .FirstOrDefault(x => x.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));
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
        if (_run is not null)
        {
            TaskLocalStatus.Text = "Hãy kết thúc phiên quay trước khi đánh dấu Task hoàn thành.";
            return;
        }

        entry.IsRecorded = !entry.IsRecorded;
        if (entry.IsRecorded)
        {
            _localTaskMarks[entry.Key] = "manual";
            // Remove legacy per-part keys so a task ID has one completion state.
            foreach (string legacy in _localTaskMarks.Keys
                         .Where(key => key.StartsWith(entry.Key + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                _localTaskMarks.Remove(legacy);
        }
        else
        {
            _localTaskMarks.Remove(entry.Key);
            foreach (string legacy in _localTaskMarks.Keys
                         .Where(key => key.StartsWith(entry.Key + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                _localTaskMarks.Remove(legacy);
        }
        SaveTaskMarks();

        if (entry.IsRecorded && _selectedTaskCode.Equals(entry.Code, StringComparison.OrdinalIgnoreCase))
        {
            _selectedTaskCode = "";
            _selectedTaskPart = "";
            _selectedTaskScope = "";
            TaskManualBox.Text = "";
            if (_agent?.IsHost == true && _agent.Room?.CurrentSession is null)
                TaskBox.Text = "";
            UpdateSelectedTaskLabel();
        }

        TaskLocalStatus.Text = entry.DisplayId + (entry.IsRecorded
            ? " · Đã đánh dấu hoàn thành; Task được ẩn khỏi danh sách Chưa quay."
            : " · Đã bỏ dấu tích; Task trở lại danh sách Chưa quay. Video cũ không bị xóa.");
        RefreshTaskList();
        TaskSyncStatus.Text = $"{_taskScope.Split('|').ElementAtOrDefault(1)} · {_taskEntries.Count} Task đã đồng bộ · {_taskEntries.Count(row => !row.IsRecorded)} chưa quay · {_taskEntries.Count(row => row.IsRecorded)} đã quay";
    }

    private void UpdateTaskRecordingDisplay()
    {
        if (_run is { } active && !ReferenceEquals(_observedCapture, active))
            ObserveCapture(active);
        if (_run is null)
        {
            TaskElapsedText.Text = "00:00:00";
            TaskRecordingHint.Text = "Sẵn sàng · chọn Task ID rồi bắt đầu ghi bằng nút hoặc F10.";
            return;
        }
        if (_state == RunState.Starting || _runStarted == default)
        {
            TaskElapsedText.Text = "00:00:00";
            TaskRecordingHint.Text = "Đang khởi tạo ghi hình · Task đã được giữ cố định cho phiên này.";
            return;
        }
        var elapsed = DateTime.Now - _runStarted;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        TaskElapsedText.Text = elapsed.ToString(@"hh\:mm\:ss");
        TaskRecordingHint.Text = _pinnedTaskCode.Length > 0
            ? "Đang ghi: " + _pinnedTaskCode + " · đổi lựa chọn không ảnh hưởng video hiện tại"
            : "Đang ghi · chưa chọn Task ID để đối chiếu.";
    }

    private void ObserveCapture(ICaptureRun run, string? taskCode = null, string? taskScope = null, string? taskPart = null)
    {
        if (ReferenceEquals(_observedCapture, run)) return;
        _observedCapture = run;

        string code = (taskCode ?? _selectedTaskCode).Trim().ToUpperInvariant();
        if (!TaskCodePattern.IsMatch(code))
        {
            string folder = Path.GetFileName(run.SessionDir);
            code = TaskCodePattern.IsMatch(folder) ? folder : "";
        }
        string stem = code.Length > 0 ? TaskCodePattern.Match(code).Groups["stem"].Value.ToUpperInvariant() : "";
        string scope = taskScope ?? (_selectedTaskCode.Equals(code, StringComparison.OrdinalIgnoreCase)
            ? _selectedTaskScope
            : (_taskStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ? _taskScope : ""));
        string part = taskPart ?? (_selectedTaskCode.Equals(code, StringComparison.OrdinalIgnoreCase) ? _selectedTaskPart : "");
        _pinnedTaskCode = code;
        _pinnedTaskKey = code.Length == 0 ? "" : (scope.Length > 0 ? scope : "MANUAL|" + stem) + "|" + code;
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
                    TaskLocalStatus.Text = pinnedCode + " · Video chưa được xác minh hợp lệ; Task vẫn ở danh sách Chưa quay.";
                    return;
                }
                if (pinnedKey.Length == 0) return;
                // Completion is deliberately manual: user checks the box after reviewing the saved video.
                TaskLocalStatus.Text = pinnedCode + " · Video đã lưu và xác minh. Hãy tích ô Task để ẩn khỏi danh sách Chưa quay.";
                TaskRecordingHint.Text = "Đã lưu video cho " + pinnedCode + " · kiểm tra video rồi tích Task để hoàn thành.";
            });
        }, TaskScheduler.Default);
    }

    private CaptureOptions ApplySelectedTaskFolder(CaptureOptions options)
    {
        string taskId = _selectedTaskCode.Trim().ToUpperInvariant();
        if (!TaskCodePattern.IsMatch(taskId)) return options;

        string folder = Path.Combine(options.OutputRoot, taskId);
        if (Directory.Exists(folder))
        {
            string archiveRoot = Path.Combine(options.OutputRoot, "_previous_takes");
            Directory.CreateDirectory(archiveRoot);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            string archive = Path.Combine(archiveRoot, taskId + "__" + stamp);
            int suffix = 1;
            while (Directory.Exists(archive))
                archive = Path.Combine(archiveRoot, taskId + "__" + stamp + "-" + suffix++.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Move the previous take out of the active Task folder before recording.
            // If the move fails, abort instead of risking an overwrite.
            Directory.Move(folder, archive);
            TaskLocalStatus.Text = "Đã bảo toàn video cũ tại captures\\_previous_takes\\" + Path.GetFileName(archive);
        }
        return options with { DirName = taskId };
    }

}
