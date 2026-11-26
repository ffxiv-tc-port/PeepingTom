using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PeepingTom.Resources;

namespace PeepingTom;

/// <summary>所有記錄過的玩家。列表只在紀錄、搜尋字或排序變了才重建（含顯示字串），每幀只畫看得到的列。</summary>
internal sealed class RecordsWindow : Window {
    private const string ClearPopupId = "##ptom-clear-records";

    private sealed class Row(TargetRecord record) {
        internal TargetRecord Record { get; } = record;
        internal string World { get; } = RecordDisplay.WorldName(record.HomeWorldId);
        internal string Count { get; } = record.Count.ToString(CultureInfo.InvariantCulture);
        internal string Last { get; } = RecordDisplay.Time(record.LastSeenUtc);
        internal string Place { get; } = RecordDisplay.PlaceName(record.LastTerritoryId);
        internal string Duration { get; } = RecordDisplay.Duration(record.TotalSeconds);
    }

    private Plugin Plugin { get; }

    private readonly List<Row> rows = new();
    private string search = "";
    private int sortByRecent;
    private int builtVersion = -1;
    private string builtSearch = "";
    private int builtSort = -1;

    internal RecordsWindow(Plugin plugin) : base(string.Format(Language.RecordsTitle, Plugin.Name) + "###ptom-records") {
        Plugin = plugin;
        Size = new Vector2(620, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // 建構時介面語言還沒套用（Plugin 建構子最後才設），打開時再取一次標題。
    public override void OnOpen() {
        WindowName = string.Format(Language.RecordsTitle, Plugin.Name) + "###ptom-records";
        base.OnOpen();
    }

    public override void Draw() {
        var store = Plugin.Watcher.Records;
        var tracker = store.Tracker;

        if (!Plugin.Config.RecordTargeters) {
            ImGui.TextDisabled(Language.RecordsDisabled);
        }

        switch (store.State) {
            case TargetRecordStore.LoadState.Loading:
                ImGui.TextDisabled(Language.RecordsLoading);
                break;
            case TargetRecordStore.LoadState.Failed:
                ImGui.TextColored(new Vector4(1f, .6f, .2f, 1f), Language.RecordsLoadFailed);
                break;
        }

        if (store.RecoveredFromCorrupt) {
            ImGui.TextDisabled(string.Format(Language.RecordsRecoveredCorrupt, TargetRecordFile.FileName + ".bad"));
        }

        var scale = ImGui.GetFontSize() / 16f;
        ImGui.SetNextItemWidth(180f * scale);
        ImGui.InputTextWithHint("##ptom-records-search", Language.RecordsSearchHint, ref search, 64);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f * scale);
        var sortLabel = sortByRecent == 1 ? Language.RecordsSortRecent : Language.RecordsSortCount;
        if (ImGui.BeginCombo("##ptom-records-sort", sortLabel)) {
            if (ImGui.Selectable(Language.RecordsSortCount, sortByRecent == 0)) {
                sortByRecent = 0;
            }

            if (ImGui.Selectable(Language.RecordsSortRecent, sortByRecent == 1)) {
                sortByRecent = 1;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var canClear = store.State == TargetRecordStore.LoadState.Ready && tracker.RecordCount > 0;
        ImGui.BeginDisabled(!canClear);
        if (ImGui.Button(Language.RecordsClear)) {
            ImGui.OpenPopup(ClearPopupId);
        }

        ImGui.EndDisabled();

        if (ImGui.BeginPopup(ClearPopupId)) {
            ImGui.TextUnformatted(Language.RecordsClearConfirm);
            if (ImGui.Button(Language.RecordsClearYes)) {
                store.Clear();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button(Language.RecordsClearNo)) {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        Rebuild(tracker);
        ImGui.TextDisabled(string.Format(Language.RecordsSummary, rows.Count, tracker.RecordCount));

        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##ptom-records-table", 6, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y))) {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Language.RecordsColumnName, ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn(Language.RecordsColumnWorld, ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(Language.RecordsColumnCount, ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Language.RecordsColumnLast, ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn(Language.RecordsColumnPlace, ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn(Language.RecordsColumnDuration, ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableHeadersRow();

        var clipper = default(ImGuiListClipper);
        clipper.Begin(rows.Count);
        try {
            while (clipper.Step()) {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++) {
                    DrawRow(rows[i]);
                }
            }
        } finally {
            clipper.End();
        }

        ImGui.EndTable();
    }

    private static void DrawRow(Row row) {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Record.Name);
        if (ImGui.IsItemHovered()) {
            RecordDisplay.DrawTooltip(row.Record);
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.World);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Count);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Last);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Place);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Duration);
    }

    private void Rebuild(TargetTracker tracker) {
        if (builtVersion == tracker.Version && builtSort == sortByRecent
            && string.Equals(builtSearch, search, StringComparison.Ordinal)) {
            return;
        }

        builtVersion = tracker.Version;
        builtSort = sortByRecent;
        builtSearch = search;

        rows.Clear();
        var needle = search.Trim();
        foreach (var record in tracker.Records) {
            if (needle.Length == 0 || record.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) {
                rows.Add(new Row(record));
            }
        }

        if (sortByRecent == 1) {
            rows.Sort(static (a, b) => b.Record.LastSeenUtc.CompareTo(a.Record.LastSeenUtc));
        } else {
            rows.Sort(static (a, b) => {
                var byCount = b.Record.Count.CompareTo(a.Record.Count);
                return byCount != 0 ? byCount : b.Record.LastSeenUtc.CompareTo(a.Record.LastSeenUtc);
            });
        }
    }
}
