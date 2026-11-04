using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using SystemModule;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 1.76 原生调色板色彩映射常量（严格兼容经典客户端）
    /// </summary>
    public static class SoloColorPalette
    {
        public const byte NormalWhite = 255;   // 普通白怪
        public const byte WealthyGold = 251;   // [富饶] 耀眼金黄
        public const byte BerserkOrange = 250; // [狂暴] 狂暴深橙
        public const byte SwiftCyan = 254;     // [迅捷] 疾风亮青
        public const byte ToughGreen = 241;    // [坚韧] 坚甲翠绿
        public const byte LordCrimson = 249;   // [领主] 猩红警报
        public const byte BossPurple = 253;    // [首领] 紫气东来
    }

    /// <summary>
    /// 怪群生态层级枚举
    /// </summary>
    public enum MonsterEcologyTier : byte
    {
        Normal = 0,
        Elite = 1,
        Lord = 2,
        Boss = 3
    }

    /// <summary>
    /// 精英突变词缀枚举
    /// </summary>
    public enum EliteAffixType : byte
    {
        None = 0,
        Wealthy = 1, // [富饶] 必爆大堆金币 + 极品首饰
        Berserk = 2, // [狂暴] 攻击提升 + 爆率翻倍
        Swift = 3,   // [迅捷] 移速攻速提升 + 药水补给
        Tough = 4    // [坚韧] 双防提升 + 高纯度黑铁矿石
    }

    /// <summary>
    /// 单人养老扩展全局配置与零分配只读集合（FrozenSet 降低 CPU 查找开销）
    /// </summary>
    public static class SoloGlobalConfig
    {
        public const string CampItemName = "玛法行军帐";
        public const string HunterTomeItemName = "玛法猎魔手札";
        public const string QuartermasterNpcName = "玛法军需官";
        public const string BeastmasterNpcName = "皇家驯兽师";

        /// <summary>
        /// 自动拾取扫描周期（毫秒），避免高频 Tick 消耗 CPU
        /// </summary>
        public const int AutoLootScanIntervalMs = 450;

        /// <summary>
        /// 自动拾取半径（格）：4x4 范围
        /// </summary>
        public const int AutoLootRadius = 4;

        /// <summary>
        /// 营地扎营费用（金币）
        /// </summary>
        public const int CampSetupGoldCost = 1000;

        /// <summary>
        /// 驯兽师寄存单只灵兽领回费用（金币）
        /// </summary>
        public const int PetRecallGoldCost = 2000;

        /// <summary>
        /// 最多可寄存的法师诱惑宠物数量
        /// </summary>
        public const int MaxSavedPets = 5;

        /// <summary>
        /// 自然刷新突变精英概率（1/18 ≈ 5.5%）
        /// </summary>
        public const int NaturalEliteChanceDenominator = 18;

        /// <summary>
        /// 禁止设立行军营地的 Boss 巢穴与特殊地图（地图代号或地图中文描述均匹配）
        /// </summary>
        public static readonly FrozenSet<string> ForbiddenCampMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 经典 Boss 巢穴地图编号
            "D515", "D717", "D024", "D2079", "D2013", "D2067", "D10061", "D10062",
            "D2083", "H007", "H010", "0150", "F001", "63", "65", "66",
            // 地图中文名称关键词
            "祖玛教主之家", "赤月魔穴", "恶魔祭坛", "石墓七层", "沃玛教主大殿",
            "牛魔寺庙大厅", "封魔殿", "未知暗殿", "沙巴克皇宫", "幻境七层", "幻境十层",
            "魔龙血域", "困惑殿堂", "堕落坟场", "死亡棺材", "桃源之门"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 经典大 Boss 名单（用于猎魔手札感知、禁回血斩杀、紫名标识与保底幸运值累积）
        /// </summary>
        public static readonly FrozenSet<string> MajorBossNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "沃玛教主", "祖玛教主", "虹魔教主", "黄泉教主", "牛魔王",
            "赤月恶魔", "双头金刚", "双头血魔", "暗之牛魔王", "触龙神",
            "邪恶蜈蚣", "白野猪", "邪恶毒蛇", "虹魔猪卫", "虹魔蝎卫",
            "暗之沃玛教主", "暗之双头血魔", "暗之黄泉教主", "暗之骷髅精灵",
            "暗之虹魔教主", "暗之双头金刚", "重装使者", "地藏王", "魔龙教主",
            "魔龙力士", "魔龙战将", "魔龙巨蛾"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 严禁被军需官一键回收的高价值名牌装备/特殊物品名单（O(1) 零分配查找）
        /// </summary>
        public static readonly FrozenSet<string> ProtectedFamousItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 专属扩展道具
            CampItemName, HunterTomeItemName,
            // 沃玛系列
            "炼狱", "魔杖", "银蛇", "井中月", "血饮", "无极棍",
            "生命项链", "天竺项链", "幽灵项链", "号角", "沃玛号角",
            "思贝儿手镯", "红宝石戒指", "心灵手镯", "铂金戒指", "龙之戒指", "幽灵手套",
            "记忆头盔", "记忆项链", "记忆手镯", "记忆戒指",
            "祈祷头盔", "祈祷项链", "祈祷手镯", "祈祷戒指", "祈祷之刃",
            // 祖玛系列
            "裁决之杖", "骨玉权杖", "龙纹剑", "怒斩", "龙牙", "逍遥扇", "命运之刃", "屠龙", "嗜魂法杖", "霸者之刃",
            "黑铁头盔", "绿色项链", "恶魔铃铛", "灵魂项链", "技巧项链",
            "骑士手镯", "阎罗手套", "龙之手镯", "三眼手镯",
            "力量戒指", "紫碧螺", "泰坦戒指",
            // 赤月与六大重装
            "圣战头盔", "圣战项链", "圣战手镯", "圣战戒指",
            "法神头盔", "法神项链", "法神手镯", "法神戒指",
            "天尊头盔", "天尊项链", "天尊手镯", "天尊戒指",
            "天魔神甲", "圣战宝甲", "法神披风", "霓裳羽衣", "天尊道袍", "天师长袍",
            // 魔龙与雷霆/烈焰/光芒
            "雷霆战甲(男)", "雷霆战甲(女)", "烈焰魔衣(男)", "烈焰魔衣(女)", "光芒道袍(男)", "光芒道袍(女)",
            "雷霆项链", "雷霆护腕", "雷霆战戒", "雷霆战靴", "雷霆腰带",
            "烈焰项链", "烈焰护腕", "烈焰魔戒", "烈焰魔靴", "烈焰腰带",
            "光芒项链", "光芒护腕", "光芒道戒", "光芒道靴", "光芒腰带",
            "开天", "镇天", "玄天",
            // 特戒与特殊材料
            "麻痹戒指", "复活戒指", "护身戒指", "传送戒指", "隐身戒指", "防御戒指", "火焰戒指", "治愈戒指", "超负载戒指",
            "狂风项链", "狂风戒指", "辟邪手镯", "夏普儿手镯", "白色虎齿项链", "灯笼项链",
            "祝福油", "金条", "金砖", "金盒", "鹿茸", "紫水晶矿"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 富饶精英必爆极品首饰奖池（随机附加 +1~+3 极品属性）
        /// </summary>
        public static readonly string[] WealthyBonusJewelryPool =
        {
            "白色虎齿项链", "灯笼项链", "死神手套", "金手镯", "坚固手套",
            "降妖除魔戒指", "珊瑚戒指", "魅力戒指", "道德戒指", "生铁戒指",
            "凤凰明珠", "琥珀项链", "放大镜", "竹笛", "蓝色水晶戒指", "黑色水晶戒指", "骷髅戒指"
        };

        /// <summary>
        /// 迅捷精英必爆补给品奖池
        /// </summary>
        public static readonly string[] SwiftBonusSupplyPool =
        {
            "强效太阳水", "太阳水", "疗伤药", "超级金创药", "超级魔法药"
        };

        /// <summary>
        /// 领主级怪物额外惊喜奖池
        /// </summary>
        public static readonly string[] LordBonusDropPool =
        {
            "祝福油", "沃玛号角", "生命项链", "幽灵项链", "天竺项链",
            "红宝石戒指", "思贝儿手镯", "心灵手镯", "铂金戒指", "龙之戒指",
            "幽灵手套", "井中月", "魔杖", "银蛇", "炼狱"
        };

        /// <summary>
        /// 获取 SoloData 旁路 JSON 目录绝对路径
        /// </summary>
        public static string GetSoloDataDirectory()
        {
            string basePath = !string.IsNullOrEmpty(SystemShare.BasePath)
                ? SystemShare.BasePath
                : AppContext.BaseDirectory;
            string envirSoloDir = Path.Combine(basePath, "Envir", "SoloData");
            if (!Directory.Exists(envirSoloDir))
            {
                Directory.CreateDirectory(envirSoloDir);
            }
            return envirSoloDir;
        }
    }
}
