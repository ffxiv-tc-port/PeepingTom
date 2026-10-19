using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using PeepingTom.Ipc;

namespace PeepingTom;

/// <summary>
/// 查玩家在不在遊戲的黑名單裡。除了 <see cref="State"/> 以外只能在框架執行緒呼叫。
/// </summary>
/// <remarks>
/// 不跨幀保存原生指標：黑名單每次查詢都重取，玩家只記 AccountId／ContentId 數值（給歷史記錄複查）。
/// 讀不到黑名單（尚未載入、找不到函式、擲例外）一律當成「不在黑名單」＝照舊顯示。
/// </remarks>
internal sealed class BlacklistFilter {
    internal enum ReadState {
        NotChecked = 0,
        Readable = 1,
        Unreadable = 2,
    }

    private readonly record struct Ids(ulong AccountId, ulong ContentId);

    private readonly Dictionary<ulong, Ids> idsByObjectId = new();
    private readonly List<ulong> staleIds = new();
    private bool broken;
    private volatile ReadState state;

    /// <summary>設定視窗讀；只用來提示「目前讀不到黑名單」。</summary>
    internal ReadState State => state;

    /// <summary>只能拿當幀從 ObjectTable 取得的物件呼叫；順手記下它的 AccountId／ContentId。</summary>
    internal unsafe bool IsBlocked(IPlayerCharacter actor) {
        var chara = (Character*)actor.Address;
        if (chara == null) {
            return false;
        }

        var ids = new Ids(chara->AccountId, chara->ContentId);
        idsByObjectId[actor.GameObjectId] = ids;
        return Query(ids);
    }

    /// <summary>歷史記錄用：只認得先前經過 <see cref="IsBlocked(IPlayerCharacter)"/> 記下的人。</summary>
    internal bool IsBlocked(Targeter targeter) {
        return idsByObjectId.TryGetValue(targeter.GameObjectId, out var ids) && Query(ids);
    }

    internal void Clear() {
        idsByObjectId.Clear();
    }

    /// <summary>丟掉已經不在目前清單、也不在歷史記錄裡的人。</summary>
    internal void Prune(IReadOnlyList<Targeter> current, IReadOnlyList<Targeter> previous) {
        if (idsByObjectId.Count <= current.Count + previous.Count) {
            return;
        }

        staleIds.Clear();
        foreach (var id in idsByObjectId.Keys) {
            if (!Contains(current, id) && !Contains(previous, id)) {
                staleIds.Add(id);
            }
        }

        foreach (var id in staleIds) {
            idsByObjectId.Remove(id);
        }
    }

    private static bool Contains(IReadOnlyList<Targeter> list, ulong gameObjectId) {
        for (var i = 0; i < list.Count; i++) {
            if (list[i].GameObjectId == gameObjectId) {
                return true;
            }
        }

        return false;
    }

    private unsafe bool Query(Ids ids) {
        if (broken || ids.AccountId == 0 && ids.ContentId == 0) {
            return false;
        }

        try {
            if (InfoModule.MemberFunctionPointers.GetInfoProxyById is null
                || InfoProxyBlacklist.MemberFunctionPointers.GetBlockResultType is null) {
                Break(null);
                return false;
            }

            var blacklist = InfoProxyBlacklist.Instance();
            if (blacklist == null) {
                SetState(ReadState.Unreadable);
                return false;
            }

            // 遊戲還沒載入黑名單時這支函式直接回 0（不在列舉裡），不是 NotBlocked。
            var result = blacklist->GetBlockResultType(ids.AccountId, ids.ContentId);
            if (result is not (InfoProxyBlacklist.BlockResultType.NotBlocked
                or InfoProxyBlacklist.BlockResultType.BlockedByAccountId
                or InfoProxyBlacklist.BlockResultType.BlockedByContentId)) {
                SetState(ReadState.Unreadable);
                return false;
            }

            SetState(ReadState.Readable);
            return result != InfoProxyBlacklist.BlockResultType.NotBlocked;
        } catch (Exception ex) {
            Break(ex);
            return false;
        }
    }

    private void SetState(ReadState next) {
        var previous = state;
        if (previous == next) {
            return;
        }

        state = next;
        if (next == ReadState.Unreadable) {
            Service.Log.Information("讀不到遊戲的黑名單（尚未載入），暫時不隱藏黑名單玩家");
        } else if (previous == ReadState.Unreadable) {
            Service.Log.Information("已讀到遊戲的黑名單，開始隱藏黑名單玩家");
        }
    }

    private void Break(Exception? ex) {
        broken = true;
        state = ReadState.Unreadable;
        Service.Log.Warning(ex, "讀取遊戲的黑名單失敗，重新載入插件前不再隱藏黑名單玩家");
    }
}
