using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// 本檔只用 BCL（離線測試直接編這個檔）。TargetTracker 與 TargetRecord 只在框架執行緒碰；
// 跨執行緒只交換 TargetRecordData 快照。
namespace PeepingTom;

internal readonly record struct TargetRecordKey(string Name, uint HomeWorldId);

/// <summary>同一位玩家（名字＋原始伺服器）盯你的累計紀錄。</summary>
internal sealed class TargetRecord {
    internal TargetRecord(string name, uint homeWorldId) {
        Name = name;
        HomeWorldId = homeWorldId;
    }

    internal string Name { get; }
    internal uint HomeWorldId { get; }
    internal int Count { get; set; }
    internal DateTime FirstSeenUtc { get; set; }

    /// <summary>最近一次看到他盯著你的時間（開始盯、停止盯都會更新）。</summary>
    internal DateTime LastSeenUtc { get; set; }

    internal uint LastTerritoryId { get; set; }

    /// <summary>已結束的那幾段盯著的秒數；正在盯的那一段停止時才加進來。</summary>
    internal double TotalSeconds { get; set; }

    // 以下不存檔。
    internal int ActiveCount { get; set; }
    internal DateTime? LastEndedUtc { get; set; }

    private string? countLabel;
    private int countLabelFor = -1;

    /// <summary>名字旁的「×N」；次數沒變就沿用同一個字串（每幀都畫）。</summary>
    internal string CountLabel {
        get {
            if (countLabel == null || countLabelFor != Count) {
                countLabelFor = Count;
                countLabel = "×" + Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return countLabel;
        }
    }
}

/// <summary>
/// 依「有沒有盯著你」的變化計次：從沒盯變成盯算一次；持續盯著不重算；
/// 停止盯之後在合併門檻內又盯回來算同一次。
/// </summary>
internal sealed class TargetTracker {
    private readonly Dictionary<TargetRecordKey, TargetRecord> records = new();
    private readonly List<Active> active = new();

    private readonly record struct Active(ulong GameObjectId, TargetRecord Record, DateTime SinceUtc);

    /// <summary>任何紀錄有變動就加一；存檔與紀錄視窗靠它判斷要不要重做。</summary>
    internal int Version { get; private set; }

    internal int RecordCount => records.Count;

    internal IEnumerable<TargetRecord> Records => records.Values;

    internal bool TryGet(string name, uint homeWorldId, out TargetRecord record) {
        return records.TryGetValue(new TargetRecordKey(name, homeWorldId), out record!);
    }

    /// <summary>有人開始盯你。回傳這個人目前的累計次數；<paramref name="excluded"/>（黑名單）回 0 且不記。</summary>
    internal int Arrive(ulong gameObjectId, string name, uint homeWorldId, uint territoryId, DateTime nowUtc, TimeSpan merge, bool excluded) {
        if (excluded) {
            return 0;
        }

        foreach (var entry in active) {
            if (entry.GameObjectId == gameObjectId) {
                return entry.Record.Count;
            }
        }

        var key = new TargetRecordKey(name, homeWorldId);
        if (!records.TryGetValue(key, out var record)) {
            record = new TargetRecord(name, homeWorldId) { FirstSeenUtc = nowUtc };
            records.Add(key, record);
        }

        var continuing = record.ActiveCount > 0
                         || record.LastEndedUtc is { } ended && nowUtc - ended <= merge;
        if (!continuing) {
            record.Count++;
        }

        record.ActiveCount++;
        record.LastSeenUtc = nowUtc;
        record.LastTerritoryId = territoryId;
        active.Add(new Active(gameObjectId, record, nowUtc));
        Version++;
        return record.Count;
    }

    /// <summary>有人停止盯你；不認得的 id（沒記錄的人）不做事。</summary>
    internal void Depart(ulong gameObjectId, DateTime nowUtc) {
        for (var i = active.Count - 1; i >= 0; i--) {
            if (active[i].GameObjectId == gameObjectId) {
                Close(i, nowUtc);
                return;
            }
        }
    }

    /// <summary>登出／卸載時把正在盯的段落全部結算。</summary>
    internal void EndAll(DateTime nowUtc) {
        for (var i = active.Count - 1; i >= 0; i--) {
            Close(i, nowUtc);
        }
    }

    private void Close(int index, DateTime nowUtc) {
        var entry = active[index];
        active.RemoveAt(index);

        var record = entry.Record;
        var seconds = (nowUtc - entry.SinceUtc).TotalSeconds;
        if (seconds > 0) {
            record.TotalSeconds += seconds;
        }

        record.ActiveCount = Math.Max(0, record.ActiveCount - 1);
        record.LastEndedUtc = nowUtc;
        if (nowUtc > record.LastSeenUtc) {
            record.LastSeenUtc = nowUtc;
        }

        Version++;
    }

    /// <summary>正在盯的人也一起清掉：他們要先移開目標再盯回來才會重新計次。</summary>
    internal void Clear() {
        records.Clear();
        active.Clear();
        Version++;
    }

    /// <summary>併入讀檔結果（讀檔完成前這次登入記到的會與檔案內容相加）。</summary>
    internal void MergeLoaded(IReadOnlyList<TargetRecordData> loaded) {
        foreach (var data in loaded) {
            var key = new TargetRecordKey(data.Name, data.HomeWorldId);
            if (!records.TryGetValue(key, out var record)) {
                records.Add(key, new TargetRecord(data.Name, data.HomeWorldId) {
                    Count = data.Count,
                    FirstSeenUtc = data.FirstSeenUtc,
                    LastSeenUtc = data.LastSeenUtc,
                    LastTerritoryId = data.LastTerritoryId,
                    TotalSeconds = data.TotalSeconds,
                });
                continue;
            }

            record.Count += data.Count;
            record.TotalSeconds += data.TotalSeconds;
            if (data.FirstSeenUtc < record.FirstSeenUtc) {
                record.FirstSeenUtc = data.FirstSeenUtc;
            }

            if (data.LastSeenUtc > record.LastSeenUtc) {
                record.LastSeenUtc = data.LastSeenUtc;
                record.LastTerritoryId = data.LastTerritoryId;
            }
        }

        Version++;
    }

    /// <summary>存檔用快照；正在盯的那一段算到 <paramref name="nowUtc"/> 為止，但不改動紀錄本身。</summary>
    internal List<TargetRecordData> Snapshot(DateTime nowUtc) {
        var list = new List<TargetRecordData>(records.Count);
        foreach (var record in records.Values) {
            var seconds = record.TotalSeconds;
            foreach (var entry in active) {
                if (ReferenceEquals(entry.Record, record) && nowUtc > entry.SinceUtc) {
                    seconds += (nowUtc - entry.SinceUtc).TotalSeconds;
                }
            }

            list.Add(new TargetRecordData {
                Name = record.Name,
                HomeWorldId = record.HomeWorldId,
                Count = record.Count,
                FirstSeenUtc = record.FirstSeenUtc,
                LastSeenUtc = record.LastSeenUtc,
                LastTerritoryId = record.LastTerritoryId,
                TotalSeconds = seconds,
            });
        }

        return list;
    }
}

internal sealed class TargetRecordData {
    public string Name { get; set; } = "";
    public uint HomeWorldId { get; set; }
    public int Count { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public uint LastTerritoryId { get; set; }
    public double TotalSeconds { get; set; }
}

internal sealed class TargetRecordFileData {
    public int Version { get; set; } = 1;
    public List<TargetRecordData>? Records { get; set; }
}

internal static class TargetRecordFile {
    internal const string FileName = "target-records.json";

    internal static byte[] Serialize(List<TargetRecordData> records) {
        return JsonSerializer.SerializeToUtf8Bytes(new TargetRecordFileData { Records = records });
    }

    /// <summary>解析失敗回 false（不擲）；個別壞掉的項目略過，數值越界的修正回合法範圍。</summary>
    internal static bool TryParse(byte[] bytes, out List<TargetRecordData> records) {
        records = new List<TargetRecordData>();
        TargetRecordFileData? file;
        try {
            file = JsonSerializer.Deserialize<TargetRecordFileData>(bytes);
        } catch (JsonException) {
            return false;
        } catch (NotSupportedException) {
            return false;
        }

        if (file?.Records == null) {
            return false;
        }

        var seen = new HashSet<TargetRecordKey>();
        foreach (var data in file.Records) {
            if (data == null || string.IsNullOrEmpty(data.Name) || !seen.Add(new TargetRecordKey(data.Name, data.HomeWorldId))) {
                continue;
            }

            data.Count = Math.Max(0, data.Count);
            if (double.IsNaN(data.TotalSeconds) || double.IsInfinity(data.TotalSeconds) || data.TotalSeconds < 0) {
                data.TotalSeconds = 0;
            }

            data.FirstSeenUtc = AsUtc(data.FirstSeenUtc);
            data.LastSeenUtc = AsUtc(data.LastSeenUtc);
            records.Add(data);
        }

        return true;
    }

    private static DateTime AsUtc(DateTime value) {
        return value.Kind switch {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    /// <summary>先寫暫存檔再換名，寫到一半中斷不會留下半份正式檔。</summary>
    internal static void WriteAtomic(string path, byte[] bytes) {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }
}
