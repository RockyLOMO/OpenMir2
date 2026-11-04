using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMir2.Common;

namespace OpenMir2.SoloExtension
{
    /// <summary>
    /// 离线法师灵兽寄存条目
    /// </summary>
    public sealed class SavedPetEntry
    {
        public string MonsterName { get; set; } = string.Empty;
        public byte SlaveMakeLevel { get; set; }
        public byte SlaveExpLevel { get; set; }
        public ushort CurHp { get; set; }
        public ushort MaxHp { get; set; }
    }

    /// <summary>
    /// 单人养老扩展玩家旁路档案（对应 Envir\SoloData\Solo_{ChrName}.json）
    /// 严禁修改原生 DBServer 二进制存档结构
    /// </summary>
    public sealed class SoloPlayerProfile
    {
        public string ChrName { get; set; } = string.Empty;

        // 1. 玛法行军帐（前线营地坐标）
        public string CampMapName { get; set; } = string.Empty;
        public string CampMapDesc { get; set; } = string.Empty;
        public short CampX { get; set; }
        public short CampY { get; set; }

        // 2. 战斗减负开关
        /// <summary>
        /// 自动拾取模式：0 = 关闭，1 = 全拾取（金币+所有物品），2 = 精品模式（金币+药水+极品/名牌/高纯矿/书）
        /// </summary>
        public int AutoLootMode { get; set; } = 1;

        /// <summary>
        /// 免按 Shift 持续锁定攻击开关
        /// </summary>
        public bool ShiftFreeAttack { get; set; } = true;

        /// <summary>
        /// 是否已发放新手开荒礼包（玛法行军帐 + 玛法猎魔手札）
        /// </summary>
        public bool StarterKitGranted { get; set; }

        // 3. 猎魔图鉴与保底机制
        public Dictionary<string, int> BestiaryKills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalMonstersKilled { get; set; }
        public int TotalElitesKilled { get; set; }
        public int TotalLordsKilled { get; set; }
        public int BossPityPoints { get; set; }

        // 4. 法师离线寄存灵兽列表
        public List<SavedPetEntry> SavedPets { get; set; } = new();

        // 运行时瞬态字段（不序列化到 JSON，避免高频分配与 I/O）
        [JsonIgnore]
        public int LastAutoLootTick;

        [JsonIgnore]
        public short LastAutoLootX = -1;

        [JsonIgnore]
        public short LastAutoLootY = -1;

        [JsonIgnore]
        public int LastShiftFreeAttackTick;

        [JsonIgnore]
        public bool IsDirty;

        [JsonIgnore]
        public int LastSaveTick;
    }

    /// <summary>
    /// 旁路 JSON 档案缓存与低开销持久化管理器
    /// 仅在上下线、立营、领宠及定时脏检查时落盘，热点路径 0 磁盘 I/O
    /// </summary>
    public static class SoloProfileStore
    {
        private static readonly ConcurrentDictionary<string, SoloPlayerProfile> Profiles =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static SoloPlayerProfile GetOrLoad(string chrName)
        {
            if (string.IsNullOrWhiteSpace(chrName))
            {
                return new SoloPlayerProfile();
            }

            return Profiles.GetOrAdd(chrName, LoadFromDisk);
        }

        public static void MarkDirty(SoloPlayerProfile profile, bool saveImmediately = false)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ChrName))
            {
                return;
            }

            profile.IsDirty = true;
            if (saveImmediately)
            {
                SaveToDisk(profile);
            }
        }

        public static void FlushIfDue(SoloPlayerProfile profile, int currentTick)
        {
            if (profile == null || !profile.IsDirty)
            {
                return;
            }

            // 至少间隔 30 秒才后台落盘一次，避免频繁击杀更新图鉴导致磁盘 I/O
            if (currentTick - profile.LastSaveTick >= 30_000)
            {
                SaveToDisk(profile);
            }
        }

        public static void SaveAndUnload(string chrName)
        {
            if (string.IsNullOrWhiteSpace(chrName))
            {
                return;
            }

            if (Profiles.TryGetValue(chrName, out SoloPlayerProfile profile))
            {
                SaveToDisk(profile);
            }
        }

        private static SoloPlayerProfile LoadFromDisk(string chrName)
        {
            try
            {
                string filePath = GetProfileFilePath(chrName);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    SoloPlayerProfile loaded = JsonSerializer.Deserialize<SoloPlayerProfile>(json, JsonOptions);
                    if (loaded != null)
                    {
                        loaded.ChrName = chrName;
                        loaded.BestiaryKills ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        loaded.SavedPets ??= new List<SavedPetEntry>();
                        loaded.LastSaveTick = HUtil32.GetTickCount();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[SoloExtension] 读取玩家档案失败 ({chrName}): {ex.Message}");
            }

            return new SoloPlayerProfile
            {
                ChrName = chrName,
                AutoLootMode = 1,
                ShiftFreeAttack = true,
                LastSaveTick = HUtil32.GetTickCount()
            };
        }

        public static void SaveToDisk(SoloPlayerProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ChrName))
            {
                return;
            }

            try
            {
                string filePath = GetProfileFilePath(profile.ChrName);
                string tempPath = filePath + ".tmp";
                string json = JsonSerializer.Serialize(profile, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
                profile.IsDirty = false;
                profile.LastSaveTick = HUtil32.GetTickCount();
            }
            catch (Exception ex)
            {
                LogService.Error($"[SoloExtension] 保存玩家档案失败 ({profile.ChrName}): {ex.Message}");
            }
        }

        private static string GetProfileFilePath(string chrName)
        {
            string safeName = string.Join("_", chrName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(SoloGlobalConfig.GetSoloDataDirectory(), $"Solo_{safeName}.json");
        }
    }
}
