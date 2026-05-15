using System.Collections.Generic;
using System.Linq;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AutoTC", "Evo", "1.3.0")]
    [Description("Adds an auto-upgrade GUI to the Tool Cupboard using TC resources.")]
    public class AutoTC : RustPlugin
    {
        private const string UI_PARENT = "AutoTC_UI";

        void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;

            if (entity is BuildingPrivlidge priv && priv.IsAuthed(player))
            {
                CreateUpgradeUI(player);
            }
        }

        void OnPlayerLootEnd(PlayerLoot inventory)
        {
            BasePlayer player = inventory.GetComponent<BasePlayer>();
            if (player != null)
            {
                CuiHelper.DestroyUi(player, UI_PARENT);
            }
        }

        private void CreateUpgradeUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_PARENT);
            CuiHelper.DestroyUi(player, "BetterTC_UI"); 

            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel
            {
                Image = { 
                    Sprite = "assets/icons/faded_square.png", 
                    Color = "0.12 0.12 0.12 0.98", 
                    Material = "assets/content/ui/uibackgroundblur-runway.mat" 
                },
                RectTransform = { AnchorMin = "0.65 0.02", AnchorMax = "0.98 0.13" },
                CursorEnabled = true
            }, "Overlay", UI_PARENT);

            elements.Add(new CuiLabel
            {
                Text = { Text = "TC AUTO-UPGRADE & REPAIR", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0.65", AnchorMax = "1 1" }
            }, UI_PARENT);

            CreateUpgradeButton(ref elements, "Wood", "1", "0.02 0.1 0.20 0.65", "0.43 0.35 0.25 0.8", "autotc.upgrade");
            CreateUpgradeButton(ref elements, "Stone", "2", "0.215 0.1 0.395 0.65", "0.35 0.35 0.35 0.8", "autotc.upgrade");
            CreateUpgradeButton(ref elements, "Metal", "3", "0.41 0.1 0.59 0.65", "0.55 0.3 0.15 0.8", "autotc.upgrade");
            CreateUpgradeButton(ref elements, "Armored", "4", "0.605 0.1 0.785 0.65", "0.2 0.45 0.45 0.8", "autotc.upgrade");
            CreateUpgradeButton(ref elements, "Repair", "0", "0.8 0.1 0.98 0.65", "0.7 0.5 0.2 0.8", "autotc.repair");

            CuiHelper.AddUi(player, elements);
        }

        private void CreateUpgradeButton(ref CuiElementContainer elements, string label, string arg, string anchors, string color, string command)
        {
            string[] a = anchors.Split(' ');
            elements.Add(new CuiButton
            {
                Button = { Command = $"{command} {arg}", Color = color, Sprite = "assets/icons/faded_square.png" },
                RectTransform = { AnchorMin = $"{a[0]} {a[1]}", AnchorMax = $"{a[2]} {a[3]}" },
                Text = { Text = label.ToUpper(), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }
            }, UI_PARENT);
        }

        [ConsoleCommand("autotc.upgrade")]
        private void CmdUpgrade(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs()) return;

            BuildingGrade.Enum targetGradeEnum = (BuildingGrade.Enum)arg.GetInt(0);
            BuildingPrivlidge priv = player.GetBuildingPrivilege();

            if (priv == null || !priv.IsAuthed(player)) return;

            var building = priv.GetBuilding();
            if (building == null) return;

            ServerMgr.Instance.StartCoroutine(UpgradeRoutine(player, priv, building.buildingBlocks.ToList(), targetGradeEnum));
        }

        private System.Collections.IEnumerator UpgradeRoutine(BasePlayer player, BuildingPrivlidge priv, List<BuildingBlock> blocks, BuildingGrade.Enum targetGradeEnum)
        {
            int count = 0;
            int failed = 0;
            int batchSize = 10;
            int processedThisFrame = 0;

            Dictionary<uint, List<ItemAmount>> costCache = new Dictionary<uint, List<ItemAmount>>();

            foreach (var block in blocks)
            {
                if (block == null || block.grade == targetGradeEnum) continue;

                List<ItemAmount> upgradeCosts;

                if (!costCache.TryGetValue(block.prefabID, out upgradeCosts))
                {
                    var construction = PrefabAttribute.server.Find<Construction>(block.prefabID);
                    if (construction == null) continue;

                    ConstructionGrade grade = construction.GetGrade(targetGradeEnum, player.skinID);
                    if (grade == null) continue;

                    upgradeCosts = grade.CostToBuild();
                    costCache[block.prefabID] = upgradeCosts;
                }

                bool canAfford = true;
                
                foreach (var cost in upgradeCosts)
                {
                    if (priv.inventory.GetAmount(cost.itemid, false) < (int)cost.amount)
                    {
                        canAfford = false;
                        break;
                    }
                }

                if (!canAfford)
                {
                    failed++;
                    continue;
                }

                foreach (var cost in upgradeCosts)
                {
                    priv.inventory.Take(null, cost.itemid, (int)cost.amount);
                }

                block.SetGrade(targetGradeEnum);
                block.SetHealthToMax();
                block.UpdateSkin();
                block.SendNetworkUpdate();
                
                count++;
                processedThisFrame++;

                if (processedThisFrame >= batchSize)
                {
                    processedThisFrame = 0;
                    yield return null; 
                }
            }

            if (player != null && player.IsConnected)
            {
                SendReply(player, $"<color=#55ff55>Upgrade complete:</color> {count} upgraded from TC resources. {failed} skipped (TC missing items).");
            }
        }

        [ConsoleCommand("autotc.repair")]
        private void CmdRepair(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            BuildingPrivlidge priv = player.GetBuildingPrivilege();
            if (priv == null || !priv.IsAuthed(player)) return;

            var building = priv.GetBuilding();
            if (building == null) return;

            ServerMgr.Instance.StartCoroutine(RepairRoutine(player, priv, building.buildingBlocks.ToList()));
        }

        private System.Collections.IEnumerator RepairRoutine(BasePlayer player, BuildingPrivlidge priv, List<BuildingBlock> blocks)
        {
            int count = 0;
            int failed = 0;
            int batchSize = 10;
            int processedThisFrame = 0;

            foreach (var block in blocks)
            {
                if (block == null || block.health >= block.MaxHealth()) continue;

                ConstructionGrade grade = block.currentGrade;
                if (grade == null) continue;

                List<ItemAmount> costs = grade.CostToBuild();
                float missingFraction = 1f - (block.health / block.MaxHealth());
                float repairMultiplier = 0.2f;

                bool canAfford = true;
                Dictionary<int, int> requiredItems = new Dictionary<int, int>();

                foreach (var cost in costs)
                {
                    int amountNeeded = Mathf.CeilToInt(cost.amount * missingFraction * repairMultiplier);
                    if (amountNeeded <= 0) continue;

                    if (priv.inventory.GetAmount(cost.itemid, false) < amountNeeded)
                    {
                        canAfford = false;
                        break;
                    }
                    requiredItems[cost.itemid] = amountNeeded;
                }

                if (!canAfford)
                {
                    failed++;
                    continue;
                }

                foreach (var kvp in requiredItems)
                {
                    priv.inventory.Take(null, kvp.Key, kvp.Value);
                }

                block.SetHealthToMax();
                block.SendNetworkUpdate();

                count++;
                processedThisFrame++;

                if (processedThisFrame >= batchSize)
                {
                    processedThisFrame = 0;
                    yield return null; 
                }
            }

            if (player != null && player.IsConnected)
            {
                SendReply(player, $"<color=#55ff55>Repair complete:</color> {count} blocks repaired. {failed} skipped (TC missing items).");
            }
        }
    }
}
