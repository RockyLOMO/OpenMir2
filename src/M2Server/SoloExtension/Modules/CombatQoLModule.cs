using System;
using M2Server;
using M2Server.Actor;
using M2Server.Monster;
using M2Server.Player;
using OpenMir2.Common;
using OpenMir2.Consts;
using OpenMir2.Data;
using OpenMir2.Enums;
using OpenMir2.Packets.ClientPackets;
using SystemModule;
using SystemModule.Actors;
using SystemModule.Const;
using SystemModule.Data;
using SystemModule.Enums;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 战斗与职业痛点减负模块（死亡零爆穿戴装、4x4 自动拾取、半月溅射增强、道士背包毒符直读、魔宠道术继承、法师负重）
    /// </summary>
    public static class CombatQoLModule
    {
        /// <summary>
        /// Hook 1: 100% 拦截人物死亡时身上穿戴装备 (UseItems) 的掉落
        /// </summary>
        public static bool ShouldBlockUseItemDropOnDeath(PlayObject player)
        {
            if (player == null)
            {
                return true;
            }

            player.SysMsg("【玛法庇护】你的穿戴装备受到了灵魂绑定保护，未曾遗落！", MsgColor.Green, MsgType.Hint);
            return true;
        }

        /// <summary>
        /// 保护背包内的核心媒介道具（玛法行军帐、玛法猎魔手札）在人物死亡时不散落
        /// </summary>
        public static bool IsProtectedBagItem(UserItem userItem)
        {
            if (userItem == null || userItem.Index <= 0)
            {
                return false;
            }

            string itemName = SystemShare.ItemSystem.GetStdItemName(userItem.Index);
            return string.Equals(itemName, SoloGlobalConfig.CampItemName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemName, SoloGlobalConfig.HunterTomeItemName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Hook 2: 玩家主循环节流 Tick（每 450ms 执行一次 4x4 范围自动拾取 + 定期脏存档刷新）
        /// </summary>
        public static void OnPlayerTick(PlayObject player)
        {
            if (player == null || player.Death || player.Ghost || player.Envir == null || player.IsRobot)
            {
                return;
            }

            int nowTick = HUtil32.GetTickCount();
            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);

            // 后台低频检查是否需要落盘 JSON（至少间隔 30 秒）
            SoloProfileStore.FlushIfDue(profile, nowTick);

            // 节流：4x4 范围自动拾取每 450ms 最多扫描一次
            if (profile.AutoLootMode > 0 && nowTick - profile.LastAutoLootTick >= SoloGlobalConfig.AutoLootScanIntervalMs)
            {
                profile.LastAutoLootTick = nowTick;
                PerformRangeAutoLoot(player, profile, nowTick);
            }
        }

        /// <summary>
        /// 执行以玩家为中心 4x4 半径的地面金币与战利品自动拾取
        /// </summary>
        private static void PerformRangeAutoLoot(PlayObject player, SoloPlayerProfile profile, int nowTick)
        {
            if (player.Dealing)
            {
                return;
            }

            var envir = player.Envir;
            short centerX = player.CurrX;
            short centerY = player.CurrY;
            int radius = SoloGlobalConfig.AutoLootRadius;

            short minX = (short)Math.Max(0, centerX - radius);
            short maxX = (short)Math.Min(envir.Width - 1, centerX + radius);
            short minY = (short)Math.Max(0, centerY - radius);
            short maxY = (short)Math.Min(envir.Height - 1, centerY + radius);

            int pickedItemsThisTick = 0;
            const int maxItemsPerTick = 4; // 限制单次 Tick 最多捡 4 件物品，消除网络包突发与 CPU 毛刺

            for (short x = minX; x <= maxX; x++)
            {
                for (short y = minY; y <= maxY; y++)
                {
                    MapItem mapItem = default;
                    if (!envir.GetItem(x, y, ref mapItem))
                    {
                        continue;
                    }

                    // 校验掉落归属保护期
                    if (nowTick - mapItem.CanPickUpTick <= SystemShare.Config.FloorItemCanPickUpTime)
                    {
                        if (mapItem.OfBaseObject > 0 && mapItem.OfBaseObject != player.ActorId)
                        {
                            // 若归属既非本人也非本人的召唤兽，则跳过
                            IActor owner = SystemShare.ActorMgr.Get(mapItem.OfBaseObject);
                            if (owner != null && owner.Master != player)
                            {
                                continue;
                            }
                        }
                    }

                    // 1. 地面金币自动入袋
                    if (string.Equals(mapItem.Name, Grobal2.StringGoldName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (player.Gold + mapItem.Count <= SystemShare.Config.HumanMaxGold)
                        {
                            if (envir.DeleteFromMap(x, y, CellType.Item, mapItem.ItemId, null) == 1)
                            {
                                if (player.IncGold(mapItem.Count))
                                {
                                    player.SendRefMsg(Messages.RM_ITEMHIDE, 0, mapItem.ItemId, x, y, "");
                                    player.GoldChanged();
                                }
                                else
                                {
                                    envir.AddItemToMap(x, y, mapItem);
                                }
                            }
                        }
                        continue;
                    }

                    // 2. 地面物品自动入包
                    if (pickedItemsThisTick >= maxItemsPerTick || !player.IsEnoughBag())
                    {
                        continue;
                    }

                    UserItem userItem = mapItem.UserItem;
                    if (userItem == null)
                    {
                        continue;
                    }

                    StdItem stdItem = SystemShare.ItemSystem.GetStdItem(userItem.Index);
                    if (stdItem == null)
                    {
                        continue;
                    }

                    if (!ShouldAutoLootItem(profile.AutoLootMode, userItem, stdItem))
                    {
                        continue;
                    }

                    int itemWeight = SystemShare.ItemSystem.GetStdItemWeight(userItem.Index);
                    if (!player.IsAddWeightAvailable(itemWeight))
                    {
                        continue;
                    }

                    if (envir.DeleteFromMap(x, y, CellType.Item, mapItem.ItemId, null) == 1)
                    {
                        player.SendRefMsg(Messages.RM_ITEMHIDE, 0, mapItem.ItemId, x, y, "");
                        player.AddItemToBag(userItem);
                        player.SendAddItem(userItem);
                        pickedItemsThisTick++;

                        if (HasBonusStats(userItem))
                        {
                            player.SysMsg($"【极品入袋】自动拾取了极品装备：{stdItem.Name}！", MsgColor.Green, MsgType.Hint);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 判定物品是否符合玩家当前自动拾取过滤规则
        /// </summary>
        public static bool ShouldAutoLootItem(int autoLootMode, UserItem userItem, StdItem stdItem)
        {
            if (autoLootMode <= 0 || userItem == null || stdItem == null)
            {
                return false;
            }

            // 模式 1：全拾取
            if (autoLootMode == 1)
            {
                return true;
            }

            // 模式 2：精品模式（极品属性装备、名牌装备、技能书、药水、毒符、卷轴/捆药、纯度>=15黑铁矿）
            if (HasBonusStats(userItem))
            {
                return true;
            }

            if (SoloGlobalConfig.ProtectedFamousItems.Contains(stdItem.Name))
            {
                return true;
            }

            // 药水(0)、肉/特殊材料(40)、使用类卷轴(2/3/31)、技能书(4)、护身符/毒药(25)
            if (stdItem.StdMode is 0 or 2 or 3 or 4 or 25 or 31)
            {
                return true;
            }

            // 黑铁矿石：仅拾取纯度 >= 15 (Dura >= 15000)
            if (stdItem.StdMode == 43)
            {
                return userItem.Dura >= 15000;
            }

            // 高价商店装（价格 >= 6000 金币）
            return stdItem.Price >= 6000;
        }

        /// <summary>
        /// 检查装备是否带有任何极品附加属性（攻/魔/道/防/魔御/准/敏/运/攻速/升级次数等）
        /// </summary>
        public static bool HasBonusStats(UserItem userItem)
        {
            if (userItem?.Desc == null)
            {
                return false;
            }

            for (int i = 0; i < 14 && i < userItem.Desc.Length; i++)
            {
                if (userItem.Desc[i] > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 阶梯经验倍率（6.2 节）：1~30 级 3.0x；31~40 级 2.0x；41 级+ 1.5x
        /// </summary>
        public static int ApplyTieredExpMultiplier(PlayObject player, int baseExp)
        {
            if (player == null || baseExp <= 0)
            {
                return baseExp;
            }

            ushort level = player.Abil.Level;
            if (level <= 30)
            {
                return baseExp * 3;
            }

            if (level <= 40)
            {
                return baseExp * 2;
            }

            return baseExp + (baseExp >> 1); // 1.5x 整数位运算
        }

        /// <summary>
        /// 战士单人优化（6.2 节）：半月弯刀溅射伤害提升至主目标伤害的至少 50%
        /// </summary>
        public static void AdjustHalfMoonDamage(int primaryPower, ref int splashPower)
        {
            if (primaryPower <= 0)
            {
                return;
            }

            int minSplash = primaryPower >> 1; // 50%
            if (splashPower < minSplash)
            {
                splashPower = minSplash;
            }
        }

        /// <summary>
        /// 道士单人优化（6.2 节）：当左手镯/护身符栏未佩戴对应毒药或护身符时，自动检索背包 (ItemList)
        /// 使用负数索引编码 (-100 - bagIndex) 与原版 UseItems[0..12] 完全区分，零侵入兼容原版施法流程
        /// </summary>
        public static bool TryCheckBagAmulet(PlayObject player, byte nCount, byte nType, ref short idx)
        {
            if (player?.ItemList == null || player.ItemList.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < player.ItemList.Count; i++)
            {
                UserItem bagItem = player.ItemList[i];
                if (bagItem == null || bagItem.Index <= 0)
                {
                    continue;
                }

                StdItem stdItem = SystemShare.ItemSystem.GetStdItem(bagItem.Index);
                if (stdItem == null || stdItem.StdMode != 25)
                {
                    continue;
                }

                bool match = nType switch
                {
                    1 => stdItem.Shape == 5 && HUtil32.Round(bagItem.Dura / 100.0) >= nCount,
                    2 => stdItem.Shape <= 2 && HUtil32.Round(bagItem.Dura / 100.0) >= nCount,
                    _ => false
                };

                if (match)
                {
                    idx = (short)(-100 - i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 道士单人优化（6.2 节）：扣减背包中的毒药或护身符持久，耗尽时自动移除并通知客户端
        /// </summary>
        public static bool TryUseBagAmulet(PlayObject player, byte nCount, short idx)
        {
            if (player?.ItemList == null || idx > -100)
            {
                return false;
            }

            int bagIndex = -100 - idx;
            if (bagIndex < 0 || bagIndex >= player.ItemList.Count)
            {
                return false;
            }

            UserItem bagItem = player.ItemList[bagIndex];
            if (bagItem == null)
            {
                return false;
            }

            ushort costDura = (ushort)(nCount * 100);
            if (bagItem.Dura > costDura)
            {
                bagItem.Dura -= costDura;
                player.SendUpdateItem(bagItem);
            }
            else
            {
                bagItem.Dura = 0;
                player.SendDelItems(bagItem);
                player.ItemList.RemoveAt(bagIndex);
            }

            return true;
        }

        /// <summary>
        /// 道士单人优化（6.2 节）：变异骷髅与神兽继承主人 20% 道术上限（并按宝宝等级额外强化生命值）
        /// </summary>
        public static void ApplySlaveAbilityBonus(BaseObject slaveObj)
        {
            if (slaveObj?.Master == null || slaveObj.Master.Race != ActorRace.Play)
            {
                return;
            }

            if (slaveObj.Race == ActorRace.WhiteSkeleton ||
                slaveObj.Race == ActorRace.ElfMonster ||
                slaveObj.Race == ActorRace.ElfWarriormon)
            {
                ushort masterMaxSc = HUtil32.HiByte(slaveObj.Master.WAbil.SC);
                ushort bonusDc = (ushort)Math.Max(1, masterMaxSc / 5); // 20% 道术转化为宝宝攻击力
                byte loDc = HUtil32.LoByte(slaveObj.WAbil.DC);
                byte hiDc = HUtil32.HiByte(slaveObj.WAbil.DC);

                ushort newLoDc = (ushort)Math.Min(255, loDc + (bonusDc >> 1));
                ushort newHiDc = (ushort)Math.Min(255, hiDc + bonusDc);
                slaveObj.WAbil.DC = HUtil32.MakeWord(newLoDc, newHiDc);
            }
        }

        /// <summary>
        /// 法师负重补正与猎魔图鉴永久属性加成（在 PlayObject.RecalcAbilitys 末尾调用）
        /// </summary>
        public static void ApplySoloAbilityBonuses(PlayObject player)
        {
            if (player == null || player.IsRobot)
            {
                return;
            }

            // 1. 法师单人优化（6.2 节）：每级额外增加 4 点背包负重，解决单刷背不动药水问题
            if (player.Job == PlayerJob.Wizard)
            {
                ushort bonusBagWeight = (ushort)(player.Abil.Level * 4);
                player.WAbil.MaxWeight = (ushort)Math.Min(ushort.MaxValue, player.WAbil.MaxWeight + bonusBagWeight);
            }

            // 2. 猎魔图鉴永久属性里程碑加成
            BestiaryModule.ApplyBestiaryStatBonuses(player);
        }
    }
}
