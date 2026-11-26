using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using PeepingTom.Resources;

namespace PeepingTom;

/// <summary>盯人紀錄的顯示用字串。只在框架執行緒（繪製）呼叫；查不到的地點、伺服器一律顯示「?」。</summary>
internal static class RecordDisplay {
    internal const string Unknown = "?";

    private static readonly Dictionary<uint, string> PlaceNames = new();
    private static readonly Dictionary<uint, string> WorldNames = new();

    internal static string PlaceName(uint territoryId) {
        if (PlaceNames.TryGetValue(territoryId, out var cached)) {
            return cached;
        }

        var name = Unknown;
        try {
            if (Service.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var row)) {
                var text = row.PlaceName.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrEmpty(text)) {
                    name = text;
                }
            }
        } catch (Exception ex) {
            Service.Log.Warning(ex, $"Could not read the place name of territory {territoryId}");
        }

        PlaceNames[territoryId] = name;
        return name;
    }

    internal static string WorldName(uint worldId) {
        if (WorldNames.TryGetValue(worldId, out var cached)) {
            return cached;
        }

        var name = Unknown;
        try {
            if (Service.DataManager.GetExcelSheet<World>().TryGetRow(worldId, out var row)) {
                var text = row.Name.ExtractText();
                if (!string.IsNullOrEmpty(text)) {
                    name = text;
                }
            }
        } catch (Exception ex) {
            Service.Log.Warning(ex, $"Could not read the name of world {worldId}");
        }

        WorldNames[worldId] = name;
        return name;
    }

    internal static string Time(DateTime utc) {
        return utc == default ? Unknown : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    internal static string Duration(double seconds) {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return $"{(int) span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>目前清單／歷史清單上滑過名字時的說明框。</summary>
    internal static void DrawTooltip(TargetRecord record) {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(string.Format(Language.RecordsTooltipCount, record.Count));
        ImGui.TextUnformatted(string.Format(Language.RecordsTooltipFirst, Time(record.FirstSeenUtc)));
        ImGui.TextUnformatted(string.Format(Language.RecordsTooltipLast, Time(record.LastSeenUtc), PlaceName(record.LastTerritoryId)));
        ImGui.TextUnformatted(string.Format(Language.RecordsTooltipDuration, Duration(record.TotalSeconds)));
        ImGui.EndTooltip();
    }
}
