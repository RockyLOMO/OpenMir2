using System;
using M2Server.Actor;
using M2Server.Npc;
using M2Server.Player;
using OpenMir2.Data;
using OpenMir2.Enums;
using OpenMir2.Packets.ClientPackets;
using SystemModule;
using SystemModule.Actors;
using SystemModule.Enums;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// OpenMir2 1.76 单人养老扩展统一静态门面 (SoloExtensionHost)
    /// 所有对原版 M2Server 引擎的调用均收敛为此类的单行 Hook，保证极低耦合与零性能退化
    /// </summary>
    public static class SoloExtensionHost
    {
        // ====================================================================
        // Hook 1: 死亡装备保护 (PlayObject.DropUseItems & ScatterBagItems)
        // ====================================================================
        public static bool ShouldBlockUseItemDropOnDeath(PlayObject player)
            => CombatQoLModule.ShouldBlockUseItemDropOnDeath(player);

        public static bool IsProtectedBagItem(UserItem userItem)
            => CombatQoLModule.IsProtectedBagItem(userItem);

        // ====================================================================
        // Hook 2: 玩家主循环节流 Tick (PlayObject.Run -> 4x4自动拾取)
        // ====================================================================
        public static void OnPlayerTick(PlayObject player)
            => CombatQoLModule.OnPlayerTick(player);

        // ====================================================================
        // Hook 3: NPC 点击与脚本标签命令拦截 (ClientMerchantDlgSelect & NormNpc)
        // ====================================================================
        public static bool TryHandleNpcClick(NormNpc npc, IPlayerActor playerActor)
        {
            if (npc == null || playerActor is not PlayObject player)
            {
                return false;
            }

            string npcName = npc.ChrName ?? string.Empty;
            if (npcName.Contains(SoloGlobalConfig.QuartermasterNpcName, StringComparison.OrdinalIgnoreCase) ||
                npcName.Contains("军需官", StringComparison.OrdinalIgnoreCase))
            {
                TownServiceModule.OpenQuartermasterDialog(player);
                return true;
            }

            if (npcName.Contains(SoloGlobalConfig.BeastmasterNpcName, StringComparison.OrdinalIgnoreCase) ||
                npcName.Contains("驯兽师", StringComparison.OrdinalIgnoreCase))
            {
                PetPersistenceModule.OpenBeastmasterDialog(player);
                return true;
            }

            return false;
        }

        public static bool TryHandleNpcCommand(PlayObject player, int npcActorId, string command)
        {
            if (player == null || string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string cmd = command.Trim();
            if (!cmd.StartsWith('@'))
            {
                return false;
            }

            // 截取首个换行或空白前的标签
            int endIdx = cmd.IndexOfAny(new[] { '\r', '\n', ' ', '\t' });
            if (endIdx > 0)
            {
                cmd = cmd[..endIdx];
            }

            switch (cmd.ToLowerInvariant())
            {
                // 行军帐指令
                case "@camp_set":
                    TownServiceModule.SetCampAnchor(player);
                    return true;
                case "@camp_return":
                    TownServiceModule.ReturnToCamp(player);
                    return true;

                // 军需官指令
                case "@solo_openquartermaster":
                    TownServiceModule.OpenQuartermasterDialog(player);
                    return true;
                case "@solo_quicksell":
                    TownServiceModule.ExecuteQuickSell(player);
                    return true;
                case "@solo_quickrepair":
                    TownServiceModule.ExecuteQuickRepair(player);
                    return true;
                case "@solo_claimstarterkit":
                    TownServiceModule.EnsureStarterKit(player, forceCheckBag: true);
                    return true;

                // 猎魔手札指令
                case "@hunter_home":
                    BestiaryModule.OpenHunterTomeDialog(player);
                    return true;
                case "@hunter_toggleloot":
                    BestiaryModule.CycleAutoLootMode(player, reopenDialog: true);
                    return true;
                case "@hunter_scanboss":
                    BestiaryModule.ScanCurrentMapBosses(player, showDialog: true);
                    return true;
                case "@hunter_bestiary":
                    BestiaryModule.OpenBestiaryDetailDialog(player);
                    return true;
                case "@hunter_openpets":
                    PetPersistenceModule.OpenBeastmasterDialog(player);
                    return true;
                case "@hunter_opencamp":
                    TownServiceModule.OpenCampDialog(player);
                    return true;

                // 皇家驯兽师指令
                case "@pet_recall":
                    PetPersistenceModule.RecallSavedPets(player);
                    return true;
                case "@pet_savenow":
                    int count = PetPersistenceModule.SaveActivePets(player, notifyPlayer: true);
                    if (count == 0)
                    {
                        player.SysMsg("【皇家驯兽师】你身边当前没有可寄存的诱惑灵兽（道士骷髅与神兽无需寄存）！", MsgColor.Red, MsgType.Hint);
                    }
                    PetPersistenceModule.OpenBeastmasterDialog(player);
                    return true;

                default:
                    return false;
            }
        }

        // ====================================================================
        // Hook 4: 双击使用自定义媒介物品 (StdMode == 31: 玛法行军帐 / 玛法猎魔手札)
        // ====================================================================
        public static bool TryHandleCustomItemUse(PlayObject player, UserItem userItem, StdItem stdItem)
        {
            if (player == null || userItem == null)
            {
                return false;
            }

            stdItem ??= SystemShare.ItemSystem.GetStdItem(userItem.Index);
            if (stdItem == null)
            {
                return false;
            }

            if (string.Equals(stdItem.Name, SoloGlobalConfig.CampItemName, StringComparison.OrdinalIgnoreCase))
            {
                TownServiceModule.OpenCampDialog(player);
                return true;
            }

            if (string.Equals(stdItem.Name, SoloGlobalConfig.HunterTomeItemName, StringComparison.OrdinalIgnoreCase))
            {
                BestiaryModule.OpenHunterTomeDialog(player);
                return true;
            }

            return false;
        }

        // ====================================================================
        // Hook 5, 6, 7: 怪物生成染色、受击亲卫/禁回血、死亡惊扰度与专属掉落
        // ====================================================================
        public static void OnMonsterSpawned(AnimalObject monster)
            => EcologyModule.OnMonsterSpawned(monster);

        public static void OnMonsterDamaged(AnimalObject monster, IActor hitter)
            => EcologyModule.OnMonsterDamaged(monster, hitter);

        public static bool IsRegenDisabled(BaseObject actor)
            => EcologyModule.IsRegenDisabled(actor);

        public static void OnMonsterKilled(AnimalObject monster, IActor killer)
            => EcologyModule.OnMonsterKilled(monster, killer);

        // ====================================================================
        // Hook 8: 玩家上下线旁路存档、三职业痛点微调与快捷 @ 指令兜底
        // ====================================================================
        public static void OnPlayerLogin(PlayObject player)
        {
            if (player == null || player.IsRobot)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            TownServiceModule.EnsureStarterKit(player, forceCheckBag: false);
            player.RecalcAbilitys();

            int savedPetCount = profile.SavedPets?.Count ?? 0;
            player.SysMsg(
                $"【单人养老服】欢迎归来，{player.ChrName}！穿戴装备零掉落、4x4自动拾取与动态精英生态已激活（输入 @tome 或 @camp 可随时呼出面板）。",
                MsgColor.Green, MsgType.Hint);

            if (savedPetCount > 0)
            {
                player.SysMsg(
                    $"【皇家驯兽师】你在兽栏中寄存有 {savedPetCount} 只契约灵兽，可前往皇家驯兽师或输入 @pet 一键领回！",
                    MsgColor.Green, MsgType.Hint);
            }
        }

        public static void OnPlayerLogout(PlayObject player)
        {
            if (player == null || player.IsRobot)
            {
                return;
            }

            PetPersistenceModule.SaveActivePets(player, notifyPlayer: false);
            SoloProfileStore.SaveAndUnload(player.ChrName);
        }

        public static int ApplyTieredExpMultiplier(PlayObject player, int baseExp)
            => CombatQoLModule.ApplyTieredExpMultiplier(player, baseExp);

        public static void AdjustHalfMoonDamage(int primaryPower, ref int splashPower)
            => CombatQoLModule.AdjustHalfMoonDamage(primaryPower, ref splashPower);

        public static bool TryCheckBagAmulet(PlayObject player, byte nCount, byte nType, ref short idx)
            => CombatQoLModule.TryCheckBagAmulet(player, nCount, nType, ref idx);

        public static bool TryUseBagAmulet(PlayObject player, byte nCount, short idx)
            => CombatQoLModule.TryUseBagAmulet(player, nCount, idx);

        public static void ApplySlaveAbilityBonus(BaseObject slaveObj)
            => CombatQoLModule.ApplySlaveAbilityBonus(slaveObj);

        public static void ApplySoloAbilityBonuses(PlayObject player)
            => CombatQoLModule.ApplySoloAbilityBonuses(player);

        /// <summary>
        /// 聊天栏快捷命令兜底（@camp / @sell / @repair / @autoloot / @boss / @pet / @tome）
        /// </summary>
        public static bool TryExecuteCommand(PlayObject player, string rawInput)
        {
            if (player == null || string.IsNullOrWhiteSpace(rawInput) || !rawInput.StartsWith('@'))
            {
                return false;
            }

            string[] parts = rawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

            switch (cmd)
            {
                case "@camp":
                case "@行军帐":
                    if (arg == "set")
                    {
                        TownServiceModule.SetCampAnchor(player);
                    }
                    else if (arg is "back" or "return")
                    {
                        TownServiceModule.ReturnToCamp(player);
                    }
                    else
                    {
                        TownServiceModule.OpenCampDialog(player);
                    }
                    return true;

                case "@sell":
                case "@统卖":
                    TownServiceModule.ExecuteQuickSell(player);
                    return true;

                case "@repair":
                case "@特修":
                    TownServiceModule.ExecuteQuickRepair(player);
                    return true;

                case "@autoloot":
                case "@拾取":
                    BestiaryModule.CycleAutoLootMode(player, reopenDialog: false);
                    return true;

                case "@boss":
                case "@感知":
                    BestiaryModule.ScanCurrentMapBosses(player, showDialog: true);
                    return true;

                case "@pet":
                case "@灵兽":
                    if (arg == "recall")
                    {
                        PetPersistenceModule.RecallSavedPets(player);
                    }
                    else
                    {
                        PetPersistenceModule.OpenBeastmasterDialog(player);
                    }
                    return true;

                case "@tome":
                case "@手札":
                case "@猎魔":
                    BestiaryModule.OpenHunterTomeDialog(player);
                    return true;

                case "@quartermaster":
                case "@军需":
                    TownServiceModule.OpenQuartermasterDialog(player);
                    return true;

                default:
                    return false;
            }
        }
    }
}
