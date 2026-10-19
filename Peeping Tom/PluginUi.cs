using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace PeepingTom {
    internal class PluginUi : IDisposable {
        private Plugin Plugin { get; }

        private WindowSystem WindowSystem { get; } = new();
        public MainWindow MainWindow { get; }
        public SettingsWindow SettingsWindow { get; }
        
        public PluginUi(Plugin plugin) {
            Plugin = plugin;
            
            MainWindow = new MainWindow(Plugin, this);
            SettingsWindow = new SettingsWindow(Plugin);
            
            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(SettingsWindow);
        }

        public void Dispose() {
            WindowSystem.RemoveAllWindows();
        }

        public void Draw() {
            if (Plugin.InPvp) return;
            
            WindowSystem.Draw();
            
            if (Plugin.Config.MarkTargeted) {
                MarkPlayer(GetCurrentTarget(), Plugin.Config.TargetedColour, Plugin.Config.TargetedSize);
            }

            if (!Plugin.Config.MarkTargeting) return;

            var player = Service.ObjectTable.LocalPlayer;
            if (player == null) return;

            // 每幀都跑、不配置。查到的包裝只在這一幀內用，不留到下一幀。
            foreach (var targeter in Plugin.Watcher.CurrentSpan) {
                if (FindObject(targeter.GameObjectId) is IPlayerCharacter actor) {
                    MarkPlayer(actor, Plugin.Config.TargetingColour, Plugin.Config.TargetingSize);
                }
            }
        }

        // 物件表裡第一個 id 相符的物件（不管是不是玩家）。
        private static IGameObject? FindObject(ulong gameObjectId) {
            foreach (var obj in Service.ObjectTable) {
                if (obj.GameObjectId == gameObjectId) {
                    return obj;
                }
            }

            return null;
        }

        private void MarkPlayer(IGameObject? player, Vector4 colour, float size) {
            if (player == null) {
                return;
            }

            if (!Service.GameGui.WorldToScreen(player.Position, out var screenPos)) {
                return;
            }

            ImGui.GetBackgroundDrawList().PushClipRect(ImGuiHelpers.MainViewport.Pos, ImGuiHelpers.MainViewport.Pos + ImGuiHelpers.MainViewport.Size, false);

            ImGui.GetBackgroundDrawList().AddCircleFilled(
                new Vector2(screenPos.X, screenPos.Y),
                size,
                ImGui.GetColorU32(colour),
                100
            );

            ImGui.GetBackgroundDrawList().PopClipRect();
        }

        private IPlayerCharacter? GetCurrentTarget() {
            var player = Service.ObjectTable.LocalPlayer;
            if (player == null) {
                return null;
            }

            var targetId = player.TargetObjectId;
            if (targetId <= 0) {
                return null;
            }

            foreach (var actor in Service.ObjectTable) {
                if (actor.GameObjectId == targetId && actor is IPlayerCharacter targeted) {
                    return targeted;
                }
            }

            return null;
        }
    }
}
