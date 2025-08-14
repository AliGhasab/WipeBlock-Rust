using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AdvancedWipeBlock", "AliGhasab", "1.0.0")]
    [Description("Block using/equipping (but allow crafting & looting) of configured items after wipe. Includes modern admin UI to manage phases and blocks in-game.")]
    public class AdvancedWipeBlock : CovalencePlugin
    {
        #region Config

        private PluginConfig _config;
        private const string PERM_IGNORE = "advancedwipeblock.ignore";
        private const string PERM_BYPASS_PREFIX = "advancedwipeblock.bypass."; // e.g. advancedwipeblock.bypass.explosives
        private const string PERM_ADMIN = "advancedwipeblock.admin";

        private class PluginConfig
        {
            public string DateFormat = "yyyy-MM-dd HH:mm"; // used for messages
            public bool AutoDetectWipe = true;            // set wipe automatically on new save
            public bool StripBlockedFromBeltOnLogin = false; // we allow keeping items; no strip
            public bool BlockMoveToBelt = false;          // allow moving to belt
            public bool BlockCrafting = false;            // crafting allowed
            public bool BlockDeployment = true;           // treat deployment as a form of use
            public bool BlockPickup = false;              // looting allowed
            public bool NotifyWithGameTip = true;         // small HUD tip
            public bool NotifyInChat = true;
            public string ChatPrefix = "<color=#ffcc00>WipeBlock</color>";
            // UI theme
            public string UiPanelColor = "0 0 0 0.85";
            public string UiHeaderColor = "1 0.84 0 1";   // gold
            public string UiAccentColor = "0.35 0.75 0.35 1"; // green
            public string UiDangerColor = "0.8 0.2 0.2 1"; // red
            public string UiTextColor = "1 1 1 1";

            public Dictionary<string, string> CategoryAliases = new Dictionary<string, string>
            {
                ["explosives"] = "Explosives", ["weapons"] = "Weapons", ["ammo"] = "Ammo", ["armor"] = "Armor",
                ["electrical"] = "Electrical", ["building"] = "Building", ["medical"] = "Medical", ["vehicles"] = "Vehicles"
            };
            public List<Phase> Phases = new List<Phase>
            {
                new Phase
                {
                    FromHours = 0,
                    UntilHours = 24,
                    Name = "Early",
                    BlockedShortnames = new List<string> { "explosive.timed", "explosive.satchel", "surveycharge", "ammo.rocket.hv", "rocket.launcher", "autoturret" },
                    BlockedCategories = new List<string> { "explosives", "ammo" }
                },
                new Phase
                {
                    FromHours = 24,
                    UntilHours = 72,
                    Name = "Mid",
                    BlockedShortnames = new List<string> { "explosive.timed" },
                    BlockedCategories = new List<string> { }
                }
            };
        }

        public class Phase
        {
            public string Name;
            public double FromHours;
            public double UntilHours; // exclusive; if <=0 means forever after FromHours
            public List<string> BlockedShortnames = new List<string>();
            public List<string> BlockedCategories = new List<string>();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
            }
            catch
            {
                PrintError("Config file is corrupt, creating a new one");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Data

        private DynamicConfigFile _dataFile;
        private StoredData _data;
        private class StoredData
        {
            public DateTime WipeStartUtc = DateTime.UtcNow;
        }

        private void LoadData()
        {
            _dataFile = Interface.Oxide.DataFileSystem.GetFile("AdvancedWipeBlock/data");
            try { _data = _dataFile.ReadObject<StoredData>(); }
            catch { _data = new StoredData(); SaveData(); }
        }
        private void SaveData() => _dataFile.WriteObject(_data);

        #endregion

        #region Runtime State (UI)

        private readonly Dictionary<ulong, int> _uiSelectedPhase = new Dictionary<ulong, int>();

        private int GetSelectedPhase(BasePlayer p)
        {
            int idx;
            if (!_uiSelectedPhase.TryGetValue(p.userID, out idx))
            {
                idx = 0;
                _uiSelectedPhase[p.userID] = idx;
            }
            idx = Mathf.Clamp(idx, 0, Math.Max(0, _config.Phases.Count - 1));
            return idx;
        }

        private void SetSelectedPhase(BasePlayer p, int idx)
        {
            _uiSelectedPhase[p.userID] = Mathf.Clamp(idx, 0, Math.Max(0, _config.Phases.Count - 1));
        }

        #endregion

        #region Hooks

        private void Init()
        {
            LoadData();
            permission.RegisterPermission(PERM_IGNORE, this);
            permission.RegisterPermission(PERM_ADMIN, this);
            foreach (var alias in _config.CategoryAliases.Keys)
                permission.RegisterPermission(PERM_BYPASS_PREFIX + alias, this);

            AddCovalenceCommand(new[] { "wipeblock", "wb" }, nameof(CmdWipeBlock));
            AddCovalenceCommand("wipeblock.set", nameof(CmdSetWipe));
            AddCovalenceCommand(new[] { "wbadmin" }, nameof(CmdWbAdmin));
        }

        private void OnNewSave(string filename)
        {
            if (_config.AutoDetectWipe)
            {
                _data.WipeStartUtc = DateTime.UtcNow;
                SaveData();
                Puts("Detected wipe via OnNewSave at " + _data.WipeStartUtc.ToString("u"));
            }
        }

        private void OnUserConnected(IPlayer iplayer)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null) return;

            if (_config.NotifyWithGameTip)
                SendGameTip(player, "Use-block active: " + (ActivePhase()?.Name ?? "None"));
        }

        // We intentionally let crafting/looting happen (BlockCrafting=false, BlockPickup=false)
        private object CanCraft(ItemDefinition itemDef, BasePlayer crafter)
        {
            if (!_config.BlockCrafting || crafter == null || itemDef == null) return null; // by default not blocking crafting
            if (HasGlobalBypass(crafter)) return true;
            if (!IsBlocked(itemDef.shortname, crafter)) return null;
            Message(crafter.IPlayer, itemDef.displayName.english + " is blocked to craft during <color=#ffcc00>" + (ActivePhase()?.Name) + "</color> phase.");
            return false;
        }

        private object CanMoveItem(Item item, PlayerInventory inv, uint targetContainer, int targetSlot, int amount)
        {
            if (!_config.BlockMoveToBelt || item == null) return null; // default allow
            var player = inv?.GetComponent<BasePlayer>();
            if (player == null) return null;
            if (HasGlobalBypass(player)) return true;

            var cont = inv.FindContainer(targetContainer);
            if (cont != null && cont.containerType == ItemContainer.Type.Belt)
            {
                if (IsBlocked(item.info?.shortname, player))
                {
                    Message(player.IPlayer, "You cannot move <color=#ffcc00>" + item.info.displayName.english + "</color> to belt right now.");
                    return false;
                }
            }
            return null;
        }

        // Deployment/building is considered a form of using the item
        private object CanBuild(Planner planner, Construction prefab)
        {
            if (!_config.BlockDeployment) return null;
            var player = planner?.GetOwnerPlayer();
            if (player == null) return null;
            if (HasGlobalBypass(player)) return true;

            var item = planner?.GetItem();
            var shortname = item?.info?.shortname;
            if (string.IsNullOrEmpty(shortname)) return null;

            if (IsBlocked(shortname, player))
            {
                MessageUseBlocked(player, item.info?.displayName?.english ?? shortname);
                return false;
            }
            return null;
        }

        // NEW: Prevent equipping blocked items. Allow owning them.
        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null || newItem == null) return;
            if (HasGlobalBypass(player)) return;
            var shortname = newItem.info?.shortname;
            if (string.IsNullOrEmpty(shortname)) return;
            if (!IsBlocked(shortname, player)) return;

            MessageUseBlocked(player, newItem.info.displayName.english);
            // Revert to previous active item (or empty hands)
            player.Invoke(() => player.UpdateActiveItem(oldItem?.uid ?? 0), 0.03f);
        }

        // NEW: Prevent using consumables / tools while still owning them
        private object OnItemUse(Item item, int amount)
        {
            var player = item?.GetOwnerPlayer();
            if (player == null || item?.info == null) return null;
            if (HasGlobalBypass(player)) return null;
            if (!IsBlocked(item.info.shortname, player)) return null;

            MessageUseBlocked(player, item.info.displayName.english);
            // Cancel consumption by returning non-null (block) and restoring amount just in case
            item.amount += amount;
            item.MarkDirty();
            return true;
        }

        #endregion

        #region Core logic

        private Phase ActivePhase()
        {
            var hours = HoursSinceWipe();
            Phase chosen = null;
            foreach (var p in _config.Phases.OrderBy(p => p.FromHours))
            {
                var until = p.UntilHours <= 0 ? double.MaxValue : p.UntilHours;
                if (hours >= p.FromHours && hours < until)
                    chosen = p;
            }
            return chosen;
        }

        private double HoursSinceWipe() => Math.Max(0, (DateTime.UtcNow - _data.WipeStartUtc).TotalHours);

        private bool IsBlocked(string shortname, BasePlayer player)
        {
            var phase = ActivePhase();
            if (phase == null) return false;
            if (string.IsNullOrEmpty(shortname)) return false;

            // explicit shortname
            if (phase.BlockedShortnames.Contains(shortname))
            {
                if (HasCategoryBypass(player, GuessCategory(shortname))) return false;
                return true;
            }

            // category-based block
            var cat = GuessCategory(shortname);
            if (cat != null && phase.BlockedCategories.Contains(cat))
            {
                if (HasCategoryBypass(player, cat)) return false;
                return true;
            }

            return false;
        }

        private string GuessCategory(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return null;
            // lightweight heuristic; owners should prefer explicit shortnames for precision
            if (shortname.Contains("rocket") || shortname.Contains("explosive") || shortname.Contains("satchel")) return "explosives";
            if (shortname.StartsWith("ammo.")) return "ammo";
            if (shortname.Contains("rifle") || shortname.Contains("pistol") || shortname.Contains("shotgun") || shortname.Contains("smg")) return "weapons";
            if (shortname.Contains("autoturret") || shortname.Contains("shotgun.trap") || shortname.Contains("guntrap")) return "building";
            if (shortname.Contains("med")) return "medical";
            return null;
        }

        private bool HasGlobalBypass(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, PERM_IGNORE);
        }

        private bool HasCategoryBypass(BasePlayer player, string category)
        {
            if (string.IsNullOrEmpty(category)) return false;
            return permission.UserHasPermission(player.UserIDString, PERM_BYPASS_PREFIX + category);
        }

        private string RemainingText()
        {
            var phase = ActivePhase();
            if (phase == null) return string.Empty;
            var hours = HoursSinceWipe();
            if (phase.UntilHours <= 0) return ""; // open-ended
            var remain = Math.Max(0, phase.UntilHours - hours);
            var ts = TimeSpan.FromHours(remain);
            if (ts.TotalHours >= 1)
                return $" (~ {Math.Floor(ts.TotalHours)}h {ts.Minutes}m)";
            return $" (~ {ts.Minutes}m)";
        }

        #endregion

        #region Commands (player)

        private void CmdWipeBlock(IPlayer iplayer, string cmd, string[] args)
        {
            if (!iplayer.IsConnected) return;
            var player = iplayer.Object as BasePlayer;
            var phase = ActivePhase();
            var hours = HoursSinceWipe();
            var until = phase?.UntilHours > 0 ? phase.UntilHours - hours : (double?)null;
            var lines = new List<string>();
            lines.Add("<size=18><b>Temporary Use-Blocks</b></size>");
            lines.Add("Phase: <b>" + (phase?.Name ?? "None") + "</b> (since wipe: " + hours.ToString("0.0") + "h)");
            if (until.HasValue) lines.Add("Ends in: " + Math.Max(0, until.Value).ToString("0.0") + "h");
            if (phase != null)
            {
                if (phase.BlockedShortnames.Count > 0)
                    lines.Add("Blocked items: " + string.Join(", ", phase.BlockedShortnames));
                if (phase.BlockedCategories.Count > 0)
                    lines.Add("Blocked categories: " + string.Join(", ", phase.BlockedCategories));
            }
            lines.Add("Craft & loot are allowed. Using/equipping is blocked if listed above.");
            Message(iplayer, string.Join("
", lines));
        }

        private void CmdSetWipe(IPlayer iplayer, string cmd, string[] args)
        {
            if (!iplayer.HasPermission("oxide.admin") && !iplayer.HasPermission(PERM_ADMIN))
            {
                Message(iplayer, "You need admin to run this.");
                return;
            }
            if (args.Length == 0)
            {
                Message(iplayer, "Wipe start is " + _data.WipeStartUtc.ToString(_config.DateFormat) + " UTC. Use: " + cmd + " yyyy-MM-dd HH:mm (UTC)");
                return;
            }
            DateTime local;
            if (DateTime.TryParse(string.Join(" ", args), out local))
            {
                _data.WipeStartUtc = DateTime.SpecifyKind(local, DateTimeKind.Utc);
                SaveData();
                Message(iplayer, "Wipe start set to " + _data.WipeStartUtc.ToString(_config.DateFormat) + " UTC");
            }
            else Message(iplayer, "Invalid date/time.");
        }

        private void CmdWbAdmin(IPlayer iplayer, string cmd, string[] args)
        {
            if (!iplayer.HasPermission(PERM_ADMIN) && !iplayer.HasPermission("oxide.admin"))
            {
                Message(iplayer, "You do not have permission to open the admin panel.");
                return;
            }
            var bp = iplayer.Object as BasePlayer; if (bp == null) return;
            OpenAdminPanel(bp);
        }

        #endregion

        #region Commands (console from UI)

        [ConsoleCommand("awb.ui.close")] private void UiClose(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            CuiHelper.DestroyUi(bp, "WB_AdminPanel");
        }

        [ConsoleCommand("awb.ui.selectphase")] private void UiSelectPhase(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            int idx = arg.GetInt(0, 0);
            SetSelectedPhase(bp, idx);
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.additem")] private void UiAddItem(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            var shortname = arg.GetString(0, string.Empty).Trim();
            if (string.IsNullOrEmpty(shortname)) { SendReply(bp, "Enter item shortname"); return; }
            var phase = _config.Phases.ElementAtOrDefault(GetSelectedPhase(bp)); if (phase == null) return;
            if (!phase.BlockedShortnames.Contains(shortname)) phase.BlockedShortnames.Add(shortname);
            SaveConfig();
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.addcat")] private void UiAddCat(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            var cat = arg.GetString(0, string.Empty).Trim().ToLower();
            if (string.IsNullOrEmpty(cat)) { SendReply(bp, "Enter category key"); return; }
            var phase = _config.Phases.ElementAtOrDefault(GetSelectedPhase(bp)); if (phase == null) return;
            if (!phase.BlockedCategories.Contains(cat)) phase.BlockedCategories.Add(cat);
            SaveConfig();
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.removeitem")] private void UiRemoveItem(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            var shortname = arg.GetString(0, string.Empty).Trim();
            var phase = _config.Phases.ElementAtOrDefault(GetSelectedPhase(bp)); if (phase == null) return;
            phase.BlockedShortnames.Remove(shortname);
            SaveConfig();
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.removecat")] private void UiRemoveCat(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            var cat = arg.GetString(0, string.Empty).Trim();
            var phase = _config.Phases.ElementAtOrDefault(GetSelectedPhase(bp)); if (phase == null) return;
            phase.BlockedCategories.Remove(cat);
            SaveConfig();
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.addphase")] private void UiAddPhase(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            var name = arg.GetString(0, "New");
            var fromH = arg.GetFloat(1, 0f);
            var untilH = arg.GetFloat(2, 0f);
            _config.Phases.Add(new Phase { Name = name, FromHours = fromH, UntilHours = untilH });
            _config.Phases = _config.Phases.OrderBy(p => p.FromHours).ToList();
            SaveConfig();
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.editphase")] private void UiEditPhase(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            int idx = GetSelectedPhase(bp);
            var phase = _config.Phases.ElementAtOrDefault(idx); if (phase == null) return;
            var name = arg.GetString(0, phase.Name);
            var fromH = arg.GetFloat(1, (float)phase.FromHours);
            var untilH = arg.GetFloat(2, (float)phase.UntilHours);
            phase.Name = name; phase.FromHours = fromH; phase.UntilHours = untilH;
            _config.Phases = _config.Phases.OrderBy(p => p.FromHours).ToList();
            SaveConfig();
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.removephase")] private void UiRemovePhase(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            int idx = GetSelectedPhase(bp);
            if (idx >= 0 && idx < _config.Phases.Count)
                _config.Phases.RemoveAt(idx);
            SaveConfig();
            SetSelectedPhase(bp, 0);
            OpenAdminPanel(bp);
        }

        [ConsoleCommand("awb.ui.setwipe")] private void UiSetWipe(ConsoleSystem.Arg arg)
        {
            var bp = arg?.Player(); if (bp == null) return;
            double hoursAgo = arg.GetFloat(0, 0f);
            _data.WipeStartUtc = DateTime.UtcNow - TimeSpan.FromHours(hoursAgo);
            SaveData();
            OpenAdminPanel(bp);
        }

        #endregion

        #region UI Builder

        private void OpenAdminPanel(BasePlayer bp)
        {
            if (bp == null) return;
            CuiHelper.DestroyUi(bp, "WB_AdminPanel");

            var elements = new CuiElementContainer();
            var root = elements.Add(new CuiPanel
            {
                Image = { Color = _config.UiPanelColor },
                RectTransform = { AnchorMin = "0.2 0.15", AnchorMax = "0.8 0.85" },
                CursorEnabled = true
            }, "Overlay", "WB_AdminPanel");

            // Header bar
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.2" },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, root, "WB_Header");

            var phase = ActivePhase();
            var hours = HoursSinceWipe();
            var until = phase?.UntilHours > 0 ? (phase.UntilHours - hours) : (double?)null;
            string headerText = "Advanced Wipe Block – Admin | Wipe: " + _data.WipeStartUtc.ToString("u") + " UTC  | Phase: " + (phase?.Name ?? "None") + "  | Since: " + hours.ToString("0.0") + "h  " + (until.HasValue ? "| Ends in: " + Math.Max(0, until.Value).ToString("0.0") + "h" : "");

            elements.Add(new CuiLabel
            {
                Text = { Text = headerText, FontSize = 16, Align = TextAnchor.MiddleCenter, Color = _config.UiHeaderColor },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "WB_Header");

            // Close button
            elements.Add(new CuiButton
            {
                Button = { Command = "awb.ui.close", Color = _config.UiDangerColor },
                Text = { Text = "✕", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.96 0.92", AnchorMax = "0.995 0.995" }
            }, root);

            // Left: Phases list
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.35" },
                RectTransform = { AnchorMin = "0.02 0.08", AnchorMax = "0.34 0.9" }
            }, root, "WB_Phases");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Phases", FontSize = 16, Align = TextAnchor.UpperLeft, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.02 0.87", AnchorMax = "0.98 0.98" }
            }, "WB_Phases");

            var y = 0.82f;
            for (int i = 0; i < _config.Phases.Count; i++)
            {
                var p = _config.Phases[i];
                var selected = (i == GetSelectedPhase(bp));
                elements.Add(new CuiButton
                {
                    Button = { Command = "awb.ui.selectphase " + i, Color = selected ? _config.UiAccentColor : "0.2 0.2 0.2 0.9" },
                    Text = { Text = p.Name + "  [" + p.FromHours.ToString("0") + "-" + (p.UntilHours <= 0 ? "∞" : p.UntilHours.ToString("0")) + "h]", FontSize = 13, Align = TextAnchor.MiddleLeft, Color = _config.UiTextColor },
                    RectTransform = { AnchorMin = string.Format("0.03 {0}", y), AnchorMax = string.Format("0.97 {0}", y + 0.06f) }
                }, "WB_Phases");
                y -= 0.065f; if (y < 0.12f) break;
            }

            // Phase controls (edit/remove/add)
            var selIdx = GetSelectedPhase(bp);
            var selPhase = _config.Phases.ElementAtOrDefault(selIdx);
            if (selPhase != null)
            {
                elements.Add(new CuiButton
                {
                    Button = { Command = "awb.ui.editphase \"" + selPhase.Name + "\" " + selPhase.FromHours + " " + selPhase.UntilHours, Color = _config.UiAccentColor },
                    Text = { Text = "Save Phase (keep values)", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                    RectTransform = { AnchorMin = "0.04 0.09", AnchorMax = "0.23 0.14" }
                }, "WB_Phases");

                elements.Add(new CuiButton
                {
                    Button = { Command = "awb.ui.removephase", Color = _config.UiDangerColor },
                    Text = { Text = "Delete Phase", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                    RectTransform = { AnchorMin = "0.25 0.09", AnchorMax = "0.44 0.14" }
                }, "WB_Phases");
            }

            // Add phase quick presets
            elements.Add(new CuiButton
            {
                Button = { Command = "awb.ui.addphase \"Early\" 0 24", Color = _config.UiAccentColor },
                Text = { Text = "+ Add Early 0-24h", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.46 0.09", AnchorMax = "0.79 0.14" }
            }, "WB_Phases");

            elements.Add(new CuiButton
            {
                Button = { Command = "awb.ui.addphase \"Mid\" 24 72", Color = _config.UiAccentColor },
                Text = { Text = "+ Add Mid 24-72h", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.80 0.09", AnchorMax = "0.97 0.14" }
            }, "WB_Phases");

            // Middle: Items of selected phase
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.35" },
                RectTransform = { AnchorMin = "0.36 0.08", AnchorMax = "0.67 0.9" }
            }, root, "WB_Items");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Blocked Items (shortnames)", FontSize = 16, Align = TextAnchor.UpperLeft, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.02 0.87", AnchorMax = "0.98 0.98" }
            }, "WB_Items");

            float iy = 0.82f;
            if (selPhase != null)
            {
                foreach (var s in selPhase.BlockedShortnames.Take(10))
                {
                    elements.Add(new CuiLabel
                    {
                        Text = { Text = s, FontSize = 13, Align = TextAnchor.MiddleLeft, Color = _config.UiTextColor },
                        RectTransform = { AnchorMin = string.Format("0.05 {0}", iy), AnchorMax = string.Format("0.7 {0}", iy + 0.05f) }
                    }, "WB_Items");
                    elements.Add(new CuiButton
                    {
                        Button = { Command = "awb.ui.removeitem " + s, Color = _config.UiDangerColor },
                        Text = { Text = "Remove", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                        RectTransform = { AnchorMin = string.Format("0.72 {0}", iy), AnchorMax = string.Format("0.95 {0}", iy + 0.05f) }
                    }, "WB_Items");
                    iy -= 0.055f; if (iy < 0.12f) break;
                }
            }

            // Add item quick inputs (common examples)
            var quickItems = new[] { "rocket.launcher", "ammo.rocket.hv", "explosive.timed", "autoturret" };
            for (int q = 0; q < quickItems.Length; q++)
            {
                var x0 = 0.05f + (q % 2) * 0.25f;
                var y0 = 0.14f - (q / 2) * 0.06f;
                elements.Add(new CuiButton
                {
                    Button = { Command = "awb.ui.additem " + quickItems[q], Color = _config.UiAccentColor },
                    Text = { Text = "+ " + quickItems[q], FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                    RectTransform = { AnchorMin = string.Format("{0} {1}", x0, y0), AnchorMax = string.Format("{0} {1}", x0 + 0.22f, y0 + 0.05f) }
                }, "WB_Items");
            }

            // Right: Categories of selected phase
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.35" },
                RectTransform = { AnchorMin = "0.69 0.08", AnchorMax = "0.98 0.9" }
            }, root, "WB_Cats");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Blocked Categories", FontSize = 16, Align = TextAnchor.UpperLeft, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.02 0.87", AnchorMax = "0.98 0.98" }
            }, "WB_Cats");

            float cy = 0.82f;
            if (selPhase != null)
            {
                foreach (var c in selPhase.BlockedCategories.Take(10))
                {
                    var alias = _config.CategoryAliases.ContainsKey(c) ? _config.CategoryAliases[c] : c;
                    elements.Add(new CuiLabel
                    {
                        Text = { Text = c + " (" + alias + ")", FontSize = 13, Align = TextAnchor.MiddleLeft, Color = _config.UiTextColor },
                        RectTransform = { AnchorMin = string.Format("0.05 {0}", cy), AnchorMax = string.Format("0.7 {0}", cy + 0.05f) }
                    }, "WB_Cats");
                    elements.Add(new CuiButton
                    {
                        Button = { Command = "awb.ui.removecat " + c, Color = _config.UiDangerColor },
                        Text = { Text = "Remove", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                        RectTransform = { AnchorMin = string.Format("0.72 {0}", cy), AnchorMax = string.Format("0.95 {0}", cy + 0.05f) }
                    }, "WB_Cats");
                    cy -= 0.055f; if (cy < 0.26f) break;
                }
            }

            // Category quick add buttons
            var quickCats = _config.CategoryAliases.Keys.Take(6).ToArray();
            for (int q = 0; q < quickCats.Length; q++)
            {
                float x0 = 0.05f + (q % 2) * 0.25f;
                float y0 = 0.14f - (q / 2) * 0.06f;
                elements.Add(new CuiButton
                {
                    Button = { Command = "awb.ui.addcat " + quickCats[q], Color = _config.UiAccentColor },
                    Text = { Text = "+ " + quickCats[q], FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                    RectTransform = { AnchorMin = string.Format("{0} {1}", x0, y0), AnchorMax = string.Format("{0} {1}", x0 + 0.22f, y0 + 0.05f) }
                }, "WB_Cats");
            }

            // Wipe time quick-set (bottom bar)
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.2" },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.98 0.065" }
            }, root, "WB_Footer");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Set wipe time (hours ago):", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = _config.UiTextColor },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.22 1" }
            }, "WB_Footer");

            float bx = 0.24f;
            int[] presets = { 0, 1, 6, 12, 24, 48 };
            foreach (var h in presets)
            {
                elements.Add(new CuiButton
                {
                    Button = { Command = "awb.ui.setwipe " + h, Color = _config.UiAccentColor },
                    Text = { Text = h + "h", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = _config.UiTextColor },
                    RectTransform = { AnchorMin = string.Format("{0} 0.1", bx), AnchorMax = string.Format("{0} 0.9", bx + 0.06f) }
                }, "WB_Footer");
                bx += 0.065f;
            }

            CuiHelper.AddUi(bp, elements);
        }

        #endregion

        #region Utils

        private void Message(IPlayer player, string text)
        {
            if (_config.NotifyInChat)
                player.Reply(_config.ChatPrefix + ": " + text);
        }

        private void SendGameTip(BasePlayer player, string text)
        {
            if (!_config.NotifyWithGameTip) return;
            player.SendConsoleCommand("gametip.showgametip", text);
            timer.Once(6f, () => player.SendConsoleCommand("gametip.hidegametip"));
        }

        private void MessageUseBlocked(BasePlayer player, string itemName)
        {
            var tail = RemainingText();
            var fa = $"⚠ استفاده از {itemName} در حال حاضر بلاک است{(string.IsNullOrEmpty(tail) ? "" : " ")}{tail.Replace("~","~")}.";
            var en = $"⚠ Using {itemName} is blocked now{tail}.";
            if (_config.NotifyInChat)
                player.ChatMessage($"{_config.ChatPrefix}: {fa} / {en}");
            if (_config.NotifyWithGameTip)
                SendGameTip(player, $"Use blocked: {itemName}{tail}");
        }

        #endregion
    }
}
