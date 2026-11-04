param(
    [string]$MirServerRoot = "D:\MirServer",
    [string]$BackupRoot = "D:\MirServer\04-Safe-Backups"
)

$ErrorActionPreference = 'Stop'
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$destDir = Join-Path $BackupRoot "SoloBackup_$timestamp"
New-Item -ItemType Directory -Path $destDir -Force | Out-Null

# 1. 备份 SoloExtension 旁路 JSON 档案 (Envir\SoloData\*.json)
$soloDataDir = Join-Path $MirServerRoot "Mir200\Envir\SoloData"
if (Test-Path $soloDataDir) {
    $destSoloData = Join-Path $destDir "SoloData"
    Copy-Item -Path $soloDataDir -Destination $destSoloData -Recurse -Force
    Write-Host "[OK] 已备份 SoloData 旁路 JSON 档案 -> $destSoloData" -ForegroundColor Green
} else {
    Write-Host "[INFO] 未检测到 $soloDataDir，跳过 SoloData 备份。" -ForegroundColor Yellow
}

# 2. 备份 DBServer 角色数据库文件 (FDB / SQLite)
$dbDir = Join-Path $MirServerRoot "DBServer\FDB"
if (Test-Path $dbDir) {
    $destDb = Join-Path $destDir "FDB"
    Copy-Item -Path $dbDir -Destination $destDb -Recurse -Force
    Write-Host "[OK] 已备份 DBServer\FDB 角色存档 -> $destDb" -ForegroundColor Green
}

Write-Host "[DONE] 单人养老服安全快照已完成：$destDir" -ForegroundColor Cyan
