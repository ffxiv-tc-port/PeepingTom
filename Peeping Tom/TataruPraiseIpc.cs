using System;
using Dalamud.Plugin.Ipc.Exceptions;

namespace PeepingTom;

/// <summary>
/// 呼叫 TataruPraise（塔塔露誇獎）的 IPC：有新的人開始把你設成目標時，請塔塔露念一句提醒。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不加任何組件相依</b>：契約名以字串常數逐字寫在這裡，只走 Dalamud 原生的
/// <c>GetIpcSubscriber</c>。TataruPraise 沒安裝／沒載入時 <c>InvokeFunc</c> 會擲
/// <c>IpcNotReadyError</c>，這裡整個吞掉，Peeping Tom 這邊完全無感。
/// <para>
/// 📌 契約名的權威來源是 <c>TataruPraise/IpcContract.cs</c>，情境鍵的權威來源是
/// <c>TataruPraise/Core/PraiseCategory.cs</c>。CallGate 是純字串比對，對不上不會有任何
/// 錯誤訊息，只會永遠拿到「沒有人註冊」——<b>失敗形式是靜默的</b>，這幾個字串不要「順手整理」。
/// </para>
/// <para>
/// ⚠️ 每次呼叫都重新取 subscriber，不快取。TataruPraise 可以在 Peeping Tom 載入之後才被裝上／
/// 重載，快取住的 subscriber 在那之後的行為沒有保證；重取的成本只是一次字典查詢。
/// </para>
/// <para>
/// ⚠️ 節流由 TataruPraise 那邊自己做（「被盯著」是通知類情境，預設冷卻 5 秒），這裡不重複做；
/// 呼叫端只要保證「只在新的人出現那一刻叫一次」就好，不要每幀對所有 targeter 重叫。
/// </para>
/// </remarks>
internal static class TataruPraiseIpc {
    /// <summary><c>Func&lt;bool&gt;</c>：總開關開著，而且池裡真的有已合成語音的句子。</summary>
    private const string TagIsAvailable = "TataruPraise.IsAvailable";

    /// <summary>
    /// <c>Func&lt;string, bool&gt;</c>：<b>指定的那個情境</b>現在出得了聲嗎
    /// （總開關開著＋這個情境沒被關掉＋這個情境至少有一句已合成的語音）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>閘門要問的是這一個，不是 <see cref="TagIsAvailable"/>。</b>後者問的是
    /// 「整池<b>有某個情境</b>播得出來」，於是「別的情境有語音、<b>呼叫端要的那個情境</b>一句都沒有」時
    /// 照樣通過，接著 <c>Praise</c> 回 <c>false</c>——呼叫端就分不出「不能出聲」與「這次剛好沒出聲」。
    /// <para>
    /// 📌 它刻意<b>不看冷卻</b>：冷卻是「這次剛好不出聲」，不是「不能出聲」。
    /// </para>
    /// <para>
    /// 🔴 舊版 TataruPraise 沒有註冊這個端點，<c>InvokeFunc</c> 會擲 <c>IpcNotReadyError</c>，
    /// 剛好落進既有的 catch＝安靜不出聲，這是正確的 fail-safe。
    /// <b>失敗時絕不可以退回去叫 <see cref="TagIsAvailable"/></b>——那樣就把這個端點的意義整個抵銷掉了。
    /// </para>
    /// </remarks>
    private const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句來念。</summary>
    private const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 情境字串。⚠️ 這是 TataruPraise <c>pool.json</c> 的鍵；那邊查不到這個鍵時
    /// <c>Praise</c> 只會回 <c>false</c>（不出聲、不報錯，對方自己記一次 log）。
    /// </summary>
    internal const string CategoryBeingWatched = "被盯著";

    /// <summary>
    /// 請塔塔露念一句「<paramref name="category"/>」情境的句子。
    /// 對方沒裝／沒載入／正在冷卻／不想出聲都只是回 <c>false</c>，不擲例外。
    /// </summary>
    /// <remarks>
    /// 🔴 必須在主執行緒（框架更新）上呼叫 —— IPC 的實作是在呼叫端的執行緒上直接跑的。
    /// </remarks>
    internal static bool Praise(string category) {
        try {
            // 先問 IsAvailableFor(category)：TataruPraise 的總開關關著、這個情境被使用者
            // 關掉、或這個情境一句已合成的都沒有，就不要去戳 Praise。
            // 這一步同時兼作「對方在不在」的探測 —— 沒註冊就會在這裡擲例外。
            if (!Service.Interface.GetIpcSubscriber<string, bool>(TagIsAvailableFor).InvokeFunc(category)) {
                return false;
            }

            return Service.Interface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(category);
        } catch (IpcNotReadyError) {
            // 對方沒安裝／還沒載入。這是完全正常的情況，靜默。
            return false;
        } catch (Exception ex) {
            // 其他狀況（對方在自己的回呼裡爆掉之類）記一筆就好，絕不能讓它往上冒去打斷
            // TargetWatcher 的更新迴圈。Information 級：要回報的使用者跑 LogLevel 1。
            Service.Log.Information($"呼叫 TataruPraise 失敗（不影響 Peeping Tom）：{ex.Message}");
            return false;
        }
    }
}
