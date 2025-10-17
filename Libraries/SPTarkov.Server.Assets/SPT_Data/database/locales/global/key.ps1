<#
.SYNOPSIS
    检测JSON文件中的重复键

.DESCRIPTION
    扫描JSON文件并识别重复的键名，显示重复次数和所在行号

.PARAMETER Path
    JSON文件的路径

.PARAMETER ShowDetails
    显示每个重复键的详细行号信息

.EXAMPLE
    .\Check-JsonDuplicateKeys.ps1 -Path "C:\SPT4\Client.0.16.9.0.40087\SPT\SPT_Data\database\locales\global\ch.json"

.EXAMPLE
    .\Check-JsonDuplicateKeys.ps1 -Path ".\config.json" -ShowDetails
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$Path,
    
    [Parameter(Mandatory=$false)]
    [switch]$ShowDetails
)

# 检查文件是否存在
if (-not (Test-Path $Path)) {
    Write-Error "文件不存在: $Path"
    exit 1
}

Write-Host "正在检查文件: $Path" -ForegroundColor Cyan
Write-Host "=" * 60

# 存储键和行号的哈希表
$keyLines = @{}
$lineNumber = 0

# 读取文件并处理每一行
Get-Content $Path | ForEach-Object {
    $lineNumber++
    $line = $_
    
    # 匹配JSON键的正则表达式
    if ($line -match '^\s*"([^"]+)"\s*:') {
        $keyName = $matches[1]
        
        # 如果键已存在，添加到数组；否则创建新数组
        if ($keyLines.ContainsKey($keyName)) {
            $keyLines[$keyName] += $lineNumber
        } else {
            $keyLines[$keyName] = @($lineNumber)
        }
    }
}

# 查找重复的键
$duplicates = $keyLines.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 }

if ($duplicates) {
    Write-Host "`n发现重复的键:" -ForegroundColor Yellow
    Write-Host "-" * 60
    
    $duplicateCount = 0
    foreach ($dup in $duplicates | Sort-Object { $_.Value.Count } -Descending) {
        $duplicateCount++
        Write-Host "`n[$duplicateCount] 键名: " -NoNewline -ForegroundColor White
        Write-Host """$($dup.Key)""" -ForegroundColor Red
        Write-Host "    重复次数: $($dup.Value.Count)" -ForegroundColor Yellow
        
        if ($ShowDetails) {
            Write-Host "    出现在行: " -NoNewline -ForegroundColor Gray
            Write-Host ($dup.Value -join ", ") -ForegroundColor Cyan
        }
    }
    
    Write-Host "`n" + "=" * 60
    Write-Host "总计: 发现 $duplicateCount 个重复的键" -ForegroundColor Red
    
    # 返回退出代码1表示发现重复
    exit 1
} else {
    Write-Host "`n✓ 未发现重复的键" -ForegroundColor Green
    Write-Host "=" * 60
    exit 0
}