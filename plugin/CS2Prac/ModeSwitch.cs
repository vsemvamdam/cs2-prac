using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

/// <summary>
/// Смена режима «смоки ⇄ префаер» без перезапуска сервера: выгружаем один плагин, загружаем другой
/// и перезагружаем карту. Игрок остаётся подключённым — переподключаться через список серверов не нужно.
/// Запустить можно из меню или ярлыком на рабочем столе: лаунчер кладёт запрос в switch.txt.
/// </summary>
public partial class CS2Prac
{
    private bool _switching;

    // Что MatchZy включает в режиме практики и что не сбрасывает смена карты.
    private static readonly string[] ResetAfterPractice =
    {
        "buddha 0", "buddha_ignore_bots 0", "sv_infinite_ammo 0", "sv_showimpacts 0",
        "sv_grenade_trajectory_prac_pipreview 0", "mp_respawn_on_death_ct 0", "mp_respawn_on_death_t 0",
        "mp_buy_anywhere 0", "mp_solid_teammates 1", "mp_free_armor 0", "mp_death_drop_grenade 2",
        "mp_drop_knife_enable 0", "ammo_grenade_limit_total 4", "mp_weapons_allow_typecount 5",
        "mp_ct_default_grenades \"\"", "mp_t_default_grenades \"\"",
        "mp_ct_default_primary \"\"", "mp_t_default_primary \"\"",
    };

    private static string ModeRu(string mode) => mode == "prefire" ? "префаер" : "смоки";

    private string? FindPluginDll(string name)
    {
        var plugins = Path.GetDirectoryName(ModuleDirectory.TrimEnd('\\', '/'));
        if (plugins == null) return null;
        var on = Path.Combine(plugins, name, name + ".dll");
        if (File.Exists(on)) return on;
        // Лаунчер убирает выключенный плагин в <папка сервера>\plugins-off
        for (var dir = Directory.GetParent(plugins); dir != null; dir = dir.Parent)
        {
            var off = Path.Combine(dir.FullName, "plugins-off", name, name + ".dll");
            if (File.Exists(off)) return off;
        }
        return null;
    }

    /// <returns>null — переключение началось, иначе текст ошибки.</returns>
    public string? SwitchMode(string mode, string? map = null)
    {
        if (mode != "smokes" && mode != "prefire") return $"неизвестный режим {mode}";
        if (_switching) return "режим уже переключается";
        if (mode == Mode)
        {
            if (string.IsNullOrWhiteSpace(map) || map.Trim() == Server.MapName) return null;
            Server.ExecuteCommand($"changelevel {map.Trim()}");
            return null;
        }
        var (offModule, onName) = mode == "prefire" ? ("MatchZy", "OpenPrefirePrac") : ("Open Prefire Prac", "MatchZy");
        var onDll = FindPluginDll(onName);
        if (onDll == null) return $"не найден плагин {onName} — запусти «Установить или обновить»";
        map = string.IsNullOrWhiteSpace(map) ? Server.MapName : map.Trim();

        _switching = true;
        try { File.WriteAllText(Path.Combine(ModuleDirectory, "mode.txt"), mode); }
        catch (Exception e) { _switching = false; return $"не записался mode.txt: {e.Message}"; }
        Mode = mode;
        PLog.Info($"Переключение режима: {mode}, карта {map}, загружаю {onDll}");

        foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
        {
            CloseMenu(p);
            p.PrintToChat($" {ChatColors.Gold}CS2 Prac{ChatColors.White} — включаю режим «{ModeRu(mode)}», карта перезагрузится…");
        }

        if (mode == "prefire")
            foreach (var c in ResetAfterPractice) Server.ExecuteCommand(c);
        Server.ExecuteCommand($"css_plugins unload \"{offModule}\"");
        Server.ExecuteCommand("bot_kick");
        Server.ExecuteCommand($"css_plugins load \"{onDll}\"");
        if (mode == "smokes") Server.ExecuteCommand("matchzy_autostart_mode 2"); // после смены карты MatchZy сам включит практику
        AddTimer(1.5f, () =>
        {
            _switching = false;
            Server.ExecuteCommand($"changelevel {map}");
        });
        // Подстраховка: если карта так и не сменилась, не блокируем следующие попытки
        AddTimer(10f, () => _switching = false);
        return null;
    }

    private void SwitchFromMenu(CCSPlayerController p, string mode)
    {
        var err = SwitchMode(mode);
        if (err != null) p.PrintToChat($" {ChatColors.Red}Не получилось: {err}");
    }

    /// <summary>Запрос от лаунчера: файл switch.txt с текстом «prefire de_mirage».</summary>
    private void PollSwitchRequest()
    {
        var req = Path.Combine(ModuleDirectory, "switch.txt");
        if (!File.Exists(req)) return;
        string[] parts;
        try
        {
            parts = File.ReadAllText(req).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            File.Delete(req);
        }
        catch { return; }
        var mode = parts.Length > 0 ? parts[0] : "";
        var map = parts.Length > 1 ? parts[1] : null;
        // Не из колбэка таймера: таймер, созданный внутри другого таймера, может не сработать (так и терялся changelevel)
        Server.NextFrame(() =>
        {
            var err = SwitchMode(mode, map);
            try { File.WriteAllText(Path.Combine(ModuleDirectory, "switch_result.txt"), err == null ? "ok" : "error: " + err); } catch { }
        });
    }

    private void StartSwitchPolling() => AddTimer(1f, PollSwitchRequest, TimerFlags.REPEAT);
}
