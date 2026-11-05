using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Prac;

/// <summary>Текст в мире (point_worldtext): табло, подписи у раскидок, маркеры целей.</summary>
public static class WorldText
{
    public static CPointWorldText? Spawn(
        string text, Vector pos, QAngle ang, Color color,
        float fontSize, float unitsPerPx,
        bool background = false, bool billboard = false,
        PointWorldTextJustifyHorizontal_t justify = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER,
        PointWorldTextJustifyVertical_t vjustify = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_TOP)
    {
        try
        {
            var e = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (e == null) return null;
            e.MessageText = text.Length > 250 ? text[..250] : text;
            e.Enabled = true;
            e.FontSize = fontSize;
            e.Fullbright = true;
            e.WorldUnitsPerPx = unitsPerPx;
            e.DepthOffset = 0f;
            e.Color = color;
            e.JustifyHorizontal = justify;
            e.JustifyVertical = vjustify;
            e.ReorientMode = billboard
                ? PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_AROUND_UP
                : PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            if (background)
            {
                e.DrawBackground = true;
                e.BackgroundBorderWidth = 3f;
                e.BackgroundBorderHeight = 1.5f;
            }
            e.Teleport(pos, ang, null);
            e.DispatchSpawn();
            return e;
        }
        catch (Exception ex)
        {
            PLog.Info($"point_worldtext: {ex.Message}");
            return null;
        }
    }

    public static void Kill(IList<CPointWorldText> list)
    {
        foreach (var e in list)
        {
            try { if (e is { IsValid: true }) e.Remove(); } catch { }
        }
        list.Clear();
    }

    /// <summary>Углы, при которых табло читается игроком, смотрящим по направлению yaw.</summary>
    public static QAngle FacingViewer(float yaw) => new(0, yaw + 270f, 90f);

    /// <summary>Углы для надписи, лежащей на полу и читаемой игроком, смотрящим по направлению yaw.</summary>
    public static QAngle OnFloor(float yaw) => new(0, yaw + 270f, 0f);
}

public static class Geo
{
    public static Vector Vec(float[] a) => new(a[0], a[1], a[2]);
    public static QAngle Ang(float[] a) => new(a[0], a[1], a[2]);
    public static float[] Arr(Vector v) => new[] { v.X, v.Y, v.Z };
    public static float[] Arr(QAngle a) => new[] { a.X, a.Y, a.Z };
    public static Vector Add(Vector a, float x, float y, float z) => new(a.X + x, a.Y + y, a.Z + z);

    public static float Dist(float[] a, float[] b)
    {
        float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static float Dist2D(float[] a, float[] b)
    {
        float dx = a[0] - b[0], dy = a[1] - b[1];
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static (float x, float y) Forward2D(float yawDeg)
    {
        var r = yawDeg * MathF.PI / 180f;
        return (MathF.Cos(r), MathF.Sin(r));
    }

    /// <summary>Единичный вектор взгляда по углам pitch/yaw.</summary>
    public static (float x, float y, float z) Dir(float pitchDeg, float yawDeg)
    {
        var p = pitchDeg * MathF.PI / 180f;
        var y = yawDeg * MathF.PI / 180f;
        return (MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p));
    }
}
