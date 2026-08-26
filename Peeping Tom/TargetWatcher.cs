using NAudio.Wave;
using Resourcer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private List<Targeter> Previous { get; } = [];

        public IReadOnlyCollection<Targeter> PreviousTargeters => Previous;

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
            // API13 把 IClientState.LocalPlayer 標為過時；ClientState.LocalPlayer 本身就是
            // => this.objectTable.LocalPlayer 的純轉發，改用 IObjectTable 取值行為不變。
            var player = Service.ObjectTable.LocalPlayer;
            if (player == null) {
                return;
            }

            // get targeters and set a copy so we can release the mutex faster
            var newCurrent = GetTargeting(Service.ObjectTable, player);

            var logToChat = CanLogToChat();

            foreach (var newTargeter in newCurrent.Where(t => Current.All(c => c.GameObjectId != t.GameObjectId))) {
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

            foreach (var stopped in Current.Where(t => newCurrent.All(c => c.GameObjectId != t.GameObjectId))) {
                try {
                    Plugin.IpcManager.SendStoppedTargeting(stopped);
                } catch (Exception ex) {
                    Service.Log.Error(ex, "Failed to send IPC message");
                }
            }

            Current = newCurrent;

            HandleHistory(Current);

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
                if (Previous.Any(old => old.GameObjectId == targeter.GameObjectId)) {
                    Previous.RemoveAll(old => old.GameObjectId == targeter.GameObjectId);
                }

                Previous.Insert(0, targeter);
            }

            // only keep the configured number of previous targeters (ignoring ones that are currently targeting)
            while (Previous.Count(old => targeting.All(actor => actor.GameObjectId != old.GameObjectId)) > Plugin.Config.NumHistory) {
                Previous.RemoveAt(Previous.Count - 1);
            }
        }

        private Targeter[] GetTargeting(IEnumerable<IGameObject> objects, IGameObject player) {
            return objects
                .Where(obj => obj is IPlayerCharacter && (obj.ObjectIndex == 0 ? (Service.TargetManager.SoftTarget ?? Service.TargetManager.Target)?.GameObjectId == player.GameObjectId : obj.TargetObjectId == player.GameObjectId))
                // .Where(obj => Marshal.ReadByte(obj.Address + ActorOffsets.PlayerCharacterTargetActorId + 4) == 0)
                .Cast<IPlayerCharacter>()
                .Where(actor => Plugin.Config.LogParty || !InParty(actor))
                .Where(actor => Plugin.Config.LogAlliance || !InAlliance(actor))
                .Where(actor => Plugin.Config.LogInCombat || !InCombat(actor))
                .Where(actor => Plugin.Config.LogSelf || actor.GameObjectId != player.GameObjectId)
                .Select(actor => new Targeter(actor))
                .ToArray();
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

            new Thread(() => {
                WaveStream reader;
                try {
                    if (Plugin.Config.SoundPath == null) {
                        reader = new WaveFileReader(Resource.AsStream("Resources/target.wav"));
                    } else {
                        reader = new MediaFoundationReader(Plugin.Config.SoundPath);
                    }
                } catch (Exception e) {
                    var error = string.Format(Language.SoundChatError, e.Message);
                    SendError(error);
                    return;
                }

                using var channel = new WaveChannel32(reader);
                channel.Volume = Plugin.Config.SoundVolume;
                channel.PadWithZeroes = false;

                using (reader) {
                    using var output = new DirectSoundOut(soundDevice.Guid);

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
            }).Start();
        }

        private void SendError(string message) {
            Service.ChatGui.Print(new XivChatEntry {
                Message = $"[{Plugin.Name}] {message}",
                Type = XivChatType.ErrorMessage,
            });
        }
    }
}
