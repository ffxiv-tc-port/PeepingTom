using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PeepingTom;

/// <summary>
/// 盯人紀錄的讀寫。<see cref="Tracker"/> 與 <see cref="Tick"/>／<see cref="Clear"/>／<see cref="Dispose"/> 只在框架執行緒用；
/// 讀檔與一般存檔在執行緒池，兩邊只交換 <see cref="TargetRecordData"/> 快照。
/// 讀檔完成（併入記憶體）之前不存檔，免得用不完整的資料蓋掉舊檔。
/// </summary>
internal sealed class TargetRecordStore : IDisposable {
    internal enum LoadState {
        Loading = 0,
        Ready = 1,
        Failed = 2,
    }

    private sealed class LoadResult(List<TargetRecordData>? records, bool recoveredFromCorrupt) {
        internal List<TargetRecordData>? Records { get; } = records;
        internal bool RecoveredFromCorrupt { get; } = recoveredFromCorrupt;
    }

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);

    private readonly string path;
    private readonly Stopwatch sinceSave = Stopwatch.StartNew();
    private LoadResult? loadResult;
    private int savedVersion;
    private int saving;
    private int saveFailureLogged;
    private Task? saveTask;

    internal TargetTracker Tracker { get; } = new();
    internal LoadState State { get; private set; } = LoadState.Loading;

    /// <summary>讀檔時發現紀錄檔損毀，已改名保留並從空白開始。</summary>
    internal bool RecoveredFromCorrupt { get; private set; }

    internal TargetRecordStore(string directory) {
        path = Path.Combine(directory, TargetRecordFile.FileName);
        Task.Run(Load);
    }

    private void Load() {
        LoadResult result;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) {
                result = new LoadResult([], false);
            } else if (TargetRecordFile.TryParse(File.ReadAllBytes(path), out var records)) {
                result = new LoadResult(records, false);
            } else {
                File.Move(path, path + ".bad", true);
                Service.Log.Warning($"盯人紀錄檔損毀，已改名為 {TargetRecordFile.FileName}.bad 保留，重新開始記錄");
                result = new LoadResult([], true);
            }
        } catch (Exception ex) {
            Service.Log.Warning(ex, "讀不到盯人紀錄檔，這次不會存檔（避免蓋掉舊紀錄）");
            result = new LoadResult(null, false);
        }

        Volatile.Write(ref loadResult, result);
    }

    /// <summary>每幀呼叫：讀檔完成就併入；有變動且距上次存檔滿 5 秒就在背景存檔。</summary>
    internal void Tick() {
        if (State == LoadState.Loading) {
            var result = Interlocked.Exchange(ref loadResult, null);
            if (result == null) {
                return;
            }

            if (result.Records == null) {
                State = LoadState.Failed;
                return;
            }

            Tracker.MergeLoaded(result.Records);
            RecoveredFromCorrupt = result.RecoveredFromCorrupt;
            State = LoadState.Ready;
            savedVersion = result.RecoveredFromCorrupt ? -1 : Tracker.Version;
            Service.Log.Information($"盯人紀錄：讀入 {result.Records.Count} 位玩家");
            return;
        }

        if (State != LoadState.Ready || Tracker.Version == savedVersion || sinceSave.Elapsed < SaveInterval
            || Volatile.Read(ref saving) != 0) {
            return;
        }

        var records = Snapshot(out var version);
        Volatile.Write(ref saving, 1);
        saveTask = Task.Run(() => {
            try {
                Write(records);
            } finally {
                Volatile.Write(ref saving, 0);
            }
        });
        savedVersion = version;
        sinceSave.Restart();
    }

    private List<TargetRecordData> Snapshot(out int version) {
        version = Tracker.Version;
        return Tracker.Snapshot(DateTime.UtcNow);
    }

    private void Write(List<TargetRecordData> records) {
        try {
            TargetRecordFile.WriteAtomic(path, TargetRecordFile.Serialize(records));
        } catch (Exception ex) {
            if (Interlocked.Exchange(ref saveFailureLogged, 1) == 0) {
                Service.Log.Warning(ex, "盯人紀錄存檔失敗，之後會再試");
            }
        }
    }

    /// <summary>清除全部紀錄；讀檔完成前不能清（否則讀回來的舊紀錄會再併進來）。</summary>
    internal bool Clear() {
        if (State != LoadState.Ready) {
            return false;
        }

        Tracker.Clear();
        RecoveredFromCorrupt = false;
        Service.Log.Information("盯人紀錄：已清除");
        return true;
    }

    internal void EndAll() {
        Tracker.EndAll(DateTime.UtcNow);
    }

    /// <summary>結算正在盯的段落並同步寫最後一次；背景存檔還沒寫完最多等 1 秒。</summary>
    public void Dispose() {
        Tracker.EndAll(DateTime.UtcNow);
        if (State != LoadState.Ready || Tracker.Version == savedVersion) {
            return;
        }

        try {
            if (saveTask is { IsCompleted: false } pending && !pending.Wait(TimeSpan.FromSeconds(1))) {
                Service.Log.Warning("盯人紀錄：上一次存檔還沒寫完，略過關閉時的存檔");
                return;
            }
        } catch (Exception) {
            // 背景存檔自己記過失敗；這裡照樣寫最後一次。
        }

        Write(Snapshot(out _));
    }
}
