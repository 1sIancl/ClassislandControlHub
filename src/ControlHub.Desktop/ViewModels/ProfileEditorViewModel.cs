using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>课表网格中的一行（对应时间表的一个「上课」时间点）。</summary>
public sealed partial class PeriodRowViewModel : ViewModelBase
{
    public int Index { get; init; }
    public string Label { get; init; } = string.Empty;
    public ObservableCollection<CellViewModel> Cells { get; } = [];
}

/// <summary>课表网格中的一个单元格（星期几 × 节次）。</summary>
public sealed partial class CellViewModel : ViewModelBase
{
    public int Day { get; init; }
    public int Index { get; init; }

    [ObservableProperty]
    private string subjectId = string.Empty;

    [ObservableProperty]
    private string label = string.Empty;

    [ObservableProperty]
    private bool isEmpty = true;

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>自定义设置键值项。</summary>
public sealed partial class KeyValueEntryViewModel : ViewModelBase
{
    [ObservableProperty]
    private string key = string.Empty;

    [ObservableProperty]
    private string value = string.Empty;
}

/// <summary>档案编辑器：时间表 / 课表 / 科目 / 自定义设置，四个标签页。</summary>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    private ProfileDto _profile = new();
    private ContentBundleDto _content = new();

    public static readonly int[] Days = [1, 2, 3, 4, 5, 6, 0];
    public static readonly string[] DayNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
    public static readonly string[] TimeKinds = ["class", "break", "separator", "action"];
    public static readonly string[] TimeKindLabels = ["上课", "课间", "分割线", "行动"];

    // ── 通用 ──
    [ObservableProperty]
    private int activeTab;

    [ObservableProperty]
    private string profileName = string.Empty;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private string revision = string.Empty;

    // ── 时间表 ──
    public ObservableCollection<TimeLayoutDto> TimeLayouts { get; } = [];
    public ObservableCollection<TimeLayoutItemDto> LayoutItems { get; } = [];

    [ObservableProperty]
    private TimeLayoutDto? selectedLayout;

    // ── 课表 ──
    public ObservableCollection<PeriodRowViewModel> Periods { get; } = [];

    [ObservableProperty]
    private string selectionHint = "点击左侧网格选择一个格子，再点科目填入";

    private CellViewModel? _selectedCell;

    // ── 科目 ──
    public ObservableCollection<SubjectDto> Subjects { get; } = [];

    // ── 自定义设置 ──
    [ObservableProperty]
    private string announcement = string.Empty;

    [ObservableProperty]
    private bool lockLocalEditing;

    public ObservableCollection<KeyValueEntryViewModel> CustomValues { get; } = [];

    public ProfileEditorViewModel(AppState state, ApiClient api)
    {
        _state = state;
        _api = api;
    }

    public async Task LoadAsync(string profileId)
    {
        try
        {
            var result = await _api.GetAsync<ProfileDto>(
                _state.Connection.ServerUrl, _state.Connection.Token, $"/admin/profiles/{profileId}");
            _profile = result.Data ?? new ProfileDto();
            _content = _profile.Content ?? new ContentBundleDto();
            ProfileName = _profile.Name;
            Revision = $"内容版本 {_profile.Revision}";
            Announcement = _content.Settings.Announcement ?? string.Empty;
            LockLocalEditing = _content.Settings.LockLocalEditing;

            RebuildTimeLayouts();
            RebuildSubjects();
            RebuildCustomValues();
            SelectedLayout = _content.TimeLayouts.FirstOrDefault();
            RebuildLayoutItems();
            RebuildGrid();

            Status = $"已加载档案「{ProfileName}」（{_content.Subjects.Count} 科目 / {_content.TimeLayouts.Count} 时间表）";
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.Message;
        }
    }

    // ────────────── 时间表 ──────────────

    partial void OnSelectedLayoutChanged(TimeLayoutDto? value)
    {
        RebuildLayoutItems();
        RebuildGrid();
    }

    private void RebuildTimeLayouts()
    {
        TimeLayouts.Clear();
        foreach (var l in _content.TimeLayouts)
        {
            TimeLayouts.Add(l);
        }
    }

    private void RebuildLayoutItems()
    {
        LayoutItems.Clear();
        if (SelectedLayout is null) return;
        foreach (var item in SelectedLayout.Items)
        {
            LayoutItems.Add(item);
        }
    }

    private static string AddMinutes(string text, int minutes)
    {
        var parts = (text ?? "00:00:00").Split(':');
        var hh = int.TryParse(parts.ElementAtOrDefault(0), out var h) ? h : 0;
        var mm = int.TryParse(parts.ElementAtOrDefault(1), out var m) ? m : 0;
        var ss = int.TryParse(parts.ElementAtOrDefault(2), out var s) ? s : 0;
        var total = (hh * 3600 + mm * 60 + ss + minutes * 60) % 86400;
        return $"{total / 3600:D2}:{total % 3600 / 60:D2}:{total % 60:D2}";
    }

    [RelayCommand]
    private void AddLayout()
    {
        var layout = new TimeLayoutDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"新时间表 {_content.TimeLayouts.Count + 1}",
            Items = [],
        };
        _content.TimeLayouts.Add(layout);
        RebuildTimeLayouts();
        SelectedLayout = layout;
    }

    [RelayCommand]
    private void RemoveLayout(TimeLayoutDto? layout)
    {
        if (layout is null) return;
        _content.TimeLayouts.Remove(layout);
        RebuildTimeLayouts();
        SelectedLayout = _content.TimeLayouts.FirstOrDefault();
    }

    /// <summary>快速生成作息：第 1 节 8:00 + 每节 45 分 + 课间 10 分 + 8 节。</summary>
    [RelayCommand]
    private void GenerateSchedule()
    {
        if (SelectedLayout is null) return;
        const int period = 45;
        const int gap = 10;
        const int count = 8;
        var cursor = "08:00:00";

        var items = new List<TimeLayoutItemDto>();
        for (var i = 0; i < count; i++)
        {
            var end = AddMinutes(cursor, period);
            items.Add(new TimeLayoutItemDto { StartTime = cursor, EndTime = end, Kind = "class" });
            cursor = end;
            if (i < count - 1 && gap > 0)
            {
                var breakEnd = AddMinutes(cursor, gap);
                items.Add(new TimeLayoutItemDto { StartTime = cursor, EndTime = breakEnd, Kind = "break", BreakName = "课间休息" });
                cursor = breakEnd;
            }
        }

        SelectedLayout.Items = items;
        RebuildLayoutItems();
        RebuildGrid();
    }

    [RelayCommand]
    private void AddItem()
    {
        if (SelectedLayout is null) return;
        var last = SelectedLayout.Items.LastOrDefault();
        var start = last?.EndTime ?? "08:00:00";
        SelectedLayout.Items.Add(new TimeLayoutItemDto
        {
            StartTime = start,
            EndTime = AddMinutes(start, 45),
            Kind = "class",
        });
        RebuildLayoutItems();
        RebuildGrid();
    }

    [RelayCommand]
    private void RemoveItem(TimeLayoutItemDto? item)
    {
        if (SelectedLayout is null || item is null) return;
        SelectedLayout.Items.Remove(item);
        RebuildLayoutItems();
        RebuildGrid();
    }

    // ────────────── 课表 ──────────────

    private void RebuildGrid()
    {
        Periods.Clear();
        _selectedCell = null;
        if (SelectedLayout is null)
        {
            SelectionHint = "请先选择时间表";
            return;
        }

        var classItems = SelectedLayout.Items.Where(i => i.Kind == "class").ToList();
        for (var index = 0; index < classItems.Count; index++)
        {
            var item = classItems[index];
            var row = new PeriodRowViewModel
            {
                Index = index,
                Label = $"第 {index + 1} 节  {item.StartTime[..5]} - {item.EndTime[..5]}",
            };

            foreach (var day in Days)
            {
                var plan = FindDayPlan(SelectedLayout.Id, day);
                var slot = plan?.Slots.FirstOrDefault(s => s.Index == index);
                var subjectId = slot?.SubjectId ?? string.Empty;
                var subject = _content.Subjects.FirstOrDefault(s => s.Id == subjectId);

                row.Cells.Add(new CellViewModel
                {
                    Day = day,
                    Index = index,
                    SubjectId = subjectId,
                    Label = subject is null ? string.Empty : SubjectShort(subject),
                    IsEmpty = subject is null,
                });
            }

            Periods.Add(row);
        }

        SelectionHint = $"共 {classItems.Count} 节 × 7 天，点击格子选课，选完自动跳到下一节";
    }

    private static string SubjectShort(SubjectDto subject)
    {
        if (!string.IsNullOrEmpty(subject.Initial)) return subject.Initial;
        if (!string.IsNullOrEmpty(subject.Name)) return subject.Name[..1];
        return "未命名";
    }

    private ClassPlanDto? FindDayPlan(string layoutId, int day)
        => _content.ClassPlans.FirstOrDefault(p => p.TimeLayoutId == layoutId
            && p.DaysOfWeek.Count == 1 && p.DaysOfWeek[0] == day);

    [RelayCommand]
    private void SelectCell(CellViewModel? cell)
    {
        if (cell is null) return;
        if (_selectedCell is not null) _selectedCell.IsSelected = false;
        _selectedCell = cell;
        cell.IsSelected = true;
        SelectionHint = $"第 {cell.Index + 1} 节 · {DayNames[Array.IndexOf(Days, cell.Day)]}（当前：{(cell.IsEmpty ? "无课" : cell.Label)}）";
    }

    [RelayCommand]
    private void AssignSubject(SubjectDto? subject)
    {
        if (subject is null || _selectedCell is null) return;
        _selectedCell.SubjectId = subject.Id;
        _selectedCell.Label = SubjectShort(subject);
        _selectedCell.IsEmpty = false;
        ApplyCellToContent(_selectedCell);

        var next = Periods.FirstOrDefault(p => p.Index == _selectedCell.Index + 1);
        if (next is not null)
        {
            var nextCell = next.Cells.First(c => c.Day == _selectedCell.Day);
            SelectCell(nextCell);
        }
        else
        {
            SelectionHint = $"已设置「{subject.Name}」，本节已到最后一节";
        }
    }

    [RelayCommand]
    private void ClearCell()
    {
        if (_selectedCell is null) return;
        _selectedCell.SubjectId = string.Empty;
        _selectedCell.Label = string.Empty;
        _selectedCell.IsEmpty = true;
        ApplyCellToContent(_selectedCell);
        SelectionHint = "已清空该格";
    }

    private void ApplyCellToContent(CellViewModel cell)
    {
        if (SelectedLayout is null) return;
        var plan = FindDayPlan(SelectedLayout.Id, cell.Day);
        if (plan is null)
        {
            plan = new ClassPlanDto
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{DayNames[Array.IndexOf(Days, cell.Day)]}课表",
                TimeLayoutId = SelectedLayout.Id,
                IsEnabled = true,
                DaysOfWeek = [cell.Day],
            };
            _content.ClassPlans.Add(plan);
        }

        var slot = plan.Slots.FirstOrDefault(s => s.Index == cell.Index);
        if (slot is null)
        {
            slot = new ClassPlanSlotDto { Index = cell.Index, IsEnabled = true };
            plan.Slots.Add(slot);
        }

        slot.SubjectId = string.IsNullOrWhiteSpace(cell.SubjectId) ? null : cell.SubjectId;
    }

    // ────────────── 科目 ──────────────

    private void RebuildSubjects()
    {
        Subjects.Clear();
        foreach (var s in _content.Subjects)
        {
            Subjects.Add(s);
        }
    }

    [RelayCommand]
    private void AddSubject()
    {
        var s = new SubjectDto { Id = Guid.NewGuid().ToString(), Name = "新科目", Initial = "新" };
        _content.Subjects.Add(s);
        RebuildSubjects();
    }

    [RelayCommand]
    private void DeleteSubject(SubjectDto? subject)
    {
        if (subject is null) return;
        _content.Subjects.Remove(subject);
        foreach (var row in Periods)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.SubjectId == subject.Id)
                {
                    cell.SubjectId = string.Empty;
                    cell.Label = string.Empty;
                    cell.IsEmpty = true;
                    ApplyCellToContent(cell);
                }
            }
        }

        RebuildSubjects();
    }

    // ────────────── 自定义设置 ──────────────

    private void RebuildCustomValues()
    {
        CustomValues.Clear();
        var values = _content.Settings?.Values ?? new Dictionary<string, string>();
        foreach (var kv in values)
        {
            CustomValues.Add(new KeyValueEntryViewModel { Key = kv.Key, Value = kv.Value });
        }
    }

    [RelayCommand]
    private void AddCustomValue()
    {
        CustomValues.Add(new KeyValueEntryViewModel { Key = $"自定义.键{CustomValues.Count + 1}", Value = string.Empty });
    }

    [RelayCommand]
    private void RemoveCustomValue(KeyValueEntryViewModel? entry)
    {
        if (entry is null) return;
        CustomValues.Remove(entry);
    }

    private void FlushCustomValues()
    {
        var values = new Dictionary<string, string>();
        foreach (var kv in CustomValues)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && !values.ContainsKey(kv.Key))
            {
                values[kv.Key] = kv.Value;
            }
        }

        _content.Settings.Values = values;
    }

    // ────────────── 保存 ──────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            FlushCustomValues();
            _content.Settings.Announcement = Announcement;
            _content.Settings.LockLocalEditing = LockLocalEditing;

            var result = await _api.PostAsync<ProfileDto>(_state.Connection.ServerUrl, _state.Connection.Token,
                $"/admin/profiles/{_profile.Id}",
                new { name = ProfileName, content = _content });

            if (result.Data is not null)
            {
                Revision = $"内容版本 {result.Data.Revision}";
            }

            Status = "已保存并下发。";
        }
        catch (Exception ex)
        {
            Status = "保存失败：" + ex.Message;
        }
    }
}
