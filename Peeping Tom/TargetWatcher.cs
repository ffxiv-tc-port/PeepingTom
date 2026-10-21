using NAudio.Wave;
using Resourcer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using PeepingTom.Ipc;
using PeepingTom.Resources;

namespace PeepingTom {
    internal class TargetWatcher : IDisposable {
        private Plugin Plugin { get; }

        private Stopwatch UpdateWatch { get; } = new();
        private Stopwatch? SoundWatch { get; set; }
        private int LastTargetAmount { get; set; }

        private Targeter[] Current { get; set; } = [];

        public IReadOnlyCollection<Targeter> CurrentTargeters => Current;

        // 與 CurrentTargeters 同一份快照；每幀都跑的呼叫端用它走訪，不經介面列舉器。
        internal ReadOnlySpan<Targeter> CurrentSpan => Current;

        private List<Targeter> Previous { get; } = [];

        public IReadOnlyCollection<Targeter> PreviousTargeters => Previous;

        // 只在框架執行緒用。只存 id、名字的原始位元組與解析結果，不存物件表的包裝或位址。
        private List<Targeter> TargetingScratch { get; } = [];
        private List<CachedName> NameCache { get; } = [];

        private BlacklistFilter Blacklist { get; } = new();
        private Stopwatch BlacklistHistoryWatch { get; } = Stopwatch.StartNew();

        internal BlacklistFilter.ReadState BlacklistState => Blacklist.State;

        public TargetWatcher(Plugin plugin) {
            Plugin = plugin;
            UpdateWatch.Start();

            Service.Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose() {
            Service.Framework.Update -= OnFrameworkUpdate;
        }

        public void ClearPrevious() {
            Previous.Clear();
            Blacklist.Clear();
        }

        private void OnFrameworkUpdate(IFramework framework1) {
            if (Plugin.InPvp) {
                return;
            }

            if (UpdateWatch.Elapsed > TimeSpan.FromMilliseconds(Plugin.Config.PollFrequency)) {
                Update();
            }
        }

        private void Update() {
            var player = Service.ObjectTable.LocalPlayer;
            if (player == null) {
                return;
            }

            // get targeters and set a copy so we can release the mutex faster
            var newCurrent = GetTargeting(Service.ObjectTable, player);

            var logToChat = CanLogToChat();

            // 這一輪有沒有「新出現的 targeter」。判定沿用下面 foreach 的同一個條件
            // （newCurrent 裡有、Current 裡沒有的 GameObjectId），也就跟著沿用了
            // GetTargeting 的既有過濾（隊伍／團隊／戰鬥中／自己／黑名單）與 PvP 早退。
            var anyNewTargeter = false;

            foreach (var newTargeter in newCurrent) {
                if (ContainsId(Current, newTargeter.GameObjectId)) {
                    continue;
                }

                anyNewTargeter = true;

                try {
                    Plugin.IpcManager.SendNewTargeter(newTargeter);
                } catch (Exception ex) {
                    Service.Log.Error(ex, "Failed to send IPC message");
                }

                if (logToChat) {
                    try {
                        LogTargeterToChat(newTargeter);
                    } catch (Exception ex) {
                        Service.Log.Error(ex, "Failed to log targeter to chat");
                    }
                }
            }

            // 請塔塔露念一句提醒。⚠️ 一輪只叫一次：同一輪同時冒出好幾個人也只是一句
            // （多叫的那幾次反正會被對方的情境冷卻擋掉，但不要白跑 IPC）。
            // 節流交給 TataruPraise 自己（「被盯著」是通知類，預設冷卻 5 秒）。
            if (anyNewTargeter && Plugin.Config.TataruPraiseOnTarget) {
                TataruPraiseIpc.Praise(TataruPraiseIpc.CategoryBeingWatched);
            }

            foreach (var stopped in Current) {
                if (ContainsId(newCurrent, stopped.GameObjectId)) {
                    continue;
                }

                try {
                    Plugin.IpcManager.SendStoppedTargeting(stopped);
                } catch (Exception ex) {
                    Service.Log.Error(ex, "Failed to send IPC message");
                }
            }

            Current = newCurrent;

            HandleHistory(Current);

            if (Plugin.Config.HideBlacklistedPlayers && BlacklistHistoryWatch.Elapsed >= TimeSpan.FromSeconds(1)) {
                BlacklistHistoryWatch.Restart();
                RemoveBlacklistedFromHistory();
            }

            // play sound if necessary
            if (CanPlaySound()) {
                SoundWatch?.Restart();
                PlaySound();
            }

            LastTargetAmount = Current.Length;
        }

        private void HandleHistory(Targeter[] targeting) {
            if (!Plugin.Config.KeepHistory || !Plugin.Config.HistoryWhenClosed && !Plugin.Ui.MainWindow.IsVisible) {
                return;
            }

            foreach (var targeter in targeting) {
                // add the targeter to the previous list
                for (var i = Previous.Count - 1; i >= 0; i--) {
                    if (Previous[i].GameObjectId == targeter.GameObjectId) {
                        Previous.RemoveAt(i);
                    }
                }

                Previous.Insert(0, targeter);
            }

            // only keep the configured number of previous targeters (ignoring ones that are currently targeting)
            while (CountNotTargeting(targeting) > Plugin.Config.NumHistory) {
                Previous.RemoveAt(Previous.Count - 1);
            }
        }

        private int CountNotTargeting(Targeter[] targeting) {
            var count = 0;
            foreach (var old in Previous) {
                if (!ContainsId(targeting, old.GameObjectId)) {
                    count++;
                }
            }

            return count;
        }

        private static bool ContainsId(Targeter[] targeters, ulong gameObjectId) {
            foreach (var targeter in targeters) {
                if (targeter.GameObjectId == gameObjectId) {
                    return true;
                }
            }

            return false;
        }

        // 每一幀都跑：條件的先後與 Blacklist.IsBlocked 被呼叫的時機（它會記下 id）不可以動。
        private Targeter[] GetTargeting(IEnumerable<IGameObject> objects, IGameObject player) {
            var found = TargetingScratch;
            found.Clear();

            var playerId = player.GameObjectId;

            foreach (var obj in objects) {
                if (obj is not IPlayerCharacter actor) {
                    continue;
                }

                if (obj.ObjectIndex == 0) {
                    // 自己那一格反正會被下面「記錄自己」那一關擋掉時，不去問目標系統（每問一次配置一個包裝）。
                    if (!Plugin.Config.LogSelf && actor.GameObjectId == playerId) {
                        continue;
                    }

                    if ((Service.TargetManager.SoftTarget ?? Service.TargetManager.Target)?.GameObjectId != playerId) {
                        continue;
                    }
                } else if (obj.TargetObjectId != playerId) {
                    continue;
                }

                // .Where(obj => Marshal.ReadByte(obj.Address + ActorOffsets.PlayerCharacterTargetActorId + 4) == 0)
                if (!Plugin.Config.LogParty && InParty(actor)) {
                    continue;
                }

                if (!Plugin.Config.LogAlliance && InAlliance(actor)) {
                    continue;
                }

                if (!Plugin.Config.LogInCombat && InCombat(actor)) {
                    continue;
                }

                if (!Plugin.Config.LogSelf && actor.GameObjectId == playerId) {
                    continue;
                }

                if (Plugin.Config.HideBlacklistedPlayers && Blacklist.IsBlocked(actor)) {
                    continue;
                }

                found.Add(new Targeter(ReadName(actor), actor.HomeWorld.RowId, actor.EntityId, actor.GameObjectId, DateTime.UtcNow));
            }

            for (var i = NameCache.Count - 1; i >= 0; i--) {
                if (NameCache[i].Seen) {
                    NameCache[i].Seen = false;
                } else {
                    NameCache.RemoveAt(i);
                }
            }

            return found.Count == 0 ? [] : found.ToArray();
        }

        // 回傳值與 actor.Name 相同：名字是那段原始位元組的函式，位元組沒變就沿用上一幀解析好的那一份。
        private unsafe SeString ReadName(IPlayerCharacter actor) {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*) actor.Address;
            if (native == null) {
                return actor.Name;
            }

            ReadOnlySpan<byte> raw = native->Name;
            var end = raw.IndexOf((byte) 0);
            if (end >= 0) {
                raw = raw[..end];
            }

            var id = actor.GameObjectId;
            CachedName? entry = null;
            foreach (var cached in NameCache) {
                if (cached.GameObjectId == id) {
                    entry = cached;
                    break;
                }
            }

            if (entry == null) {
                entry = new CachedName(id);
                NameCache.Add(entry);
            } else if (raw.SequenceEqual(entry.Raw)) {
                entry.Seen = true;
                return entry.Name;
            }

            entry.Raw = raw.ToArray();
            entry.Name = actor.Name;
            entry.Seen = true;
            return entry.Name;
        }

        private sealed class CachedName(ulong gameObjectId) {
            internal ulong GameObjectId { get; } = gameObjectId;
            internal byte[] Raw { get; set; } = [];
            internal SeString Name { get; set; } = null!;
            internal bool Seen { get; set; }
        }

        // 已在歷史記錄裡、之後才被加進黑名單的人。拿掉就不會回來，開關關掉也一樣（同其他篩選）。
        private void RemoveBlacklistedFromHistory() {
            for (var i = Previous.Count - 1; i >= 0; i--) {
                if (Blacklist.IsBlocked(Previous[i])) {
                    Previous.RemoveAt(i);
                }
            }

            Blacklist.Prune(Current, Previous);
        }

        private static bool InCombat(IGameObject actor) => actor is IPlayerCharacter pc && pc.StatusFlags.HasFlag(StatusFlags.InCombat);

        private static bool InParty(IGameObject actor) => actor is IPlayerCharacter pc && pc.StatusFlags.HasFlag(StatusFlags.PartyMember);

        private static bool InAlliance(IGameObject actor) => actor is IPlayerCharacter pc && pc.StatusFlags.HasFlag(StatusFlags.AllianceMember);

        private bool CanPlaySound() {
            if (!Plugin.Config.PlaySoundOnTarget) {
                return false;
            }

            if (Current.Length <= LastTargetAmount) {
                return false;
            }

            if (!Plugin.Config.PlaySoundWhenClosed && !Plugin.Ui.MainWindow.IsVisible) {
                return false;
            }

            if (SoundWatch == null) {
                SoundWatch = new Stopwatch();
                return true;
            }

            var secs = SoundWatch.Elapsed.TotalSeconds;
            return secs >= Plugin.Config.SoundCooldown;
        }

        private bool CanLogToChat() {
            if (!Plugin.Config.LogToChat) {
                return false;
            }

            return Plugin.Config.LogToChatWhenClosed || Plugin.Ui.MainWindow.IsVisible;
        }

        private void LogTargeterToChat(Targeter targeter) {
            // the name has to stay a PlayerPayload so it remains clickable, so the localised
            // sentence is split around its {0} placeholder rather than run through string.Format
            var format = Language.ChatLogTargeting;
            var index = format.IndexOf("{0}", StringComparison.Ordinal);

            var payloads = new List<Payload> {
                new TextPayload($"[{Plugin.Name}] "),
            };

            if (index < 0) {
                payloads.Add(new PlayerPayload(targeter.Name.TextValue, targeter.HomeWorldId));
                payloads.Add(new TextPayload(" " + format));
            } else {
                var before = format[..index];
                var after = format[(index + 3)..];

                if (before.Length > 0) {
                    payloads.Add(new TextPayload(before));
                }

                payloads.Add(new PlayerPayload(targeter.Name.TextValue, targeter.HomeWorldId));

                if (after.Length > 0) {
                    payloads.Add(new TextPayload(after));
                }
            }

            Service.ChatGui.Print(new XivChatEntry {
                Message = new SeString(payloads),
            });
        }

        private void PlaySound() {
            var soundDevice = DirectSoundOut.Devices.FirstOrDefault(d => d.Guid == Plugin.Config.SoundDeviceNew);
            if (soundDevice == null) {
                return;
            }

            var device = soundDevice.Guid;
            new Thread(() => {
                // 執行緒入口冒出的未處理例外會讓整個遊戲結束。
                try {
                    PlaySoundOn(device);
                } catch (Exception ex) {
                    if (Interlocked.Exchange(ref this._soundThreadFailureLogged, 1) == 0) {
                        Service.Log.Warning(ex, "Unexpected failure while playing the targeting sound");
                    }
                }
            }).Start();
        }

        // WaveChannel32 只收單聲道／立體聲的 8／16／24 位元 PCM 或浮點；被拒的自訂音效以路徑＋最後寫入時間記住，換檔後才重試。
        // 播放執行緒可能重疊，集合一律在鎖內碰。
        private readonly object _unsupportedSoundsLock = new();
        private readonly HashSet<(string Path, DateTime WriteTime)> _unsupportedSounds = [];
        private int _soundThreadFailureLogged;

        private void PlaySoundOn(Guid device) {
            var path = Plugin.Config.SoundPath;
            (string Path, DateTime WriteTime)? key = path == null ? null : (path, LastWriteTimeOrMin(path));
            if (key != null) {
                lock (this._unsupportedSoundsLock) {
                    if (this._unsupportedSounds.Contains(key.Value)) {
                        return;
                    }
                }
            }

            WaveStream reader;
            try {
                if (path == null) {
                    reader = new WaveFileReader(Resource.AsStream("Resources/target.wav"));
                } else {
                    reader = new MediaFoundationReader(path);
                }
            } catch (Exception e) {
                var error = string.Format(Language.SoundChatError, e.Message);
                SendError(error);
                return;
            }

            using (reader) {
                WaveChannel32 channel;
                try {
                    channel = new WaveChannel32(reader);
                } catch (ArgumentException ex) when (key != null) {
                    ReportUnsupportedSound(key.Value, reader.WaveFormat, ex);
                    return;
                }

                using (channel) {
                    channel.Volume = Plugin.Config.SoundVolume;
                    channel.PadWithZeroes = false;

                    using var output = new DirectSoundOut(device);

                    try {
                        output.Init(channel);
                        output.Play();

                        while (output.PlaybackState == PlaybackState.Playing) {
                            Thread.Sleep(500);
                        }
                    } catch (Exception ex) {
                        Service.Log.Error(ex, "Exception playing sound");
                    }
                }
            }
        }

        private void ReportUnsupportedSound((string Path, DateTime WriteTime) key, WaveFormat format, Exception ex) {
            bool added;
            lock (this._unsupportedSoundsLock) {
                added = this._unsupportedSounds.Add(key);
            }

            if (!added) {
                return;
            }

            var fileName = Path.GetFileName(key.Path);
            Service.Log.Warning(ex, $"Sound file \"{fileName}\" has an unsupported format ({format}); it will be skipped until it changes");
            // NAudio 對所有不支援的格式擲同一則例外，提示依格式本身分流。
            SendError(format.Channels > 2
                ? string.Format(Language.SoundChannelsUnsupported, fileName)
                : string.Format(Language.SoundEncodingUnsupported, fileName, format.BitsPerSample, format.Encoding));
        }

        private static DateTime LastWriteTimeOrMin(string path) {
            try {
                return File.GetLastWriteTimeUtc(path);
            } catch (Exception) {
                return DateTime.MinValue;
            }
        }

        private void SendError(string message) {
            Service.ChatGui.Print(new XivChatEntry {
                Message = $"[{Plugin.Name}] {message}",
                Type = XivChatType.ErrorMessage,
            });
        }
    }
}
