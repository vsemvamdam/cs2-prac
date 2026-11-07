using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

/// <summary>
/// «Проверить разметку карты»: сервер сам бросает каждую раскидку так, как её бросил бы игрок,
/// стоящий на точке броска и смотрящий в прицел раскидки, и меряет, далеко ли граната упала от цели.
/// Эталоны не нужны, поэтому так можно проверить всю библиотеку раскидок сразу после патча.
/// Бросок считается по формуле CS (CBaseCSGrenade::ThrowGrenade); константы сверены с реальными бросками
/// и пишутся в журнал userdata/throws.jsonl вместе с каждым броском игрока.
/// </summary>
public partial class CS2Prac
{
    // Модель броска. Меняются командой !simparam для сверки с реальными бросками.
    private static float _eyeHeight = 64.093811f;  // высота глаз стоя
    private static float _throwSpeed = 675f;       // 750 × 0,9 — скорость при полном броске (ЛКМ)
    private static float _srcForward = 22f;        // граната появляется в 22 юн. перед глазами
    private static float _pitchAdjust = 10f;       // прицел поднимается на (90 − |pitch|) × 10/90 градусов
    // Вертикальная скорость игрока, которую получает граната при джамп-тро. Формально sv_jump_impulse = 302, но в CS2
    // раскидки библиотеки ложатся в цель при ~235 (подобрано по Mirage: 10 из 19 джамп-тро в пределах 17 юн.).
    private static float _jumpImpulse = 235f;
    private static float _velInherit = 1.25f;      // граната получает скорость игрока × 1,25
    private static float _gravity = 0.4f;          // GravityScale гранаты (у реальных бросков 0,4)
    private static bool _spawnBack = true;         // точка появления как у настоящего броска CS2

    private SimRun? _sim;

    private sealed class SimRun
    {
        public CCSPlayerController Player = null!;
        public readonly Dictionary<string, float> Dist = new();
        public readonly Dictionary<string, string> Skipped = new();
        public readonly HashSet<string> Done = new();
        public int Total;
        public bool Finished;
    }

    private string JournalPath => Path.Combine(UserDir, "throws.jsonl");

    private void Journal(object rec)
    {
        var line = JsonSerializer.Serialize(rec) + "\n";
        try
        {
            Directory.CreateDirectory(UserDir);
            File.AppendAllText(JournalPath, line);
        }
        catch { }
        // Копия в папку лаунчера (logs), путь к ней лаунчер кладёт в logdir.txt при установке плагина
        try
        {
            var hint = Path.Combine(ModuleDirectory, "logdir.txt");
            if (!File.Exists(hint)) return;
            var dir = File.ReadAllText(hint).Trim();
            if (dir.Length > 0 && Directory.Exists(dir)) File.AppendAllText(Path.Combine(dir, "throws.jsonl"), line);
        }
        catch { }
    }

    private static readonly Regex MovingThrow = new(@"\b(strafe|run|running|step|w\s*\+|walk(ing)?\s+(jump|throw)|forward)\b", RegexOptions.IgnoreCase);

    /// <summary>Сила броска по подсказке: ЛКМ 1, ЛКМ+ПКМ («middle click») 0,5, ПКМ 0.</summary>
    private static float ThrowStrength(Lineup l)
    {
        var d = (l.Title + " " + l.Desc).ToLowerInvariant();
        if (d.Contains("middle click") || d.Contains("middle-click") || d.Contains("left+right") || d.Contains("both click")) return 0.5f;
        if (d.Contains("right click") || d.Contains("right-click") || d.Contains("rmb")) return 0f;
        return 1f;
    }

    /// <summary>Бросок раскидки по модели CS. null — если раскидка требует движения (разбег, стрейф), такое не моделируем.</summary>
    private static ThrowRef? ModelThrow(Lineup l, out string? why, bool? jumpOverride = null)
    {
        why = null;
        if (jumpOverride == null && MovingThrow.IsMatch(l.Title + " " + l.Desc))
        {
            why = "бросок в движении";
            return null;
        }
        var strength = ThrowStrength(l);
        var pitch = l.Aim[0];
        if (pitch > 90f) pitch -= 360f; else if (pitch < -90f) pitch += 360f;
        pitch -= (90f - MathF.Abs(pitch)) * _pitchAdjust / 90f;
        var (fx, fy, fz) = Geo.Dir(pitch, l.Aim[1]);
        var speed = _throwSpeed * Math.Clamp(strength * 0.7f + 0.3f, 0f, 1f);
        var jump = jumpOverride ?? l.JumpThrow;
        var pz = jump ? _jumpImpulse : 0f;
        var vel = new[] { fx * speed, fy * speed, fz * speed + pz * _velInherit };
        // CS2 ставит InitialPosition не в 22 юн. перед глазами, а на той же траектории чуть раньше по времени.
        // Сдвиг назад зависит только от наклона прицела (подогнано по реальным броскам, ошибка < 1 юн.).
        // Для раскидок у стены это важно: граната, появившаяся ближе к игроку, может задеть край.
        var up = Math.Clamp(-l.Aim[0], 0f, 70f);
        var back = _spawnBack ? 0.0066f + 0.00201f * up - 1.3555e-5f * up * up : 0f;
        return new ThrowRef
        {
            Type = l.Type,
            Pos = new[]
            {
                l.Stand[0] + fx * _srcForward - vel[0] * back,
                l.Stand[1] + fy * _srcForward - vel[1] * back,
                l.Stand[2] + _eyeHeight + fz * _srcForward + strength * 12f - 12f - vel[2] * back + (_spawnBack ? 3f : 0f),
            },
            Ang = new float[3],
            Vel = vel,
            AngVel = new[] { 600f, 0f, 0f },
            ItemIndex = l.Type switch { "smoke" => 45, "flash" => 43, "he" => 44, "decoy" => 47, _ => (ushort)(l.Side == "CT" ? 48 : 46) },
            PlayerPos = l.Stand,
            PlayerAng = l.Aim,
            Jumped = jump,
            // Как у настоящей гранаты. Без ActualGravityScale граната, созданная заново, падает в 2,5 раза быстрее.
            Gravity = _gravity,
            ActualGravity = _gravity,
            Friction = 0.2f,
            Elasticity = 0.45f,
        };
    }

    private void StartSim(CCSPlayerController player)
    {
        if (_sim is { Finished: false }) { player.PrintToChat($" {ChatColors.Grey}Проверка разметки уже идёт."); return; }
        CloseMenu(player);
        var run = new SimRun { Player = player };
        _sim = run;
        var queue = new List<(Lineup l, ThrowRef r)>();
        foreach (var l in _community.Where(l => l.Dest != null))
        {
            var r = ModelThrow(l, out var why);
            if (r == null) run.Skipped[l.Id] = why ?? "";
            else queue.Add((l, r));
        }
        run.Total = queue.Count;
        player.PrintToChat($" {ChatColors.Gold}Проверка разметки: {ChatColors.White}сервер бросает {queue.Count} раскидок {_map}" +
                           (run.Skipped.Count > 0 ? $", ещё {run.Skipped.Count} бросаются в движении — их не проверить автоматически" : "") + "…");
        Journal(new { kind = "sim_start", map = _map, build = _build, total = queue.Count, skipped = run.Skipped.Count,
                      model = new { _eyeHeight, _throwSpeed, _srcForward, _pitchAdjust, _jumpImpulse, _velInherit, _gravity, _spawnBack } });
        for (var i = 0; i < queue.Count; i++)
        {
            var (l, r) = queue[i];
            AddTimer(0.2f + 0.35f * i, () =>
            {
                if (_sim != run || run.Finished || !player.IsValid) return;
                var g = Replay(r, player);
                if (g == null) { OnSimLanded(l.Id, null); return; }
                _nades[g.Index] = new TrackedNade { Slot = player.Slot, Type = l.Type, SimId = l.Id };
                WatchSim(g.Index, l.Id, l.Type, run);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
        AddTimer(0.35f * queue.Count + 15f, () => FinishSim(run), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void WatchSim(uint index, string id, string type, SimRun run)
    {
        var started = Server.CurrentTime;
        float[]? last = null;
        var still = 0;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(0.1f, () =>
        {
            if (run.Finished || run.Done.Contains(id)) { timer?.Kill(); return; }
            var e = Utilities.GetEntityFromIndex<CBaseCSGrenadeProjectile>((int)index);
            var age = Server.CurrentTime - started;
            if (e == null || !e.IsValid || e.AbsOrigin == null)
            {
                timer?.Kill();
                if (last != null) { _nades.Remove(index); OnSimLanded(id, last); }
                return;
            }
            var pos = Geo.Arr(e.AbsOrigin);
            bool landed;
            if (type is "flash" or "he") landed = age >= 1.65f;
            else
            {
                still = last != null && Geo.Dist(pos, last) < 0.5f ? still + 1 : 0;
                landed = still >= 3 && age > 0.5f;
            }
            last = pos;
            if (!landed && age < 12f) return;
            timer?.Kill();
            _nades.Remove(index);
            OnSimLanded(id, pos);
            try { e.Remove(); } catch { }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnSimLanded(string id, float[]? det)
    {
        var run = _sim;
        var l = FindLineup(id);
        if (run == null || run.Finished || l?.Dest == null || !run.Done.Add(id)) return;
        var dist = det == null ? -1f : Geo.Dist(det, l.Dest);
        run.Dist[id] = dist;
        // Итог проверки виден на подписи раскидки ([OK] / [МИМО]) и в подсказке при телепорте.
        // Если игрок уже точно бросал эту раскидку (есть эталон), реальный бросок важнее — его не трогаем.
        var c = CheckOf(id);
        if (c.Ref == null)
        {
            c.Status = dist >= 0 && dist <= Math.Max(l.Threshold, 60f) ? "ok" : "miss";
            c.LastDistance = dist;
            c.CheckedBuild = _build;
            c.CheckedAt = DateTime.UtcNow;
        }
        Journal(new { kind = "sim", map = _map, id, title = l.Title, type = l.Type, side = l.Side, jump = l.JumpThrow,
                      strength = ThrowStrength(l), det, dest = l.Dest, dist, threshold = l.Threshold });
        if (run.Done.Count >= run.Total) FinishSim(run);
    }

    private void FinishSim(SimRun run)
    {
        if (run.Finished) return;
        run.Finished = true;
        SaveChecks();
        RefreshLabels();
        ClearSmokes();
        var bad = run.Dist.Where(kv => kv.Value < 0 || kv.Value > Math.Max(FindLineup(kv.Key)?.Threshold ?? 80f, 60f))
                          .OrderByDescending(kv => kv.Value).ToList();
        var ok = run.Dist.Count - bad.Count;
        var lost = run.Total - run.Dist.Count;
        PLog.Info($"Проверка разметки {_map}: попали {ok}, мимо {bad.Count}, потеряны {lost}, в движении {run.Skipped.Count}");
        Journal(new { kind = "sim_done", map = _map, ok, miss = bad.Count, lost, skipped = run.Skipped.Count });
        var p = run.Player;
        if (!p.IsValid) return;
        p.PrintToChat($" {ChatColors.Gold}Разметка {_map}: {ChatColors.Green}попадают {ok}{ChatColors.White}, {ChatColors.Red}мимо {bad.Count + lost}" +
                      $"{ChatColors.White}, в движении (не проверялись) {run.Skipped.Count}.");
        var page = new MenuPage
        {
            Title = "Разметка карты",
            Sub = $"попадают {ok} · мимо {bad.Count + lost} · в движении {run.Skipped.Count}",
            Back = () => ToolsPage(p),
        };
        foreach (var (id, d) in bad)
        {
            var l = FindLineup(id);
            if (l == null) continue;
            page.Add(d >= 0 ? $"{l.Title} ({d:0} юн.)" : $"{l.Title} (не упала)", pl => GoToLineup(pl, l), Color.FromArgb(255, 255, 110, 110));
        }
        if (bad.Count == 0) page.Add("Все проверенные раскидки попадают в цель", null, Color.FromArgb(255, 80, 255, 120), true);
        OpenMenu(p, page);
    }

    /// <summary>!simparam [имя значение] — константы модели броска (для сверки с реальными бросками).</summary>
    private void OnSimParam(CCSPlayerController? p, CommandInfo info)
    {
        if (info.ArgCount >= 3 && float.TryParse(info.GetArg(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            switch (info.GetArg(1).ToLowerInvariant())
            {
                case "eye": _eyeHeight = v; break;
                case "speed": _throwSpeed = v; break;
                case "fwd": _srcForward = v; break;
                case "pitch": _pitchAdjust = v; break;
                case "jump": _jumpImpulse = v; break;
                case "inherit": _velInherit = v; break;
                case "grav": _gravity = v; break;
                case "back": _spawnBack = v != 0; break;
            }
        }
        info.ReplyToCommand($"eye {_eyeHeight} speed {_throwSpeed} fwd {_srcForward} pitch {_pitchAdjust} jump {_jumpImpulse} inherit {_velInherit} grav {_gravity} back {_spawnBack}");
    }
}
