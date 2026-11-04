using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using M2Server;
using M2Server.Actor;
using M2Server.Monster;
using M2Server.Player;
using OpenMir2.Common;
using OpenMir2.Data;
using OpenMir2.Enums;
using OpenMir2.Packets.ClientPackets;
using SystemModule;
using SystemModule.Actors;
using SystemModule.Enums;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 怪物生态变异元数据（按 ActorId 索引，支持 ReAliveEx 原地复活重置与 O(1) 零分配查询）
    /// </summary>
    public sealed class MonsterEcologyMeta
    {
        public int ActorId;
        public string BaseName = string.Empty;
        public ushort BaseMaxHp;
        public ushort BaseDc;
        public ushort BaseAc;
        public ushort BaseMac;
        public int BaseWalkSpeed;
        public int BaseNextHitTime;
        public int BaseFightExp;
        public byte BaseNameColor;

        public MonsterEcologyTier Tier;
        public EliteAffixType Affix;
        public bool SpawnedGuardsAt50;
        public bool PerilRegenDisabled;
    }

    /// <summary>
    /// L1.5 色彩预警精英与区域领主生态引擎（地图惊扰度、四色词缀突变、领主亲卫与残血禁回血、富饶/坚韧专属掉落）
    /// </summary>
    public static class EcologyModule
    {
        private static readonly ConcurrentDictionary<int, MonsterEcologyMeta> MetaByActorId = new();
        private static readonly ConcurrentDictionary<string, int> MapHeatByMapName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 防止领主召唤亲卫时亲卫自身再次变异为精英/领主
        /// </summary>
        [ThreadStatic]
        private static bool _suppressNextSpawnMutation;

        /// <summary>
        /// Hook 5: 怪物生成或原地复活 (Initialize / ReAliveEx) 时处理动态变异与 1.76 调色板染色
        /// </summary>
        public static void OnMonsterSpawned(AnimalObject animal)
        {
            if (animal is not MonsterObject monster)
            {
                return;
            }

            // 忽略玩家宝宝、和平动物（鹿/鸡等）、守卫及新手村极低级怪
            if (monster.Master != null || monster.Animal || monster.Race < ActorRace.Animal || monster.Abil.Level < 7)
            {
                return;
            }

            MonsterEcologyMeta meta = GetOrInitBaseMeta(monster);
            RestoreMonsterToBase(monster, meta);

            // 1. 经典大 Boss：标记为首领层（紫色名字 253），支持 20% 残血禁回血与猎魔感知
            if (SoloGlobalConfig.MajorBossNames.Contains(meta.BaseName))
            {
                meta.Tier = MonsterEcologyTier.Boss;
                meta.Affix = EliteAffixType.None;
                monster.NameColor = SoloColorPalette.BossPurple;
                monster.RefNameColor();
                return;
            }

            if (_suppressNextSpawnMutation)
            {
                return;
            }

            // 2. 自然刷新突变概率（1/18 概率突变为四色词缀精英）
            if (M2Share.RandomNumber.Random(SoloGlobalConfig.NaturalEliteChanceDenominator) == 0)
            {
                EliteAffixType randomAffix = (EliteAffixType)(M2Share.RandomNumber.Random(4) + 1);
                PromoteMonsterToElite(monster, meta, randomAffix);
            }
        }

        /// <summary>
        /// Hook 6a: 怪物受击时检查领主 50% 血量召唤同族亲卫、20% 残血狂怒攻速提升与禁绝回血
        /// </summary>
        public static void OnMonsterDamaged(AnimalObject animal, IActor hitter)
        {
            if (animal == null || animal.Death || animal.WAbil.MaxHP == 0)
            {
                return;
            }

            if (!MetaByActorId.TryGetValue(animal.ActorId, out MonsterEcologyMeta meta) || meta.Tier == MonsterEcologyTier.Normal)
            {
                return;
            }

            int hpPercent = (animal.WAbil.HP * 100) / animal.WAbil.MaxHP;
            PlayObject attackerPlayer = ResolveMasterPlayer(hitter ?? animal.LastHiter ?? animal.ExpHitter);

            // 区域领主血量 <= 50%：召唤 2 只同族亲卫
            if (meta.Tier == MonsterEcologyTier.Lord && !meta.SpawnedGuardsAt50 && hpPercent <= 50 && hpPercent > 0)
            {
                meta.SpawnedGuardsAt50 = true;
                SpawnLordKinGuards(animal, meta.BaseName, 2);
                attackerPlayer?.SysMsg(
                    $"【领主咆哮】{animal.ChrName} 发出震耳咆哮，召唤了 2 只 {meta.BaseName} 亲卫前来助阵！",
                    MsgColor.Red, MsgType.Hint);
            }

            // 区域领主或大 Boss 血量 <= 20%：困兽犹斗（攻速提升 20%，但彻底切断自然回血，解决单人战士砍不死高回血 Boss 的痛点）
            if ((meta.Tier == MonsterEcologyTier.Lord || meta.Tier == MonsterEcologyTier.Boss) &&
                !meta.PerilRegenDisabled && hpPercent <= 20 && hpPercent > 0)
            {
                meta.PerilRegenDisabled = true;
                animal.NextHitTime = Math.Max(400, (int)(animal.NextHitTime * 0.8));
                attackerPlayer?.SysMsg(
                    $"【困兽犹斗】{animal.ChrName} 陷入残血狂怒（攻速提升20%），但已彻底丧失自然回血能力，正是斩杀良机！",
                    MsgColor.Green, MsgType.Hint);
            }
        }

        /// <summary>
        /// Hook 6b: O(1) 检查怪物是否因 20% 残血困兽状态而被切断自然回血
        /// </summary>
        public static bool IsRegenDisabled(BaseObject actor)
        {
            if (actor == null || actor.Race < ActorRace.Animal)
            {
                return false;
            }

            return MetaByActorId.TryGetValue(actor.ActorId, out MonsterEcologyMeta meta) && meta.PerilRegenDisabled;
        }

        /// <summary>
        /// Hook 7: 怪物死亡时结算地图惊扰度 (Map Heat)、额外词缀战利品、猎魔图鉴与 Boss 保底气运
        /// </summary>
        public static void OnMonsterKilled(AnimalObject animal, IActor killer)
        {
            if (animal is not MonsterObject monster || monster.Master != null || monster.Envir == null)
            {
                return;
            }

            MetaByActorId.TryGetValue(monster.ActorId, out MonsterEcologyMeta meta);
            string baseName = meta?.BaseName ?? StripAffixTag(monster.ChrName);
            MonsterEcologyTier tier = meta?.Tier ?? MonsterEcologyTier.Normal;
            EliteAffixType affix = meta?.Affix ?? EliteAffixType.None;

            PlayObject player = ResolveMasterPlayer(killer ?? monster.LastHiter ?? monster.ExpHitter);
            if (player != null)
            {
                // 1. 记录猎魔图鉴进度并检查永久属性里程碑
                BestiaryModule.RecordMonsterKill(player, baseName, tier);

                // 2. 结算精英/领主/Boss 专属额外掉落（直接注入 monster.Gold 与 monster.ItemList，由原版 DieDropItems 自动散落地面）
                ApplyEcologyBonusDrops(monster, player, tier, affix);

                // 3. 累积当前地图惊扰度（仅限非安全区且等级 >= 7 的怪物）
                if (!monster.InSafeZone() && monster.Abil.Level >= 7 && tier == MonsterEcologyTier.Normal)
                {
                    AccumulateMapHeatAndTrigger(monster, player, baseName);
                }
            }

            // 恢复基础属性以便下次 ReAliveEx 原地复活干净
            if (meta != null)
            {
                meta.SpawnedGuardsAt50 = false;
                meta.PerilRegenDisabled = false;
            }
        }

        /// <summary>
        /// 获取指定地图当前惊扰度 (0..100)
        /// </summary>
        public static int GetMapHeat(string mapName)
        {
            if (string.IsNullOrEmpty(mapName))
            {
                return 0;
            }

            return MapHeatByMapName.TryGetValue(mapName, out int heat) ? heat : 0;
        }

        /// <summary>
        /// 获取指定怪物的生态层级与词缀信息
        /// </summary>
        public static bool TryGetMonsterMeta(int actorId, out MonsterEcologyMeta meta)
        {
            return MetaByActorId.TryGetValue(actorId, out meta);
        }

        private static void AccumulateMapHeatAndTrigger(MonsterObject killedMonster, PlayObject player, string baseMonName)
        {
            string mapName = killedMonster.Envir.MapName;
            string mapDesc = killedMonster.Envir.MapDesc;

            int newHeat = MapHeatByMapName.AddOrUpdate(mapName, 4, (_, old) => old + 4);

            // 惊扰度恰巧跨过 52：在玩家附近刷出 1 只同族四色词缀精英
            if (newHeat is >= 52 and < 56)
            {
                SpawnAmbushEliteOrLord(player, mapName, mapDesc, baseMonName, isLord: false);
            }
            // 惊扰度达到 100：重置为 0，并召唤 1 只猩红区域领主！
            else if (newHeat >= 100)
            {
                MapHeatByMapName[mapName] = 0;
                SpawnAmbushEliteOrLord(player, mapName, mapDesc, baseMonName, isLord: true);
            }
        }

        private static void SpawnAmbushEliteOrLord(PlayObject player, string mapName, string mapDesc, string baseMonName, bool isLord)
        {
            short spawnX = (short)Math.Clamp(player.CurrX + M2Share.RandomNumber.Random(5) - 2, 1, player.Envir.Width - 2);
            short spawnY = (short)Math.Clamp(player.CurrY + M2Share.RandomNumber.Random(5) - 2, 1, player.Envir.Height - 2);

            _suppressNextSpawnMutation = true;
            IActor spawned;
            try
            {
                spawned = SystemShare.WorldEngine.RegenMonsterByName(mapName, spawnX, spawnY, baseMonName);
            }
            finally
            {
                _suppressNextSpawnMutation = false;
            }

            if (spawned is not MonsterObject monObj)
            {
                return;
            }

            MonsterEcologyMeta meta = GetOrInitBaseMeta(monObj);
            RestoreMonsterToBase(monObj, meta);

            if (isLord)
            {
                PromoteMonsterToLord(monObj, meta);
                player.SysMsg(
                    $"【领主降临】你在 {mapDesc} 的持续杀戮惊醒了盘踞深处的猩红霸主：{monObj.ChrName}({monObj.CurrX}:{monObj.CurrY})！",
                    MsgColor.Red, MsgType.Hint);
            }
            else
            {
                EliteAffixType affix = (EliteAffixType)(M2Share.RandomNumber.Random(4) + 1);
                PromoteMonsterToElite(monObj, meta, affix);
                player.SysMsg(
                    $"【异变预警】空气中弥漫着躁动气息，一只 {monObj.ChrName} 在 ({monObj.CurrX}:{monObj.CurrY}) 现身了！",
                    MsgColor.Green, MsgType.Hint);
            }
        }

        private static void PromoteMonsterToElite(MonsterObject monster, MonsterEcologyMeta meta, EliteAffixType affix)
        {
            meta.Tier = MonsterEcologyTier.Elite;
            meta.Affix = affix;

            string suffix;
            byte color;
            double hpMult = 2.5;
            double dcMult = 1.25;
            double defMult = 1.15;

            switch (affix)
            {
                case EliteAffixType.Wealthy:
                    suffix = "[富饶]";
                    color = SoloColorPalette.WealthyGold;
                    hpMult = 2.5;
                    break;
                case EliteAffixType.Berserk:
                    suffix = "[狂暴]";
                    color = SoloColorPalette.BerserkOrange;
                    hpMult = 2.6;
                    dcMult = 1.45;
                    break;
                case EliteAffixType.Swift:
                    suffix = "[迅捷]";
                    color = SoloColorPalette.SwiftCyan;
                    hpMult = 2.2;
                    monster.WalkSpeed = Math.Max(450, (int)(meta.BaseWalkSpeed * 0.72));
                    monster.NextHitTime = Math.Max(500, (int)(meta.BaseNextHitTime * 0.75));
                    break;
                default:
                    suffix = "[坚韧]";
                    color = SoloColorPalette.ToughGreen;
                    hpMult = 3.0;
                    defMult = 1.5;
                    break;
            }

            monster.ChrName = meta.BaseName + suffix;
            monster.NameColor = color;
            ApplyScaledStats(monster, meta, hpMult, dcMult, defMult, expMult: 3);
            monster.RefNameColor();
            monster.RefShowName();
        }

        private static void PromoteMonsterToLord(MonsterObject monster, MonsterEcologyMeta meta)
        {
            meta.Tier = MonsterEcologyTier.Lord;
            meta.Affix = EliteAffixType.Wealthy;

            monster.ChrName = meta.BaseName + "[领主]";
            monster.NameColor = SoloColorPalette.LordCrimson;
            monster.WalkSpeed = Math.Max(500, (int)(meta.BaseWalkSpeed * 0.85));
            monster.NextHitTime = Math.Max(550, (int)(meta.BaseNextHitTime * 0.85));

            ApplyScaledStats(monster, meta, hpMult: 5.0, dcMult: 1.45, defMult: 1.35, expMult: 8);
            monster.RefNameColor();
            monster.RefShowName();
        }

        private static void ApplyScaledStats(MonsterObject monster, MonsterEcologyMeta meta, double hpMult, double dcMult, double defMult, int expMult)
        {
            ushort newMaxHp = (ushort)Math.Clamp((int)(meta.BaseMaxHp * hpMult), 60, 32000);
            byte loDc = (byte)Math.Clamp((int)(HUtil32.LoByte(meta.BaseDc) * dcMult), 1, 255);
            byte hiDc = (byte)Math.Clamp((int)(HUtil32.HiByte(meta.BaseDc) * dcMult), loDc, 255);
            byte loAc = (byte)Math.Clamp((int)(HUtil32.LoByte(meta.BaseAc) * defMult), 0, 255);
            byte hiAc = (byte)Math.Clamp((int)(HUtil32.HiByte(meta.BaseAc) * defMult), loAc, 255);
            byte loMac = (byte)Math.Clamp((int)(HUtil32.LoByte(meta.BaseMac) * defMult), 0, 255);
            byte hiMac = (byte)Math.Clamp((int)(HUtil32.HiByte(meta.BaseMac) * defMult), loMac, 255);

            // 同步更新 Abil 与 WAbil，防止 BaseObject.RecalcAbilitys() 将变异属性重置回白板
            monster.Abil.MaxHP = newMaxHp;
            monster.Abil.HP = newMaxHp;
            monster.Abil.DC = HUtil32.MakeWord(loDc, hiDc);
            monster.Abil.AC = HUtil32.MakeWord(loAc, hiAc);
            monster.Abil.MAC = HUtil32.MakeWord(loMac, hiMac);

            monster.WAbil.MaxHP = newMaxHp;
            monster.WAbil.HP = newMaxHp;
            monster.WAbil.DC = monster.Abil.DC;
            monster.WAbil.AC = monster.Abil.AC;
            monster.WAbil.MAC = monster.Abil.MAC;

            monster.FightExp = meta.BaseFightExp * expMult;
        }

        private static void ApplyEcologyBonusDrops(MonsterObject monster, PlayObject player, MonsterEcologyTier tier, EliteAffixType affix)
        {
            monster.ItemList ??= new List<UserItem>();

            if (tier == MonsterEcologyTier.Elite)
            {
                switch (affix)
                {
                    case EliteAffixType.Wealthy:
                        // [富饶]：必爆大堆金币 + 1~2 件必带 +1~+3 极品属性的首饰
                        monster.Gold += 8000 + M2Share.RandomNumber.Random(12000);
                        int count = 1 + M2Share.RandomNumber.Random(2);
                        for (int i = 0; i < count; i++)
                        {
                            string itemName = SoloGlobalConfig.WealthyBonusJewelryPool[
                                M2Share.RandomNumber.Random(SoloGlobalConfig.WealthyBonusJewelryPool.Length)];
                            AddGuaranteedDropItem(monster, itemName, guaranteeBonusPoints: true, minPureDura: 0);
                        }
                        player.SysMsg($"【富饶爆落】{monster.ChrName} 爆出了满地金币与极品首饰！", MsgColor.Green, MsgType.Hint);
                        break;

                    case EliteAffixType.Berserk:
                        // [狂暴]：额外金币 + 高概率极品首饰与强效太阳水
                        monster.Gold += 3500 + M2Share.RandomNumber.Random(4000);
                        string berserkJewelry = SoloGlobalConfig.WealthyBonusJewelryPool[
                            M2Share.RandomNumber.Random(SoloGlobalConfig.WealthyBonusJewelryPool.Length)];
                        AddGuaranteedDropItem(monster, berserkJewelry, guaranteeBonusPoints: true, minPureDura: 0);
                        AddGuaranteedDropItem(monster, "强效太阳水", guaranteeBonusPoints: false, minPureDura: 0);
                        break;

                    case EliteAffixType.Swift:
                        // [迅捷]：额外金币 + 2~3 瓶高阶补给药水
                        monster.Gold += 2500 + M2Share.RandomNumber.Random(2500);
                        for (int i = 0; i < 2; i++)
                        {
                            string potionName = SoloGlobalConfig.SwiftBonusSupplyPool[
                                M2Share.RandomNumber.Random(SoloGlobalConfig.SwiftBonusSupplyPool.Length)];
                            AddGuaranteedDropItem(monster, potionName, guaranteeBonusPoints: false, minPureDura: 0);
                        }
                        break;

                    case EliteAffixType.Tough:
                        // [坚韧]：必掉纯度 15~25 的高纯度黑铁矿石（供沙巴克武器升级）
                        monster.Gold += 3000 + M2Share.RandomNumber.Random(3000);
                        ushort oreDura = (ushort)((15 + M2Share.RandomNumber.Random(11)) * 1000);
                        AddGuaranteedDropItem(monster, "黑铁矿石", guaranteeBonusPoints: false, minPureDura: oreDura);
                        player.SysMsg($"【坚韧结晶】{monster.ChrName} 碎裂后掉落了纯度 {oreDura / 1000} 的高纯度黑铁矿石！", MsgColor.Green, MsgType.Hint);
                        break;
                }
            }
            else if (tier == MonsterEcologyTier.Lord)
            {
                // [领主]：丰厚金币 + 高纯度黑铁矿 + 沃玛/祝福油惊喜掉落 + 10点首领保底气运
                monster.Gold += 18000 + M2Share.RandomNumber.Random(18000);
                ushort lordOreDura = (ushort)((18 + M2Share.RandomNumber.Random(10)) * 1000);
                AddGuaranteedDropItem(monster, "黑铁矿石", guaranteeBonusPoints: false, minPureDura: lordOreDura);

                string lordReward = SoloGlobalConfig.LordBonusDropPool[
                    M2Share.RandomNumber.Random(SoloGlobalConfig.LordBonusDropPool.Length)];
                AddGuaranteedDropItem(monster, lordReward, guaranteeBonusPoints: M2Share.RandomNumber.Random(2) == 0, minPureDura: 0);

                SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
                profile.BossPityPoints += 10;
                CheckAndTriggerBossPity(monster, player, profile);
                SoloProfileStore.MarkDirty(profile);
            }
            else if (tier == MonsterEcologyTier.Boss)
            {
                // 经典大 Boss：累积 25 点保底气运，满 100 必触发保底珍稀战利品
                SoloPlayerProfile profile = SoloProfileStore.GetOrLoad(player.ChrName);
                profile.BossPityPoints += 25;
                CheckAndTriggerBossPity(monster, player, profile);
                SoloProfileStore.MarkDirty(profile, saveImmediately: true);
            }
        }

        private static void CheckAndTriggerBossPity(MonsterObject monster, PlayObject player, SoloPlayerProfile profile)
        {
            if (profile.BossPityPoints < 100)
            {
                player.SysMsg($"【猎魔气运】当前首领保底气运：{profile.BossPityPoints}/100（满100必额外爆出珍稀极品）！", MsgColor.Green, MsgType.Hint);
                return;
            }

            profile.BossPityPoints -= 100;
            string pityReward = SoloGlobalConfig.LordBonusDropPool[
                M2Share.RandomNumber.Random(SoloGlobalConfig.LordBonusDropPool.Length)];
            AddGuaranteedDropItem(monster, pityReward, guaranteeBonusPoints: true, minPureDura: 0);
            player.SysMsg($"【天道酬勤】你的猎魔保底气运已满100，{monster.ChrName} 额外爆出了珍稀战利品：【{pityReward}】！", MsgColor.Green, MsgType.Hint);
        }

        private static void AddGuaranteedDropItem(MonsterObject monster, string itemName, bool guaranteeBonusPoints, ushort minPureDura)
        {
            UserItem userItem = null;
            if (!SystemShare.ItemSystem.CopyToUserItemFromName(itemName, ref userItem) || userItem == null)
            {
                return;
            }

            StdItem stdItem = SystemShare.ItemSystem.GetStdItem(userItem.Index);
            if (stdItem == null)
            {
                return;
            }

            if (minPureDura > 0)
            {
                userItem.Dura = minPureDura;
                userItem.DuraMax = Math.Max(userItem.DuraMax, minPureDura);
            }

            if (guaranteeBonusPoints)
            {
                SystemShare.ItemSystem.RandomUpgradeItem(stdItem, userItem);
                // 若原版随机升级未摇出点数，则强制赋予 +1 ~ +3 点主属性极品（DC/MC/SC，对应 Desc[2..4]）
                if (!CombatQoLModule.HasBonusStats(userItem) && userItem.Desc != null && userItem.Desc.Length >= 5)
                {
                    int statIdx = 2 + M2Share.RandomNumber.Random(3); // 2=DC, 3=MC, 4=SC
                    userItem.Desc[statIdx] = (byte)(1 + M2Share.RandomNumber.Random(3));
                }
            }

            monster.ItemList.Add(userItem);
        }

        private static void SpawnLordKinGuards(AnimalObject lord, string baseGuardName, int guardCount)
        {
            if (lord.Envir == null || string.IsNullOrEmpty(baseGuardName))
            {
                return;
            }

            _suppressNextSpawnMutation = true;
            try
            {
                for (int i = 0; i < guardCount; i++)
                {
                    short gx = (short)Math.Clamp(lord.CurrX + M2Share.RandomNumber.Random(3) - 1, 1, lord.Envir.Width - 2);
                    short gy = (short)Math.Clamp(lord.CurrY + M2Share.RandomNumber.Random(3) - 1, 1, lord.Envir.Height - 2);
                    SystemShare.WorldEngine.RegenMonsterByName(lord.Envir.MapName, gx, gy, baseGuardName);
                }
            }
            finally
            {
                _suppressNextSpawnMutation = false;
            }
        }

        private static MonsterEcologyMeta GetOrInitBaseMeta(MonsterObject monster)
        {
            return MetaByActorId.GetOrAdd(monster.ActorId, _ => new MonsterEcologyMeta
            {
                ActorId = monster.ActorId,
                BaseName = StripAffixTag(monster.ChrName),
                BaseMaxHp = Math.Max((ushort)1, monster.Abil.MaxHP),
                BaseDc = monster.Abil.DC,
                BaseAc = monster.Abil.AC,
                BaseMac = monster.Abil.MAC,
                BaseWalkSpeed = monster.WalkSpeed > 0 ? monster.WalkSpeed : 1400,
                BaseNextHitTime = monster.NextHitTime > 0 ? monster.NextHitTime : 2000,
                BaseFightExp = monster.FightExp,
                BaseNameColor = monster.NameColor != 0 ? monster.NameColor : SoloColorPalette.NormalWhite
            });
        }

        private static void RestoreMonsterToBase(MonsterObject monster, MonsterEcologyMeta meta)
        {
            meta.Tier = MonsterEcologyTier.Normal;
            meta.Affix = EliteAffixType.None;
            meta.SpawnedGuardsAt50 = false;
            meta.PerilRegenDisabled = false;

            monster.ChrName = meta.BaseName;
            monster.NameColor = meta.BaseNameColor;
            monster.Abil.MaxHP = meta.BaseMaxHp;
            monster.Abil.HP = meta.BaseMaxHp;
            monster.Abil.DC = meta.BaseDc;
            monster.Abil.AC = meta.BaseAc;
            monster.Abil.MAC = meta.BaseMac;

            monster.WAbil.MaxHP = meta.BaseMaxHp;
            monster.WAbil.HP = meta.BaseMaxHp;
            monster.WAbil.DC = meta.BaseDc;
            monster.WAbil.AC = meta.BaseAc;
            monster.WAbil.MAC = meta.BaseMac;

            monster.WalkSpeed = meta.BaseWalkSpeed;
            monster.NextHitTime = meta.BaseNextHitTime;
            monster.FightExp = meta.BaseFightExp;
        }

        public static string StripAffixTag(string chrName)
        {
            if (string.IsNullOrEmpty(chrName))
            {
                return string.Empty;
            }

            int bracketIdx = chrName.IndexOf('[');
            return bracketIdx > 0 ? chrName[..bracketIdx] : chrName;
        }

        private static PlayObject ResolveMasterPlayer(IActor actor)
        {
            if (actor == null)
            {
                return null;
            }

            if (actor is PlayObject directPlayer)
            {
                return directPlayer;
            }

            if (actor.Master is PlayObject masterPlayer)
            {
                return masterPlayer;
            }

            return null;
        }
    }
}
