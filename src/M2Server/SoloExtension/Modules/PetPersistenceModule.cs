using System;
using System.Collections.Generic;
using M2Server.Monster;
using M2Server.Player;
using OpenMir2.Enums;
using SystemModule.Actors;
using SystemModule.Enums;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 法师契约灵兽离线持久化与【皇家驯兽师】一键召回模块
    /// 解决单人法师每次下线重登都要花 40 分钟重新招 5 只虎卫/巨型多角虫练 7 级的痛点
    /// </summary>
    public static class PetPersistenceModule
    {
        /// <summary>
        /// 在玩家下线 (MakeGhost) 或主动点击驯兽师寄存时，将当前存活的诱惑魔宠写入 Solo_{Name}.json
        /// </summary>
        public static int SaveActivePets(PlayObject player, bool notifyPlayer = false)
        {
            if (player == null || player.IsRobot)
            {
                return 0;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            if (player.SlaveList == null || player.SlaveList.Count == 0)
            {
                return 0;
            }

            List<SavedPetEntry> captured = new();
            for (int i = 0; i < player.SlaveList.Count; i++)
            {
                IMonsterActor slave = player.SlaveList[i];
                if (slave == null || slave.Death || slave.Ghost)
                {
                    continue;
                }

                // 排除道士技能召唤物（骷髅、神兽），仅持久化诱惑之光/契约魔宠（如虎卫、鹰卫、黑色恶蛆、半兽勇士、巨型多角虫等）
                if (slave.Race == ActorRace.WhiteSkeleton ||
                    slave.Race == ActorRace.ElfMonster ||
                    slave.Race == ActorRace.ElfWarriormon)
                {
                    continue;
                }

                string cleanName = EcologyModule.StripAffixTag(slave.ChrName);
                byte makeLevel = (slave as MonsterObject)?.SlaveMakeLevel ?? 3;
                byte expLevel = slave.SlaveExpLevel;

                captured.Add(new SavedPetEntry
                {
                    MonsterName = cleanName,
                    SlaveMakeLevel = makeLevel,
                    SlaveExpLevel = expLevel,
                    CurHp = slave.WAbil.HP,
                    MaxHp = slave.WAbil.MaxHP
                });

                if (captured.Count >= SoloGlobalConfig.MaxSavedPets)
                {
                    break;
                }
            }

            // 仅当本次捕获到了有效魔宠时才覆盖存档，避免上线未领宠又立刻下线冲掉已存的魔宠
            if (captured.Count > 0)
            {
                profile.SavedPets = captured;
                SoloProfileStore.MarkDirty(profile, saveImmediately: true);
                if (notifyPlayer)
                {
                    player.SysMsg($"【皇家驯兽师】已为你妥善寄存 {captured.Count} 只契约灵兽！下次上线可随时领回。", MsgColor.Green, MsgType.Hint);
                }
            }

            return captured.Count;
        }

        /// <summary>
        /// 打开【皇家驯兽师】交互对话框
        /// </summary>
        public static void OpenBeastmasterDialog(PlayObject player)
        {
            if (player == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            int savedCount = profile.SavedPets?.Count ?? 0;

            string petSummary;
            if (savedCount == 0)
            {
                petSummary = "<当前兽栏空置（下线时存活的诱惑灵兽会自动寄存在此）/FCOLOR=249>";
            }
            else
            {
                List<string> items = new(savedCount);
                for (int i = 0; i < savedCount; i++)
                {
                    SavedPetEntry p = profile.SavedPets[i];
                    items.Add($"{p.MonsterName}({p.SlaveExpLevel}级)");
                }
                petSummary = $"<{string.Join("、", items)}/FCOLOR=251>";
            }

            int totalFee = savedCount * SoloGlobalConfig.PetRecallGoldCost;
            string body =
                $"尊贵的玛法学者 <{player.ChrName}>，皇家兽栏为你守护历经千辛驯服的契约灵兽。\\" +
                $"当前寄存灵兽（{savedCount}/{SoloGlobalConfig.MaxSavedPets}）：{petSummary}\\" +
                "------------------------------------------------\\" +
                $"<【一键领回】召唤全部寄存灵兽（共需 {totalFee} 金币）/@Pet_Recall>\\" +
                "<【主动登记】立即将身边存活的灵兽存入兽栏/@Pet_SaveNow>\\" +
                "<返回猎魔手札/@Hunter_Home>    <关闭窗口/@exit>";

            TownServiceModule.SendNativeNpcDialog(player, SoloGlobalConfig.BeastmasterNpcName, body);
        }

        /// <summary>
        /// 一键召回存档中寄存的契约灵兽并恢复原有等级
        /// </summary>
        public static void RecallSavedPets(PlayObject player)
        {
            if (player?.Envir == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            if (profile.SavedPets == null || profile.SavedPets.Count == 0)
            {
                player.SysMsg("【皇家驯兽师】你在兽栏中没有寄存任何灵兽！", MsgColor.Red, MsgType.Hint);
                return;
            }

            player.SlaveList ??= new List<IMonsterActor>();
            int availableSlots = SoloGlobalConfig.MaxSavedPets - player.SlaveList.Count;
            if (availableSlots <= 0)
            {
                player.SysMsg($"【皇家驯兽师】你身边已带领 {player.SlaveList.Count} 只属下，无法继续领回！", MsgColor.Red, MsgType.Hint);
                return;
            }

            int countToRecall = Math.Min(availableSlots, profile.SavedPets.Count);
            int totalCost = countToRecall * SoloGlobalConfig.PetRecallGoldCost;

            if (totalCost > 0 && (player.Gold < totalCost || !player.DecGold(totalCost)))
            {
                player.SysMsg($"【皇家驯兽师】领回 {countToRecall} 只灵兽需要饲料费 {totalCost} 金币，你的金币不足！", MsgColor.Red, MsgType.Hint);
                return;
            }

            if (totalCost > 0)
            {
                player.GoldChanged();
            }

            int recalled = 0;
            for (int i = 0; i < countToRecall; i++)
            {
                SavedPetEntry entry = profile.SavedPets[0];
                IActor slaveActor = player.MakeSlave(
                    entry.MonsterName,
                    entry.SlaveMakeLevel > 0 ? entry.SlaveMakeLevel : 3,
                    entry.SlaveExpLevel,
                    SoloGlobalConfig.MaxSavedPets,
                    86400); // 24 小时忠诚度

                if (slaveActor is MonsterObject monSlave)
                {
                    monSlave.WAbil.HP = monSlave.WAbil.MaxHP;
                    profile.SavedPets.RemoveAt(0);
                    recalled++;
                }
                else
                {
                    break;
                }
            }

            SoloProfileStore.MarkDirty(profile, saveImmediately: true);
            player.SysMsg($"【皇家驯兽师】已为你成功召回 {recalled} 只契约灵兽（消耗饲料费 {totalCost} 金币）！", MsgColor.Green, MsgType.Hint);
        }
    }
}
