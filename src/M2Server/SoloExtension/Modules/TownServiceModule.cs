using System;
using System.Collections.Generic;
using M2Server;
using M2Server.Player;
using OpenMir2.Common;
using OpenMir2.Consts;
using OpenMir2.Data;
using OpenMir2.Enums;
using OpenMir2.Packets.ClientPackets;
using SystemModule;
using SystemModule.Actors;
using SystemModule.Data;
using SystemModule.Enums;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 城镇军需官与前线行军帐模块（极品锁死一键统卖、全身特修、洞穴立营与安全区返场）
    /// </summary>
    public static class TownServiceModule
    {
        /// <summary>
        /// 向客户端发送标准 1.76 原生 NPC 对话框（无需新增 UI 控件）
        /// </summary>
        public static void SendNativeNpcDialog(PlayObject player, string title, string dialogBody)
        {
            if (player == null)
            {
                return;
            }

            IActor senderNpc = (IActor)SystemShare.ManageNPC ?? (IActor)SystemShare.FunctionNPC ?? player;
            player.LastNpc = senderNpc.ActorId;
            player.SendMsg(senderNpc, Messages.RM_MERCHANTSAY, 0, 0, 0, 0, $"{title}/{dialogBody}");
        }

        /// <summary>
        /// 打开【玛法行军帐】交互面板
        /// </summary>
        public static void OpenCampDialog(PlayObject player)
        {
            if (player == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            string campStatus = string.IsNullOrEmpty(profile.CampMapName)
                ? "<尚未设立前线营地/FCOLOR=249>"
                : $"<{profile.CampMapDesc}({profile.CampMapName}) [{profile.CampX}:{profile.CampY}]/FCOLOR=251>";

            string currentZone = player.InSafeZone() ? "<城镇安全区（可拔营返场）/FCOLOR=250>" : "<野外探索区（可扎下营帐）/FCOLOR=254>";

            string body =
                $"勇士 <{player.ChrName}>，玛法行军帐助你免去万水千山的跑图之苦。\\" +
                $"当前所处区域：{currentZone}\\" +
                $"当前记录营地：{campStatus}\\" +
                "------------------------------------------------\\" +
                "【铁律】洞穴深处扎营，回城仍需使用回城卷；唯有在城镇安全区方可重返营地！\\" +
                $"<在此处扎下前线营帐（消耗1张地牢逃脱卷或{SoloGlobalConfig.CampSetupGoldCost}金币）/@Camp_Set>\\" +
                "<从安全区立刻返回前线营地/@Camp_Return>\\" +
                "<顺便打开随身军需整备/@Solo_OpenQuartermaster>    <关闭窗口/@exit>";

            SendNativeNpcDialog(player, SoloGlobalConfig.CampItemName, body);
        }

        /// <summary>
        /// 在当前野外/洞穴坐标设立营地锚点
        /// </summary>
        public static void SetCampAnchor(PlayObject player)
        {
            if (player?.Envir == null)
            {
                return;
            }

            if (player.InSafeZone())
            {
                player.SysMsg("【玛法行军帐】安全区内无需扎营，请在野外或洞穴探索时使用！", MsgColor.Red, MsgType.Hint);
                return;
            }

            string mapName = player.Envir.MapName ?? string.Empty;
            string mapDesc = player.Envir.MapDesc ?? string.Empty;
            if (SoloGlobalConfig.ForbiddenCampMaps.Contains(mapName) ||
                SoloGlobalConfig.ForbiddenCampMaps.Contains(mapDesc) ||
                mapDesc.Contains("教主") || mapDesc.Contains("魔穴") || mapDesc.Contains("祭坛"))
            {
                player.SysMsg($"【玛法行军帐】{mapDesc} 魔气过于狂暴，严禁在首领巢穴内扎营！", MsgColor.Red, MsgType.Hint);
                return;
            }

            // 优先消耗 1 张地牢逃脱卷，若无则消耗 1000 金币
            bool paid = TryConsumeBagItem(player, "地牢逃脱卷");
            string costDesc = "1 张地牢逃脱卷";
            if (!paid)
            {
                if (player.Gold < SoloGlobalConfig.CampSetupGoldCost || !player.DecGold(SoloGlobalConfig.CampSetupGoldCost))
                {
                    player.SysMsg($"【玛法行军帐】扎营失败：需要 1 张地牢逃脱卷或 {SoloGlobalConfig.CampSetupGoldCost} 金币！", MsgColor.Red, MsgType.Hint);
                    return;
                }
                player.GoldChanged();
                costDesc = $"{SoloGlobalConfig.CampSetupGoldCost} 金币";
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            profile.CampMapName = mapName;
            profile.CampMapDesc = mapDesc;
            profile.CampX = player.CurrX;
            profile.CampY = player.CurrY;
            SoloProfileStore.MarkDirty(profile, saveImmediately: true);

            player.SysMsg($"【玛法行军帐】成功在 {mapDesc}({player.CurrX}:{player.CurrY}) 扎下营帐（消耗 {costDesc}）！回城补给后可在安全区直接返场。", MsgColor.Green, MsgType.Hint);
            OpenCampDialog(player);
        }

        /// <summary>
        /// 从城镇安全区传送回已记录的前线营地
        /// </summary>
        public static void ReturnToCamp(PlayObject player)
        {
            if (player?.Envir == null)
            {
                return;
            }

            if (!player.InSafeZone())
            {
                player.SysMsg("【玛法行军帐】只能在城镇安全区内拔营返场！野外脱困请使用回城卷或地牢逃脱卷。", MsgColor.Red, MsgType.Hint);
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            if (string.IsNullOrEmpty(profile.CampMapName))
            {
                player.SysMsg("【玛法行军帐】你尚未在野外设立任何前线营地！", MsgColor.Red, MsgType.Hint);
                return;
            }

            var targetMap = SystemShare.MapMgr.FindMap(profile.CampMapName);
            if (targetMap == null)
            {
                player.SysMsg("【玛法行军帐】目标营地所在地图已失效，请重新设立营地！", MsgColor.Red, MsgType.Hint);
                return;
            }

            player.SpaceMove(profile.CampMapName, profile.CampX, profile.CampY, 0);
            player.SysMsg($"【玛法行军帐】已将你送达前线营地：{profile.CampMapDesc}({profile.CampX}:{profile.CampY})！", MsgColor.Green, MsgType.Hint);
        }

        /// <summary>
        /// 打开【玛法军需官】交互面板
        /// </summary>
        public static void OpenQuartermasterDialog(PlayObject player)
        {
            if (player == null)
            {
                return;
            }

            int repairCost = EstimateTotalSpecialRepairCost(player, out int damagedCount);
            EstimateQuickSellBag(player, out int sellableCount, out int protectedCount, out int estimatedGold);

            string body =
                $"欢迎来到玛法军需处，<{player.ChrName}>！我可为你快速处理战利品与修缮全身甲胄。\\" +
                $"【极品防误卖铁律】任何带有 <+1 及以上极品属性/FCOLOR=251>、沃玛/祖玛/赤月名牌、纯度>=15黑铁矿均会被自动锁定跳过！\\" +
                "------------------------------------------------\\" +
                $"当前包袱可统卖普通装备：<{sellableCount} 件/FCOLOR=254>（预计收入 <{estimatedGold} 金币/FCOLOR=251>，自动锁定保护 <{protectedCount} 件/FCOLOR=250>）\\" +
                $"当前穿戴需特修装备：<{damagedCount} 件/FCOLOR=254>（预计特修费用 <{repairCost} 金币/FCOLOR=251>，零持久上限损耗）\\" +
                "------------------------------------------------\\" +
                "<【一键统卖】跨品类出售包内普通白板装备/@Solo_QuickSell>\\" +
                "<【一键特修】全身穿戴装备无损特殊修理/@Solo_QuickRepair>\\" +
                "<【补给领取】补领玛法行军帐与猎魔手札/@Solo_ClaimStarterKit>    <关闭/@exit>";

            SendNativeNpcDialog(player, SoloGlobalConfig.QuartermasterNpcName, body);
        }

        /// <summary>
        /// 执行一键跨品类统卖（严格锁死任何 >= +1 极品与名牌装备）
        /// </summary>
        public static void ExecuteQuickSell(PlayObject player)
        {
            if (player?.ItemList == null)
            {
                return;
            }

            if (player.ItemList.Count == 0)
            {
                player.SysMsg("【玛法军需官】你的包袱空空如也，没有可出售的装备。", MsgColor.Red, MsgType.Hint);
                return;
            }

            IList<DeleteItem> delList = new List<DeleteItem>();
            int soldCount = 0;
            int protectedCount = 0;
            int totalGoldEarned = 0;

            for (int i = player.ItemList.Count - 1; i >= 0; i--)
            {
                UserItem userItem = player.ItemList[i];
                if (userItem == null || userItem.Index <= 0)
                {
                    continue;
                }

                StdItem stdItem = SystemShare.ItemSystem.GetStdItem(userItem.Index);
                if (stdItem == null)
                {
                    continue;
                }

                if (!IsVendorSellableWhiteEquipment(userItem, stdItem, out bool isValuableProtected))
                {
                    if (isValuableProtected)
                    {
                        protectedCount++;
                    }
                    continue;
                }

                int sellPrice = CalculateSellPrice(userItem, stdItem);
                if (sellPrice <= 0)
                {
                    continue;
                }

                if (player.Gold + totalGoldEarned + sellPrice > SystemShare.Config.HumanMaxGold)
                {
                    player.SysMsg("【玛法军需官】你的金币即将达到携带上限，已停止继续出售！", MsgColor.Red, MsgType.Hint);
                    break;
                }

                totalGoldEarned += sellPrice;
                soldCount++;

                delList.Add(new DeleteItem
                {
                    ItemName = stdItem.Name,
                    MakeIndex = userItem.MakeIndex
                });

                player.ItemList.RemoveAt(i);
            }

            if (soldCount > 0)
            {
                player.IncGold(totalGoldEarned);
                player.GoldChanged();
                RefreshBagWeight(player);

                int objectId = HUtil32.Sequence();
                SystemShare.ActorMgr.AddOhter(objectId, delList);
                player.SendMsg(Messages.RM_SENDDELITEMLIST, 0, objectId, 0, 0);

                player.SysMsg(
                    $"【军需统卖】成功出售普通装备 {soldCount} 件，获得 {totalGoldEarned} 金币！（已自动锁定保护 {protectedCount} 件极品/名牌物品）",
                    MsgColor.Green, MsgType.Hint);
            }
            else
            {
                player.SysMsg(
                    $"【军需统卖】包袱内没有可统卖的普通白板装备（已自动保护 {protectedCount} 件极品/名牌物品）。",
                    MsgColor.Green, MsgType.Hint);
            }
        }

        /// <summary>
        /// 执行全身穿戴装备一键无损特修
        /// </summary>
        public static void ExecuteQuickRepair(PlayObject player)
        {
            if (player?.UseItems == null)
            {
                return;
            }

            int totalCost = EstimateTotalSpecialRepairCost(player, out int damagedCount);
            if (damagedCount == 0)
            {
                player.SysMsg("【玛法军需官】你全身穿戴的装备持久均已满值，无需修理！", MsgColor.Green, MsgType.Hint);
                return;
            }

            if (totalCost > 0 && (player.Gold < totalCost || !player.DecGold(totalCost)))
            {
                player.SysMsg($"【玛法军需官】全身特修需要 {totalCost} 金币，你的金币不足！", MsgColor.Red, MsgType.Hint);
                return;
            }

            int repairedCount = 0;
            for (int i = 0; i < player.UseItems.Length; i++)
            {
                UserItem item = player.UseItems[i];
                if (item == null || item.Index <= 0 || item.Dura >= item.DuraMax || item.DuraMax <= 0)
                {
                    continue;
                }

                StdItem stdItem = SystemShare.ItemSystem.GetStdItem(item.Index);
                if (stdItem == null || !IsRepairableStdMode(stdItem.StdMode))
                {
                    continue;
                }

                item.Dura = item.DuraMax;
                player.SendUpdateItem(item);
                player.SendMsg(player, Messages.RM_DURACHANGE, (short)i, item.Dura, item.DuraMax, 0);
                repairedCount++;
            }

            if (totalCost > 0)
            {
                player.GoldChanged();
            }

            player.SysMsg($"【军需特修】已为你无损特修全身 {repairedCount} 件穿戴装备，共消耗 {totalCost} 金币！", MsgColor.Green, MsgType.Hint);
        }

        /// <summary>
        /// 发放或补领【玛法行军帐】与【玛法猎魔手札】
        /// </summary>
        public static void EnsureStarterKit(PlayObject player, bool forceCheckBag = false)
        {
            if (player == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            if (profile.StarterKitGranted && !forceCheckBag)
            {
                return;
            }

            bool grantedAny = false;
            if (player.CheckItems(SoloGlobalConfig.CampItemName) == null && player.IsEnoughBag())
            {
                UserItem campItem = null;
                if (SystemShare.ItemSystem.CopyToUserItemFromName(SoloGlobalConfig.CampItemName, ref campItem) && campItem != null)
                {
                    player.AddItemToBag(campItem);
                    player.SendAddItem(campItem);
                    grantedAny = true;
                }
            }

            if (player.CheckItems(SoloGlobalConfig.HunterTomeItemName) == null && player.IsEnoughBag())
            {
                UserItem tomeItem = null;
                if (SystemShare.ItemSystem.CopyToUserItemFromName(SoloGlobalConfig.HunterTomeItemName, ref tomeItem) && tomeItem != null)
                {
                    player.AddItemToBag(tomeItem);
                    player.SendAddItem(tomeItem);
                    grantedAny = true;
                }
            }

            if (!profile.StarterKitGranted)
            {
                profile.StarterKitGranted = true;
                SoloProfileStore.MarkDirty(profile, saveImmediately: true);
            }

            if (grantedAny)
            {
                player.SysMsg("【玛法军需处】你已获得【玛法行军帐】与【玛法猎魔手札】（也可随时使用 @camp、@tome、@sell、@repair 快捷指令）！", MsgColor.Green, MsgType.Hint);
            }
            else if (forceCheckBag)
            {
                player.SysMsg("【玛法军需处】你的包袱中已有行军帐与猎魔手札（若DB尚未导入该物品，可直接输入 @camp 与 @tome 打开面板）！", MsgColor.Green, MsgType.Hint);
            }
        }

        private static bool IsVendorSellableWhiteEquipment(UserItem userItem, StdItem stdItem, out bool isValuableProtected)
        {
            isValuableProtected = false;

            // 1. 严禁出售带有任何极品点数的装备
            if (CombatQoLModule.HasBonusStats(userItem))
            {
                isValuableProtected = true;
                return false;
            }

            // 2. 严禁出售名牌装备与保护名单物品
            if (SoloGlobalConfig.ProtectedFamousItems.Contains(stdItem.Name))
            {
                isValuableProtected = true;
                return false;
            }

            // 3. 矿石类（StdMode == 43）：仅出售纯度 < 15 的低纯度矿石，纯度 >= 15 自动保护
            if (stdItem.StdMode == 43)
            {
                if (userItem.Dura >= 15000 && stdItem.Name.Contains("黑铁"))
                {
                    isValuableProtected = true;
                    return false;
                }
                return true;
            }

            // 4. 仅出售常规武器/衣服/头盔/项链/手镯/戒指/靴子/腰带白板装备
            bool isStandardGear = stdItem.StdMode is 5 or 6 or 10 or 11 or 15 or 19 or 20 or 21 or 22 or 23 or 24 or 26 or 52 or 54 or 62 or 64;
            if (!isStandardGear)
            {
                return false;
            }

            // 需要鉴定的未知装备或禁止出售标记跳过
            if ((stdItem.ItemDesc & 2) != 0)
            {
                isValuableProtected = true;
                return false;
            }

            return true;
        }

        private static int CalculateSellPrice(UserItem userItem, StdItem stdItem)
        {
            if (stdItem.Price <= 0)
            {
                return 0;
            }

            double duraFactor = 1.0;
            if (userItem.DuraMax > 0)
            {
                duraFactor = Math.Clamp((double)userItem.Dura / userItem.DuraMax, 0.25, 1.0);
            }

            return Math.Max(1, HUtil32.Round(stdItem.Price * 0.5 * duraFactor));
        }

        private static void EstimateQuickSellBag(PlayObject player, out int sellableCount, out int protectedCount, out int estimatedGold)
        {
            sellableCount = 0;
            protectedCount = 0;
            estimatedGold = 0;

            if (player?.ItemList == null)
            {
                return;
            }

            for (int i = 0; i < player.ItemList.Count; i++)
            {
                UserItem userItem = player.ItemList[i];
                if (userItem == null || userItem.Index <= 0)
                {
                    continue;
                }

                StdItem stdItem = SystemShare.ItemSystem.GetStdItem(userItem.Index);
                if (stdItem == null)
                {
                    continue;
                }

                if (IsVendorSellableWhiteEquipment(userItem, stdItem, out bool isProtected))
                {
                    sellableCount++;
                    estimatedGold += CalculateSellPrice(userItem, stdItem);
                }
                else if (isProtected)
                {
                    protectedCount++;
                }
            }
        }

        private static int EstimateTotalSpecialRepairCost(PlayObject player, out int damagedCount)
        {
            damagedCount = 0;
            int totalCost = 0;

            if (player?.UseItems == null)
            {
                return 0;
            }

            for (int i = 0; i < player.UseItems.Length; i++)
            {
                UserItem item = player.UseItems[i];
                if (item == null || item.Index <= 0 || item.Dura >= item.DuraMax || item.DuraMax <= 0)
                {
                    continue;
                }

                StdItem stdItem = SystemShare.ItemSystem.GetStdItem(item.Index);
                if (stdItem == null || !IsRepairableStdMode(stdItem.StdMode))
                {
                    continue;
                }

                damagedCount++;
                double missingRatio = (double)(item.DuraMax - item.Dura) / item.DuraMax;
                int itemCost = Math.Max(10, HUtil32.Round(stdItem.Price * 0.35 * missingRatio));
                totalCost += itemCost;
            }

            return totalCost;
        }

        private static bool IsRepairableStdMode(byte stdMode)
        {
            return stdMode is 5 or 6 or 10 or 11 or 15 or 19 or 20 or 21 or 22 or 23 or 24 or 26 or 52 or 54 or 62 or 64;
        }

        private static bool TryConsumeBagItem(PlayObject player, string itemName)
        {
            if (player?.ItemList == null)
            {
                return false;
            }

            for (int i = player.ItemList.Count - 1; i >= 0; i--)
            {
                UserItem item = player.ItemList[i];
                if (item == null || item.Index <= 0)
                {
                    continue;
                }

                string stdName = SystemShare.ItemSystem.GetStdItemName(item.Index);
                if (string.Equals(stdName, itemName, StringComparison.OrdinalIgnoreCase))
                {
                    player.SendDelItems(item);
                    player.ItemList.RemoveAt(i);
                    RefreshBagWeight(player);
                    return true;
                }
            }

            return false;
        }

        private static void RefreshBagWeight(PlayObject player)
        {
            if (player?.ItemList == null)
            {
                return;
            }

            ushort totalWeight = 0;
            for (int i = 0; i < player.ItemList.Count; i++)
            {
                UserItem item = player.ItemList[i];
                if (item != null && item.Index > 0)
                {
                    StdItem stdItem = SystemShare.ItemSystem.GetStdItem(item.Index);
                    if (stdItem != null)
                    {
                        totalWeight += stdItem.Weight;
                    }
                }
            }

            player.WAbil.Weight = totalWeight;
            player.SendMsg(Messages.RM_WEIGHTCHANGED, 0, 0, 0, 0);
        }
    }
}
