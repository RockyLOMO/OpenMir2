using System;
using System.Collections.Generic;
using System.Linq;
using M2Server.Player;
using OpenMir2;
using OpenMir2.Consts;
using OpenMir2.Enums;
using SystemModule;
using SystemModule.Actors;
using SystemModule.Enums;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 【玛法猎魔手札】与怪物图鉴养成模块（拾取模式切换、本图首领/领主方位感知、图鉴里程碑永久属性加成）
    /// </summary>
    public static class BestiaryModule
    {
        /// <summary>
        /// 打开【玛法猎魔手札】主界面
        /// </summary>
        public static void OpenHunterTomeDialog(PlayObject player)
        {
            if (player == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            string lootModeDesc = profile.AutoLootMode switch
            {
                0 => "<已关闭/FCOLOR=249>",
                1 => "<全拾取模式（4x4金币+全部战利品）/FCOLOR=250>",
                _ => "<精品模式（4x4金币+药水+极品/名牌/高纯矿）/FCOLOR=251>"
            };

            int mapHeat = EcologyModule.GetMapHeat(player.Envir?.MapName);
            CalculateBestiaryBonuses(profile, out ushort bonusHp, out byte bonusDef, out byte bonusAtk, out int unlockedSpecies);

            string body =
                $"翻开《玛法猎魔手札》，这里记载着 <{player.ChrName}> 的孤胆征途。\\" +
                $"当前地图惊扰度：<{mapHeat}/100/FCOLOR=249>（满50引出四色精英，满100惊醒猩红领主）\\" +
                $"首领保底气运值：<{profile.BossPityPoints}/100/FCOLOR=251>（满100击杀领主/首领必出珍稀极品）\\" +
                $"图鉴永久属性加成：<生命+{bonusHp}  双防上限+{bonusDef}  主属性上限+{bonusAtk}/FCOLOR=254>（已点亮 {unlockedSpecies} 种图鉴）\\" +
                $"当前范围自动拾取：{lootModeDesc}\\" +
                "------------------------------------------------\\" +
                "<【切换拾取】在 全拾取 / 精品拾取 / 关闭 间切换/@Hunter_ToggleLoot>\\" +
                "<【气息感知】探查当前地图存活的精英/领主/首领坐标/@Hunter_ScanBoss>\\" +
                "<【猎魔图鉴】查看怪物讨伐里程碑明细/@Hunter_Bestiary>\\" +
                "<【皇家兽栏】存取法师契约灵兽/@Hunter_OpenPets>    <【行军营帐】打开营地面板/@Hunter_OpenCamp>\\" +
                "<【随身军需】打开军需官统卖与特修/@Solo_OpenQuartermaster>    <合上手札/@exit>";

            TownServiceModule.SendNativeNpcDialog(player, SoloGlobalConfig.HunterTomeItemName, body);
        }

        /// <summary>
        /// 切换 4x4 范围自动拾取模式（1=全拾取 -> 2=精品模式 -> 0=关闭）
        /// </summary>
        public static void CycleAutoLootMode(PlayObject player, bool reopenDialog = true)
        {
            if (player == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            profile.AutoLootMode = profile.AutoLootMode switch
            {
                1 => 2,
                2 => 0,
                _ => 1
            };
            SoloProfileStore.MarkDirty(profile, saveImmediately: true);

            string modeText = profile.AutoLootMode switch
            {
                1 => "全拾取模式（4x4范围自动捡取金币与全部物品）",
                2 => "精品模式（4x4范围自动捡取金币、药水、极品、名牌与高纯度黑铁矿）",
                _ => "已关闭（恢复手动拾取）"
            };

            player.SysMsg($"【玛法猎魔手札】范围自动拾取已切换为：{modeText}！", MsgColor.Green, MsgType.Hint);
            if (reopenDialog)
            {
                OpenHunterTomeDialog(player);
            }
        }

        /// <summary>
        /// 探查当前地图存活的四色精英、猩红领主与经典大 Boss 坐标
        /// </summary>
        public static void ScanCurrentMapBosses(PlayObject player, bool showDialog = true)
        {
            if (player?.Envir == null)
            {
                return;
            }

            IList<IActor> monsters = new List<IActor>();
            SystemShare.WorldEngine.GetMapMonster(player.Envir, monsters);

            List<string> findings = new();
            for (int i = 0; i < monsters.Count; i++)
            {
                IActor actor = monsters[i];
                if (actor == null || actor.Death || actor.Ghost || actor.Master != null)
                {
                    continue;
                }

                bool isNotable = false;
                string colorTag = "251";
                if (EcologyModule.TryGetMonsterMeta(actor.ActorId, out MonsterEcologyMeta meta) && meta.Tier != MonsterEcologyTier.Normal)
                {
                    isNotable = true;
                    colorTag = meta.Tier switch
                    {
                        MonsterEcologyTier.Lord => "249",
                        MonsterEcologyTier.Boss => "253",
                        _ => "251"
                    };
                }
                else if (SoloGlobalConfig.MajorBossNames.Contains(EcologyModule.StripAffixTag(actor.ChrName)))
                {
                    isNotable = true;
                    colorTag = "253";
                }

                if (isNotable)
                {
                    findings.Add($"<{actor.ChrName}({actor.CurrX}:{actor.CurrY}) [HP:{actor.WAbil.HP}/{actor.WAbil.MaxHP}]/FCOLOR={colorTag}>");
                    if (findings.Count >= 10)
                    {
                        break;
                    }
                }
            }

            int mapHeat = EcologyModule.GetMapHeat(player.Envir.MapName);
            if (!showDialog)
            {
                if (findings.Count == 0)
                {
                    player.SysMsg($"【猎魔感知】{player.Envir.MapDesc}(惊扰度:{mapHeat}/100) 当前未感应到精英或首领气息。", MsgColor.Green, MsgType.Hint);
                }
                else
                {
                    player.SysMsg($"【猎魔感知】{player.Envir.MapDesc}(惊扰度:{mapHeat}/100) 发现 {findings.Count} 处高危气息：", MsgColor.Green, MsgType.Hint);
                    foreach (string item in findings)
                    {
                        string plain = item.Replace("<", "").Split("/FCOLOR=")[0];
                        player.SysMsg($"  -> {plain}", MsgColor.Red, MsgType.Hint);
                    }
                }
                return;
            }

            string listBlock = findings.Count == 0
                ? "<当前地图风平浪静，暂未感知到精英、领主或首领气息（继续刷怪累积惊扰度可引出精英与领主）。/FCOLOR=250>\\"
                : string.Join("\\", findings) + "\\";

            string body =
                $"【玛法气息感知】当前所在：<{player.Envir.MapDesc}({player.Envir.MapName})>  惊扰度：<{mapHeat}/100/FCOLOR=249>\\" +
                "------------------------------------------------\\" +
                listBlock +
                "------------------------------------------------\\" +
                "<重新感知本图气息/@Hunter_ScanBoss>    <返回手札首页/@Hunter_Home>    <关闭/@exit>";

            TownServiceModule.SendNativeNpcDialog(player, "玛法气息感知", body);
        }

        /// <summary>
        /// 打开猎魔图鉴里程碑详情页
        /// </summary>
        public static void OpenBestiaryDetailDialog(PlayObject player)
        {
            if (player == null)
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            CalculateBestiaryBonuses(profile, out ushort bonusHp, out byte bonusDef, out byte bonusAtk, out int unlockedSpecies);

            var topKills = profile.BestiaryKills
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv =>
                {
                    string rank = kv.Value >= 500 ? "[金章:主属性+1]" : kv.Value >= 200 ? "[银章:双防+1]" : kv.Value >= 50 ? "[铜章:生命+3]" : "[讨伐中]";
                    return $"{kv.Key}: <{kv.Value}只/FCOLOR=251> {rank}";
                })
                .ToList();

            string recordsText = topKills.Count == 0
                ? "尚未记录任何怪物讨伐数据，快去玛法大陆历练吧！\\"
                : string.Join("\\", topKills) + "\\";

            string body =
                $"【猎魔图鉴统计】累计讨伐总数：<{profile.TotalMonstersKilled}/FCOLOR=251>  精英：<{profile.TotalElitesKilled}/FCOLOR=250>  领主：<{profile.TotalLordsKilled}/FCOLOR=249>\\" +
                $"已解锁图鉴勋章：<{unlockedSpecies} 枚/FCOLOR=254>（单怪满50只获铜章+3HP，满200只获银章+1双防，满500只获金章+1攻魔道）\\" +
                $"当前总加成：<生命上限+{bonusHp}、防御/魔御上限+{bonusDef}、攻/魔/道上限+{bonusAtk}/FCOLOR=250>\\" +
                "------------------------------------------------\\" +
                recordsText +
                "------------------------------------------------\\" +
                "<返回猎魔手札首页/@Hunter_Home>    <关闭窗口/@exit>";

            TownServiceModule.SendNativeNpcDialog(player, "玛法猎魔图鉴", body);
        }

        /// <summary>
        /// 记录怪物击杀并检查是否达成 50 / 200 / 500 讨伐里程碑
        /// </summary>
        public static void RecordMonsterKill(PlayObject player, string baseMonsterName, MonsterEcologyTier tier)
        {
            if (player == null || string.IsNullOrEmpty(baseMonsterName))
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            profile.TotalMonstersKilled++;
            if (tier == MonsterEcologyTier.Elite)
            {
                profile.TotalElitesKilled++;
            }
            else if (tier is MonsterEcologyTier.Lord or MonsterEcologyTier.Boss)
            {
                profile.TotalLordsKilled++;
            }

            int currentKills = profile.BestiaryKills.TryGetValue(baseMonsterName, out int old) ? old + 1 : 1;
            profile.BestiaryKills[baseMonsterName] = currentKills;
            SoloProfileStore.MarkDirty(profile);

            if (currentKills is 50 or 200 or 500)
            {
                string medal = currentKills switch
                {
                    50 => "【铜章里程碑】（永久生命上限+3）",
                    200 => "【银章里程碑】（永久防御/魔御上限+1）",
                    _ => "【金章里程碑】（永久全职业主属性上限+1）"
                };

                player.RecalcAbilitys();
                player.SendMsg(Messages.RM_ABILITY, 0, 0, 0, 0);
                SoloProfileStore.MarkDirty(profile, saveImmediately: true);
                player.SysMsg($"【猎魔图鉴】恭喜！你已累计讨伐 {currentKills} 只 [{baseMonsterName}]，达成 {medal}！", MsgColor.Green, MsgType.Hint);
            }
        }

        /// <summary>
        /// 将玩家猎魔图鉴里程碑属性叠加至 WAbil
        /// </summary>
        public static void ApplyBestiaryStatBonuses(PlayObject player)
        {
            if (player == null || string.IsNullOrEmpty(player.ChrName))
            {
                return;
            }

            SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
            CalculateBestiaryBonuses(profile, out ushort bonusHp, out byte bonusDef, out byte bonusAtk, out _);

            if (bonusHp > 0)
            {
                player.WAbil.MaxHP = (ushort)Math.Min(ushort.MaxValue, player.WAbil.MaxHP + bonusHp);
            }

            if (bonusDef > 0)
            {
                player.WAbil.AC = HUtil32.MakeWord(
                    HUtil32.LoByte(player.WAbil.AC),
                    (ushort)Math.Min(255, HUtil32.HiByte(player.WAbil.AC) + bonusDef));
                player.WAbil.MAC = HUtil32.MakeWord(
                    HUtil32.LoByte(player.WAbil.MAC),
                    (ushort)Math.Min(255, HUtil32.HiByte(player.WAbil.MAC) + bonusDef));
            }

            if (bonusAtk > 0)
            {
                player.WAbil.DC = HUtil32.MakeWord(
                    HUtil32.LoByte(player.WAbil.DC),
                    (ushort)Math.Min(255, HUtil32.HiByte(player.WAbil.DC) + bonusAtk));
                player.WAbil.MC = HUtil32.MakeWord(
                    HUtil32.LoByte(player.WAbil.MC),
                    (ushort)Math.Min(255, HUtil32.HiByte(player.WAbil.MC) + bonusAtk));
                player.WAbil.SC = HUtil32.MakeWord(
                    HUtil32.LoByte(player.WAbil.SC),
                    (ushort)Math.Min(255, HUtil32.HiByte(player.WAbil.SC) + bonusAtk));
            }
        }

        private static void CalculateBestiaryBonuses(
            SoloPlayerProfile profile,
            out ushort bonusHp,
            out byte bonusDef,
            out byte bonusAtk,
            out int unlockedSpecies)
        {
            bonusHp = 0;
            bonusDef = 0;
            bonusAtk = 0;
            unlockedSpecies = 0;

            if (profile?.BestiaryKills == null || profile.BestiaryKills.Count == 0)
            {
                return;
            }

            int bronzeCount = 0;
            int silverCount = 0;
            int goldCount = 0;

            foreach (KeyValuePair<string, int> kv in profile.BestiaryKills)
            {
                if (kv.Value >= 50)
                {
                    unlockedSpecies++;
                    bronzeCount++;
                }
                if (kv.Value >= 200)
                {
                    silverCount++;
                }
                if (kv.Value >= 500)
                {
                    goldCount++;
                }
            }

            // 铜章每枚 +3 HP（上限 +150 HP）；银章每枚 +1 双防上限（上限 +12）；金章每枚 +1 攻魔道上限（上限 +15）
            bonusHp = (ushort)Math.Min(150, bronzeCount * 3);
            bonusDef = (byte)Math.Min(12, silverCount);
            bonusAtk = (byte)Math.Min(15, goldCount + (profile.TotalLordsKilled / 10));
        }
    }
}
