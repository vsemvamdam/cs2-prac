#Requires -Version 5.1
<#
  CS2 Prac — локальный тренировочный сервер CS2 (смоки + префаер) на официальных картах.

  Действия:
    -Action update   установить или обновить: сервер CS2 (SteamCMD), Metamod, CounterStrikeSharp,
                     MatchZy (смоки/практика), OpenPrefirePrac (префаер)
    -Action plugin   переустановить только плагин CS2 Prac, конфиги и бинды 1–9 (без SteamCMD)
    -Action start    запустить сервер: -Mode smokes | prefire, -Map de_mirage
    -Action stop     остановить сервер
    -Action diag     собрать диагностику в logs\diag.txt
    -Action selftest запустить оба режима без игры и сохранить logs\diag-smokes.txt / diag-prefire.txt

  Сервер ставится в отдельную папку через SteamCMD с анонимным входом. В установленной игре
  меняется только одно: в её папку cfg кладутся cs2prac_binds.cfg / cs2prac_unbinds.cfg (бинды 1–9).
#>
param(
    [ValidateSet('update', 'plugin', 'start', 'stop', 'diag', 'selftest')]
    [string]$Action = 'start',
    [ValidateSet('smokes', 'prefire')]
    [string]$Mode = 'smokes',
    [string]$Map = 'de_mirage',
    [string]$InstallRoot,
    [switch]$Validate,
    [switch]$NoConnect
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogDir       = Join-Path $ScriptDir 'logs'
$BackupDir    = Join-Path $ScriptDir 'backups'
$SettingsFile = Join-Path $ScriptDir 'settings.json'
$VersionsFile = Join-Path $ScriptDir 'versions.txt'
New-Item -ItemType Directory -Force -Path $LogDir, $BackupDir | Out-Null
$LogFile    = Join-Path $LogDir ("{0}-{1}.log" -f $Action, (Get-Date -Format 'yyyyMMdd-HHmmss'))
$StatusFile = Join-Path $LogDir 'status.txt'
$UA = @{ 'User-Agent' = 'cs2prac-installer' }

function Log([string]$Text) {
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Text
    Write-Host $line
    try { [IO.File]::AppendAllText($LogFile, $line + "`r`n", [Text.Encoding]::UTF8) } catch { }
}
function Set-Status([string]$Text) {
    Log "== $Text"
    try { [IO.File]::WriteAllText($StatusFile, ('{0} | {1} | {2}' -f (Get-Date -Format 's'), $Action, $Text), [Text.Encoding]::UTF8) } catch { }
}

# ---------- пути ----------
function Resolve-InstallRoot {
    if ($InstallRoot) { return $InstallRoot }
    if (Test-Path $SettingsFile) {
        $s = Get-Content $SettingsFile -Raw | ConvertFrom-Json
        if ($s.InstallRoot) { return $s.InstallRoot }
    }
    # первый запуск: диск с наибольшим свободным местом
    $disk = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Sort-Object FreeSpace -Descending | Select-Object -First 1
    return (Join-Path ($disk.DeviceID + '\') 'CS2PracServer')
}
$Root       = Resolve-InstallRoot
$SteamCmd   = Join-Path $Root 'steamcmd\steamcmd.exe'
$ServerDir  = Join-Path $Root 'server'
$Cs2Exe     = Join-Path $ServerDir 'game\bin\win64\cs2.exe'
$Csgo       = Join-Path $ServerDir 'game\csgo'
$Addons     = Join-Path $Csgo 'addons'
$Plugins    = Join-Path $Addons 'counterstrikesharp\plugins'
$PluginsOff = Join-Path $Root 'plugins-off'   # вне папки plugins, чтобы CSS их точно не грузил
$Tmp        = Join-Path $Root 'tmp'

function Save-Settings {
    @{ InstallRoot = $Root } | ConvertTo-Json | Set-Content -Path $SettingsFile -Encoding UTF8
}

# ---------- утилиты ----------
function Get-File([string]$Url, [string]$OutFile) {
    Log "Скачиваю $Url"
    for ($i = 1; $i -le 3; $i++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -Headers $UA
            return
        } catch {
            Log "  попытка $i не удалась: $($_.Exception.Message)"
            Start-Sleep -Seconds (3 * $i)
        }
    }
    throw "Не удалось скачать $Url"
}

function Expand-Zip([string]$Zip, [string]$Dest) {
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Dest)
}

function Copy-Tree([string]$Src, [string]$Dst) {
    New-Item -ItemType Directory -Force -Path $Dst | Out-Null
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & robocopy.exe $Src $Dst /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $old
    if ($code -ge 8) { throw "robocopy $Src -> $Dst завершился с кодом $code" }
}

# Каталог, в котором лежит папка addons (корень архива или на уровень ниже)
function Find-ArchiveRoot([string]$Dir) {
    if (Test-Path (Join-Path $Dir 'addons')) { return $Dir }
    $sub = Get-ChildItem $Dir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'addons') } | Select-Object -First 1
    if ($sub) { return $sub.FullName }
    return $null
}

function Get-GithubAsset([string]$Repo, [string]$Pattern) {
    $rels = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=20" -Headers ($UA + @{ Accept = 'application/vnd.github+json' }) -UseBasicParsing
    $rels = @($rels | Where-Object { -not $_.draft })
    $ordered = @($rels | Where-Object { -not $_.prerelease }) + @($rels | Where-Object { $_.prerelease })
    foreach ($r in $ordered) {
        $a = $r.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
        if ($a) { return [pscustomobject]@{ Tag = $r.tag_name; Name = $a.name; Url = $a.browser_download_url; Date = $r.published_at } }
    }
    $names = ($rels | Select-Object -First 1).assets | ForEach-Object { $_.name }
    throw "В релизах $Repo нет файла по шаблону '$Pattern'. В последнем релизе: $($names -join ', ')"
}

function Get-ServerProcesses {
    Get-Process -Name cs2 -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($ServerDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
}

function Stop-Server {
    $p = @(Get-ServerProcesses)
    if ($p.Count -gt 0) {
        Log "Останавливаю сервер (PID $($p.Id -join ', '))"
        $p | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

# ---------- установка компонентов ----------
function Update-GameFiles {
    Set-Status 'SteamCMD: скачиваю/обновляю сервер CS2 (первый раз это десятки ГБ)'
    if (-not (Test-Path $SteamCmd)) {
        $dir = Split-Path $SteamCmd
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $zip = Join-Path $Tmp 'steamcmd.zip'
        Get-File 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' $zip
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
    }
    $scArgs = @('+force_install_dir', $ServerDir, '+login', 'anonymous', '+app_update', '730')
    if ($Validate -or -not (Test-Path $Cs2Exe)) { $scArgs += 'validate' }
    $scArgs += '+quit'
    $code = -1
    for ($i = 1; $i -le 4; $i++) {
        Log "SteamCMD, попытка $i"
        # Вывод SteamCMD идёт прямо в окно (живой прогресс), в лог — только итог
        $proc = Start-Process -FilePath $SteamCmd -ArgumentList $scArgs -NoNewWindow -PassThru
        $null = $proc.Handle
        $t0 = Get-Date
        while (-not $proc.WaitForExit(60000)) {
            Set-Status ("SteamCMD работает {0:N0} мин" -f ((Get-Date) - $t0).TotalMinutes)
        }
        $code = $proc.ExitCode
        Log "SteamCMD завершился с кодом $code"
        if ($code -eq 0 -and (Test-Path $Cs2Exe)) { return }
        Start-Sleep -Seconds 5
    }
    throw "SteamCMD не смог установить сервер CS2 (код $code). Подробности в логе и в $Root\steamcmd\logs"
}

function Install-Metamod {
    Set-Status 'Metamod:Source'
    $base = 'https://mms.alliedmods.net/mmsdrop/2.0/'
    $url = $null; $ver = $null
    try {
        $r = Invoke-WebRequest -Uri ($base + 'mmsource-latest-windows') -UseBasicParsing -Headers $UA
        $c = $r.Content
        if ($c -is [byte[]]) { $c = [Text.Encoding]::ASCII.GetString($c) }
        $name = "$c".Trim()
        if ($name -match '^mmsource-[\w\.\-]+-windows\.zip$') { $url = $base + $name; $ver = $name }
        else { Log "Неожиданный ответ mmsdrop: '$name'" }
    } catch { Log "mmsdrop недоступен: $($_.Exception.Message)" }
    if (-not $url) {
        $a = Get-GithubAsset 'alliedmodders/metamod-source' 'windows.*\.zip$'
        $url = $a.Url; $ver = "$($a.Tag) ($($a.Name))"
    }
    $zip = Join-Path $Tmp 'metamod.zip'; $ex = Join-Path $Tmp 'metamod'
    Get-File $url $zip; Expand-Zip $zip $ex
    $rootDir = Find-ArchiveRoot $ex
    if (-not $rootDir) { throw 'В архиве Metamod нет папки addons' }
    Copy-Tree (Join-Path $rootDir 'addons') $Addons
    if (-not (Test-Path (Join-Path $Addons 'metamod\bin\win64'))) { throw 'Metamod распакован, но addons\metamod\bin\win64 не найден' }
    return "Metamod:Source $ver"
}

function Install-CSSharp {
    Set-Status 'CounterStrikeSharp'
    $a = Get-GithubAsset 'roflmuffin/CounterStrikeSharp' 'counterstrikesharp-with-runtime.*windows.*\.zip$'
    $zip = Join-Path $Tmp 'css.zip'; $ex = Join-Path $Tmp 'css'
    Get-File $a.Url $zip; Expand-Zip $zip $ex
    $rootDir = Find-ArchiveRoot $ex
    if (-not $rootDir) { throw 'В архиве CounterStrikeSharp нет папки addons' }
    Copy-Tree (Join-Path $rootDir 'addons') $Addons
    if (-not (Test-Path (Join-Path $Addons 'counterstrikesharp\bin\win64'))) { throw 'CounterStrikeSharp распакован, но bin\win64 не найден' }
    return "CounterStrikeSharp $($a.Tag) ($($a.Name))"
}

function Install-CssPlugin([string]$Repo, [string]$Pattern, [string]$PluginName) {
    Set-Status "Плагин $PluginName"
    $a = Get-GithubAsset $Repo $Pattern
    $zip = Join-Path $Tmp "$PluginName.zip"; $ex = Join-Path $Tmp $PluginName
    Get-File $a.Url $zip; Expand-Zip $zip $ex

    # убрать старую версию из обоих мест
    foreach ($d in @((Join-Path $Plugins $PluginName), (Join-Path $PluginsOff $PluginName))) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
    $rootDir = Find-ArchiveRoot $ex
    if ($rootDir) {
        Copy-Tree (Join-Path $rootDir 'addons') $Addons
        if (Test-Path (Join-Path $rootDir 'cfg')) { Copy-Tree (Join-Path $rootDir 'cfg') (Join-Path $Csgo 'cfg') }
    } else {
        $dll = Get-ChildItem $ex -Recurse -Filter "$PluginName.dll" | Select-Object -First 1
        if (-not $dll) { throw "В архиве $($a.Name) не найден $PluginName.dll" }
        Copy-Tree $dll.Directory.FullName (Join-Path $Plugins $PluginName)
        $cfg = Get-ChildItem $ex -Recurse -Directory -Filter 'cfg' | Select-Object -First 1
        if ($cfg) { Copy-Tree $cfg.FullName (Join-Path $Csgo 'cfg') }
    }
    if (-not (Test-Path (Join-Path $Plugins "$PluginName\$PluginName.dll"))) { throw "После установки нет plugins\$PluginName\$PluginName.dll" }
    return "$PluginName $($a.Tag) ($($a.Name))"
}

function Set-GameInfo {
    $gi = Join-Path $Csgo 'gameinfo.gi'
    if (-not (Test-Path $gi)) { throw "Не найден $gi" }
    $txt = [IO.File]::ReadAllText($gi)
    if ($txt -match 'csgo/addons/metamod') { return }
    $new = [regex]::Replace($txt, '(\r?\n)([ \t]*)(Game_LowViolence[ \t]+csgo_lv[^\r\n]*)', '${1}${2}${3}${1}${2}Game' + "`t" + 'csgo/addons/metamod', 1)
    if ($new -eq $txt) { throw 'Не нашёл строку Game_LowViolence csgo_lv в gameinfo.gi — формат файла изменился' }
    [IO.File]::WriteAllText($gi, $new)
    Log 'gameinfo.gi: добавлен путь Metamod'
}

$PracExtra = @'
// --- CS2 Prac begin (grenade practice) ---
sv_cheats 1
sv_infinite_ammo 1
ammo_grenade_limit_total 5
sv_grenade_trajectory_prac_pipreview 1
sv_grenade_trajectory_prac_trailtime 10
sv_showimpacts 1
sv_showimpacts_time 5
mp_buy_anywhere 1
mp_buytime 99999
mp_maxmoney 65535
mp_startmoney 65535
mp_freezetime 0
mp_roundtime 60
mp_roundtime_defuse 60
mp_respawn_on_death_ct 1
mp_respawn_on_death_t 1
mp_limitteams 0
mp_autoteambalance 0
mp_ct_default_grenades "weapon_smokegrenade weapon_flashbang weapon_incgrenade weapon_hegrenade"
mp_t_default_grenades "weapon_smokegrenade weapon_flashbang weapon_molotov weapon_hegrenade"
weapon_auto_cleanup_time 10
// --- CS2 Prac end ---
'@

$MatchZyExtra = @'
// --- CS2 Prac begin (local server: everyone admin, autostart practice) ---
matchzy_everyone_is_admin true
matchzy_autostart_mode 2
matchzy_save_nades_as_global_enabled true
matchzy_show_credits_on_match_start false
matchzy_kick_when_no_match_loaded false
// --- CS2 Prac end ---
'@

function Add-Block([string]$File, [string]$Block) {
    $txt = ''
    if (Test-Path $File) { $txt = [IO.File]::ReadAllText($File) }
    if ($txt -notmatch 'CS2 Prac') { [IO.File]::WriteAllText($File, $txt.TrimEnd() + "`r`n`r`n" + $Block + "`r`n") }
}

function Set-ServerConfigs {
    Set-Status 'Конфиги'
    $mz = Join-Path $Csgo 'cfg\MatchZy'
    New-Item -ItemType Directory -Force -Path $mz | Out-Null
    # Дописываем и в основной файл, и в override — работает при любой логике MatchZy
    foreach ($pair in @(@('config.cfg', 'config_override.cfg', $MatchZyExtra), @('prac.cfg', 'prac_override.cfg', $PracExtra))) {
        $base = Join-Path $mz $pair[0]; $over = Join-Path $mz $pair[1]
        $baseTxt = ''
        if (Test-Path $base) { $baseTxt = [IO.File]::ReadAllText($base) -replace '(?s)// --- CS2 Prac.*?// --- CS2 Prac end ---', '' }
        [IO.File]::WriteAllText($over, $baseTxt.TrimEnd() + "`r`n`r`n" + $pair[2] + "`r`n")
        if (Test-Path $base) { Add-Block $base $pair[2] }
    }
    # Скины: CounterStrikeSharp по умолчанию запрещает менять предметы (FollowCS2ServerGuidelines).
    # Сервер локальный (sv_lan 1, без GSLT), поэтому запрет снимаем.
    $coreDir = Join-Path $Addons 'counterstrikesharp\configs'
    $core = Join-Path $coreDir 'core.json'
    if (-not (Test-Path $core)) {
        $coreEx = Join-Path $coreDir 'core.example.json'
        if (Test-Path $coreEx) { Copy-Item $coreEx $core }
    }
    if (Test-Path $core) {
        $t = [IO.File]::ReadAllText($core)
        $n = [regex]::Replace($t, '("FollowCS2ServerGuidelines"\s*:\s*)true', '${1}false')
        if ($n -ne $t) { [IO.File]::WriteAllText($core, $n); Log 'core.json: FollowCS2ServerGuidelines = false (нужно для скинов)' }
    } else { Log 'core.json не найден: скины могут не работать' }

    # Префаер: оружие выдаёт CS2 Prac (со скинами), поэтому EquipPlayer = 0
    foreach ($dir in @((Join-Path $Plugins 'OpenPrefirePrac'), (Join-Path $PluginsOff 'OpenPrefirePrac'))) {
        if (Test-Path $dir) {
            '{ "Difficulty": 3, "TrainingMode": 0, "BotWeapon": 0, "BotAimLock": 0, "EquipPlayer": 0 }' |
                Set-Content -Path (Join-Path $dir 'default_cfg.json') -Encoding ASCII
        }
    }
}

# ---------- плагин CS2 Prac (табло, проверка раскидок, скины) ----------
function Install-PracPlugin {
    Set-Status 'Плагин CS2 Prac'
    $src = Join-Path $ScriptDir 'plugin\CS2Prac'
    if (-not (Test-Path (Join-Path $src 'CS2Prac.dll'))) { throw "Не найден $src\CS2Prac.dll" }
    $dst = Join-Path $Plugins 'CS2Prac'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    # userdata (свои раскидки, эталоны, выбор скинов) не трогаем
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & robocopy.exe $src $dst /E /XD userdata gamedata /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $old
    if ($code -ge 8) { throw "robocopy плагина завершился с кодом $code" }
    # Куда плагину дублировать журнал бросков (logs рядом с лаунчером) — для диагностики без доступа к папке сервера
    Set-Content -Path (Join-Path $dst 'logdir.txt') -Value $LogDir -Encoding UTF8
    $gd = Join-Path $src 'gamedata\cs2prac.json'
    if (Test-Path $gd) { Copy-Item $gd (Join-Path $Addons 'counterstrikesharp\gamedata\cs2prac.json') -Force }
    $ver = (Get-Item (Join-Path $dst 'CS2Prac.dll')).VersionInfo.FileVersion
    return "CS2 Prac $ver"
}

# ---------- бинды 1–9 в клиенте CS2 ----------
function Get-ClientCfgDir {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $steam = $steam -replace '/', '\'
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libs += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $cfg = Join-Path $l 'steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg'
        if (Test-Path $cfg) { return @{ Cfg = $cfg; Steam = $steam } }
    }
    return $null
}

function Install-ClientBinds {
    Set-Status 'Бинды 1–9 для меню'
    $c = Get-ClientCfgDir
    if (-not $c) { Log 'CS2 не найдена в библиотеках Steam — бинды не записаны.'; return 'Бинды: CS2 не найдена' }
    # Текущие бинды игрока на 1–9, чтобы не затереть свои настройки
    $current = @{}
    $keysFile = Get-ChildItem (Join-Path $c.Steam 'userdata') -Recurse -Filter 'cs2_user_keys_0_slot0.vcfg' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($keysFile) {
        foreach ($m in [regex]::Matches((Get-Content $keysFile.FullName -Raw), '"([1-9])"\s+"([^"]*)"')) { $current[$m.Groups[1].Value] = $m.Groups[2].Value }
    }
    $on = @('// CS2 Prac: цифры 1-9 переключают оружие как обычно и выбирают пункты меню на тренировочном сервере.')
    $off = @('// CS2 Prac: вернуть бинды 1-9 как были до установки.')
    foreach ($n in 1..9) {
        $cur = if ($current.ContainsKey("$n")) { $current["$n"] } else { "slot$n" }
        $base = (($cur -split ';') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^css_\d$' }) -join '; '
        if (-not $base) { $base = "slot$n" }
        $off += "bind `"$n`" `"$base`""
        if ($base -match '(^|;\s*)[+-]') { $on += "bind `"$n`" `"$base`""; Log "Клавиша $n занята командой '$base' — оставлена как есть"; continue }
        $on += "bind `"$n`" `"$base; css_$n`""
    }
    $on += 'echo "CS2 Prac: binds 1-9 ready"'
    [IO.File]::WriteAllLines((Join-Path $c.Cfg 'cs2prac_binds.cfg'), $on)
    [IO.File]::WriteAllLines((Join-Path $c.Cfg 'cs2prac_unbinds.cfg'), $off)
    Log "Бинды записаны: $($c.Cfg)\cs2prac_binds.cfg"
    return 'Бинды 1–9: cs2prac_binds.cfg'
}

function Backup-Nades {
    $cfgDir = Join-Path $Csgo 'cfg'
    if (-not (Test-Path $cfgDir)) { return }
    $files = Get-ChildItem $cfgDir -Recurse -File -Filter '*nade*.json' -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        Copy-Item $f.FullName (Join-Path $BackupDir ("{0}-{1}" -f $stamp, $f.Name))
        Log "Бэкап сохранённых гранат: $($f.Name)"
    }
}

function Set-PluginMode([string]$M) {
    New-Item -ItemType Directory -Force -Path $PluginsOff | Out-Null
    $want = @{ 'MatchZy' = ($M -eq 'smokes'); 'OpenPrefirePrac' = ($M -eq 'prefire') }
    foreach ($p in $want.Keys) {
        $on = Join-Path $Plugins $p; $off = Join-Path $PluginsOff $p
        if ($want[$p]) {
            if (-not (Test-Path $on) -and (Test-Path $off)) { Move-Item $off $on }
            if (-not (Test-Path $on)) { throw "Плагин $p не установлен — запусти «Установить или обновить»" }
        } elseif (Test-Path $on) {
            if (Test-Path $off) { Remove-Item $off -Recurse -Force }
            Move-Item $on $off
        }
    }
    $prac = Join-Path $Plugins 'CS2Prac'
    if (Test-Path $prac) { Set-Content -Path (Join-Path $prac 'mode.txt') -Value $M -Encoding ASCII }
    Log "Режим: $M (MatchZy=$($want['MatchZy']), OpenPrefirePrac=$($want['OpenPrefirePrac']), CS2 Prac всегда включён)"
}

function Test-VCRuntime {
    foreach ($k in @('HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64')) {
        $v = Get-ItemProperty $k -ErrorAction SilentlyContinue
        if ($v -and $v.Installed -eq 1) { return "VC++ x64 runtime $($v.Version)" }
    }
    Log 'ВНИМАНИЕ: не найден Microsoft Visual C++ Redistributable x64 — без него CounterStrikeSharp не запустится. Установи: https://aka.ms/vs/17/release/vc_redist.x64.exe'
    return 'VC++ x64 runtime: НЕ НАЙДЕН'
}

function New-Shortcuts {
    try {
        $desk = [Environment]::GetFolderPath('Desktop')
        $sh = New-Object -ComObject WScript.Shell
        foreach ($s in @(@('CS2 Prac — смоки', '2. Смоки.cmd'), @('CS2 Prac — префаер', '3. Префаер.cmd'))) {
            $lnk = $sh.CreateShortcut((Join-Path $desk ($s[0] + '.lnk')))
            $lnk.TargetPath = Join-Path $ScriptDir $s[1]
            $lnk.WorkingDirectory = $ScriptDir
            if (Test-Path $Cs2Exe) { $lnk.IconLocation = "$Cs2Exe,0" }
            $lnk.Save()
        }
        Log "Ярлыки на рабочем столе созданы ($desk)"
    } catch { Log "Ярлыки не созданы: $($_.Exception.Message)" }
}

# ---------- действия ----------
function Invoke-Update {
    Set-Status "Установка/обновление в $Root"
    $drive = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f (Split-Path $Root -Qualifier))
    $freeGb = [math]::Round($drive.FreeSpace / 1GB, 1)
    Log "Свободно на $($drive.DeviceID): $freeGb ГБ"
    $need = 10; if (-not (Test-Path $Cs2Exe)) { $need = 60 }
    if ($freeGb -lt $need) { throw "Мало места на $($drive.DeviceID): $freeGb ГБ, нужно хотя бы $need ГБ" }

    New-Item -ItemType Directory -Force -Path $Root, $Tmp | Out-Null
    Save-Settings
    Stop-Server
    Backup-Nades

    $versions = @()
    Update-GameFiles
    $versions += Install-Metamod
    $versions += Install-CSSharp
    $versions += Install-CssPlugin 'shobhit-pathak/MatchZy' '^MatchZy[-_]?v?[0-9\.]+\.zip$' 'MatchZy'
    $versions += Install-CssPlugin 'lengran/OpenPrefirePrac' '\.zip$' 'OpenPrefirePrac'
    $versions += Install-PracPlugin
    Set-GameInfo
    Set-ServerConfigs
    Set-PluginMode 'smokes'
    $versions += Install-ClientBinds
    $versions += Test-VCRuntime

    $buildFile = Join-Path $ServerDir 'game\csgo\steam.inf'
    $build = if (Test-Path $buildFile) { (Get-Content $buildFile | Where-Object { $_ -match '^PatchVersion' }) -join ' ' } else { '' }
    $out = @("Обновлено: $(Get-Date -Format 'yyyy-MM-dd HH:mm')", "Папка сервера: $Root", "CS2 $build") + $versions
    $out | Set-Content -Path $VersionsFile -Encoding UTF8
    $out | ForEach-Object { Log $_ }

    New-Shortcuts
    Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
    Set-Status 'ГОТОВО: установка завершена'
}

function Invoke-PluginOnly {
    if (-not (Test-Path (Join-Path $Addons 'counterstrikesharp'))) { throw 'Сначала нужна полная установка: «1. Установить или обновить»' }
    $running = @(Get-ServerProcesses).Count -gt 0
    $v = Install-PracPlugin   # при запущенном сервере CounterStrikeSharp подхватит новую версию сам (hot reload)
    if (-not $running) {
        Set-ServerConfigs
        Set-PluginMode 'smokes'
    } else {
        Log 'Сервер запущен: плагин обновится на лету, режим и конфиги не трогаю'
    }
    $b = Install-ClientBinds
    Log $v; Log $b
    Set-Status 'ГОТОВО: плагин обновлён'
}

function Request-LiveSwitch {
    # Сервер уже запущен — просим плагин сменить режим без перезапуска: игрок остаётся подключённым.
    if (@(Get-ServerProcesses).Count -eq 0) { return $false }
    $prac = Join-Path $Plugins 'CS2Prac'
    if (-not (Test-Path (Join-Path $prac 'CS2Prac.dll'))) { return $false }
    $req = Join-Path $prac 'switch.txt'; $res = Join-Path $prac 'switch_result.txt'
    Remove-Item $res -Force -ErrorAction SilentlyContinue
    Set-Content -Path $req -Value "$Mode $Map" -Encoding ASCII
    Set-Status "Переключаю запущенный сервер: $Mode, $Map"
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $res) {
            $r = (Get-Content $res -Raw).Trim()
            Remove-Item $res -Force -ErrorAction SilentlyContinue
            if ($r -eq 'ok') { Log 'Режим переключён без перезапуска сервера'; return $true }
            Log "Плагин не смог переключить режим ($r) — перезапускаю сервер"
            return $false
        }
    }
    Remove-Item $req -Force -ErrorAction SilentlyContinue
    Log 'Сервер не ответил на переключение — перезапускаю'
    return $false
}

function Connect-Client {
    $client = @(Get-Process -Name cs2 -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and -not $_.Path.StartsWith($ServerDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
    if ($client.Count -gt 0) {
        # Запущенную игру Steam не переподключает (выдаёт «Only one instance»)
        Log 'CS2 уже запущена. Если она не на сервере: Играть → значок глобуса (серверы сообщества) → Избранное → CS2 Prac'
        return
    }
    # steam://connect и steam://run с параметрами при закрытой игре открывают только главное меню,
    # поэтому запускаем через steam.exe -applaunch: так +connect доходит до игры.
    # Заодно каждый раз подгружаем бинды 1–9 для меню.
    $cs2Args = @()
    if (Get-ClientCfgDir) { $cs2Args += @('+exec', 'cs2prac_binds') }
    $cs2Args += @('+connect', '127.0.0.1:27015')
    $steamExe = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe
    Log 'Запускаю CS2 с биндами 1–9. Если игра останется в главном меню: Играть → значок глобуса → Избранное → CS2 Prac'
    if ($steamExe -and (Test-Path $steamExe)) {
        Start-Process -FilePath $steamExe -ArgumentList (@('-applaunch', '730') + $cs2Args)
    } else {
        Start-Process ('steam://run/730//' + (($cs2Args -join ' ') -replace ' ', '%20'))
    }
}

function Invoke-Start {
    if (-not (Test-Path $Cs2Exe) -or -not (Test-Path (Join-Path $Addons 'counterstrikesharp'))) {
        throw 'Сервер ещё не установлен — сначала запусти «1. Установить или обновить»'
    }
    if (Request-LiveSwitch) {
        Set-Status "Сервер переключён: $Mode, $Map"
        if (-not $NoConnect) { Connect-Client }
        return
    }
    Stop-Server
    Set-GameInfo
    Set-PluginMode $Mode
    Backup-Nades
    $console = Join-Path $Csgo 'console.log'
    if (Test-Path $console) { Remove-Item $console -Force -ErrorAction SilentlyContinue }

    $srvArgs = @(
        '-dedicated', '-console', '-condebug',
        '-port', '27015',
        '-maxplayers', '64', '-maxplayers_override', '64',
        '+sv_lan', '1',
        '+hostname', "`"CS2 Prac`"",
        '+sv_hibernate_when_empty', '0',
        '+game_alias', 'competitive',
        '+map', $Map
    )
    Set-Status "Запуск сервера: $Mode, $Map"
    Log ("{0} {1}" -f $Cs2Exe, ($srvArgs -join ' '))
    $p = Start-Process -FilePath $Cs2Exe -ArgumentList $srvArgs -WorkingDirectory (Split-Path $Cs2Exe) -PassThru
    Log "PID сервера: $($p.Id)"

    $ready = $false
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 2
        if ($p.HasExited) { throw "Сервер закрылся сразу после запуска (код $($p.ExitCode)). Запусти «Диагностика»." }
        if (Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue) { $ready = $true; break }
    }
    if (-not $ready) { throw 'Сервер не открыл порт 27015 за 3 минуты. Запусти «Диагностика».' }
    Start-Sleep -Seconds 8
    Set-Status "Сервер запущен: $Mode, $Map"

    if (-not $NoConnect) { Connect-Client }
}

function Invoke-Diag([string]$OutName = 'diag.txt') {
    $d = Join-Path $LogDir $OutName
    $o = New-Object System.Collections.Generic.List[string]
    $o.Add("Диагностика $(Get-Date -Format 's')")
    $o.Add("Папка: $Root")
    $o.Add("cs2.exe: $(Test-Path $Cs2Exe)")
    if (Test-Path $VersionsFile) { $o.AddRange([string[]](Get-Content $VersionsFile)) }
    $gi = Join-Path $Csgo 'gameinfo.gi'
    if (Test-Path $gi) { $o.Add("gameinfo.gi содержит metamod: $([IO.File]::ReadAllText($gi) -match 'csgo/addons/metamod')") }
    $o.Add('Плагины включены: ' + ((Get-ChildItem $Plugins -Directory -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', '))
    $o.Add('Плагины выключены: ' + ((Get-ChildItem $PluginsOff -Directory -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', '))
    $o.Add('Процессы сервера: ' + ((@(Get-ServerProcesses) | ForEach-Object Id) -join ', '))
    $o.Add('UDP 27015: ' + [bool](Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue))
    $console = Join-Path $Csgo 'console.log'
    if (Test-Path $console) {
        $o.Add('--- console.log: строки про плагины ---')
        $lines = Get-Content $console -ErrorAction SilentlyContinue
        $o.AddRange([string[]]@($lines | Where-Object { $_ -match 'META|Metamod|CounterStrikeSharp|MatchZy|Prefire|plugin|error|fail' } | Select-Object -Last 120))
        $o.Add('--- console.log: последние 60 строк ---')
        $o.AddRange([string[]]@($lines | Select-Object -Last 60))
    } else { $o.Add('console.log не найден') }
    $cssLogs = Join-Path $Addons 'counterstrikesharp\logs'
    $recent = Get-ChildItem $cssLogs -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2
    foreach ($f in $recent) {
        $o.Add("--- CSS лог $($f.Name), последние 150 строк ---")
        $o.AddRange([string[]]@(Get-Content $f.FullName -Tail 150 -Encoding UTF8))
    }
    # Журнал бросков (реальных и проверки разметки) — чтобы разбирать промахи раскидок
    $journal = Join-Path $Plugins 'CS2Prac\userdata\throws.jsonl'
    if (Test-Path $journal) {
        Copy-Item $journal (Join-Path $LogDir 'throws.jsonl') -Force
        $o.Add("Журнал бросков скопирован: logs\throws.jsonl ($((Get-Item $journal).Length) байт)")
    }
    $o | Set-Content -Path $d -Encoding UTF8
    Log "Диагностика сохранена: $d"
}

function Invoke-SelfTest {
    Stop-Server   # иначе первый запуск переключит уже запущенный сервер, а не проверит чистый старт
    foreach ($m in @('smokes', 'prefire')) {
        $script:Mode = $m
        $script:NoConnect = $true
        try {
            Invoke-Start
            Start-Sleep -Seconds 30
        } catch { Log "Проверка $m — ошибка запуска: $($_.Exception.Message)" }
        Invoke-Diag "diag-$m.txt"
        Stop-Server
    }
    Set-Status 'ГОТОВО: проверка завершена'
}

try {
    Log "CS2 Prac: $Action (папка сервера: $Root)"
    switch ($Action) {
        'update' { Invoke-Update }
        'plugin' { Invoke-PluginOnly }
        'start'  { Invoke-Start }
        'stop'   { Stop-Server; Set-Status 'Сервер остановлен' }
        'diag'   { Invoke-Diag }
        'selftest' { Invoke-SelfTest }
    }
    exit 0
} catch {
    Set-Status "ОШИБКА: $($_.Exception.Message)"
    Log ($_.ScriptStackTrace)
    exit 1
}
