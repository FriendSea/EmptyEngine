using System.Numerics;

namespace EmptyEngine.ObjectModel.Editor;

/// <summary>ギズモ上の 1 点（サーフェス座標）</summary>
public readonly record struct GizmoPoint(double X, double Y);

/// <summary>ギズモが扱う軸</summary>
public enum GizmoAxis
{
    X,
    Y,
    Z,
}

/// <summary>インスペクタ内に表示する 3D トランスフォーム・ギズモの状態と幾何</summary>
public sealed class TransformGizmoModel
{
    /// <summary>描画面の 1 辺（px）</summary>
    public const double Surface = 200;

    /// <summary>回転リングの半径（px）</summary>
    public const double RingRadius = 78;

    /// <summary>ハンドルの当たり判定半径（px）</summary>
    public const double HitRadius = 11;

    /// <summary>平行移動ハンドルの当たり判定半径（px）</summary>
    public const double CenterHitRadius = 16;

    private const double Cx = Surface / 2;
    private const double Cy = Surface / 2;
    private const double AxisLength = 46;
    private const double PixelsPerUnit = 36;
    private const double MinProjectedAxis = 0.2;
    private const int RingSegments = 64;

    private static readonly Matrix4x4 View =
        Matrix4x4.CreateRotationY(-35f * (MathF.PI / 180f))
        * Matrix4x4.CreateRotationX(25f * (MathF.PI / 180f));

    private enum Handle
    {
        None,
        Center,
        XTip,
        YTip,
        ZTip,
        RotateX,
        RotateY,
        RotateZ,
    }

    private Handle _active = Handle.None;
    private double _startMouseX;
    private double _startMouseY;
    private double _startPosX;
    private double _startPosY;

    /// <summary>ハンドル操作で TRS が変わったときの発火</summary>
    public event Action? Changed;

    public double PositionX { get; private set; }
    public double PositionY { get; private set; }

    /// <summary>オイラー角（度）</summary>
    public Vector3 RotationDeg { get; private set; }

    public Vector3 Scale { get; private set; } = Vector3.One;

    /// <summary>いずれかのハンドルをドラッグ中か</summary>
    public bool IsDragging => _active != Handle.None;

    /// <summary>ギズモの中心（平行移動ハンドルの位置）</summary>
    public static GizmoPoint Center => new(Cx, Cy);

    /// <summary>外部からの変更の反映</summary>
    public void SetState(double posX, double posY, Vector3 rotationDeg, Vector3 scale)
    {
        PositionX = posX;
        PositionY = posY;
        RotationDeg = rotationDeg;
        Scale = scale;
    }

    /// <summary>指定座標でのハンドルの掴み取り</summary>
    public bool BeginDrag(double x, double y)
    {
        _active = HitTest(new GizmoPoint(x, y));
        if (_active == Handle.None) return false;

        _startMouseX = x;
        _startMouseY = y;
        _startPosX = PositionX;
        _startPosY = PositionY;
        return true;
    }

    /// <summary>掴んだままの移動</summary>
    public void DragTo(double x, double y)
    {
        if (_active == Handle.None) return;

        var offset = new GizmoPoint(x - Cx, y - Cy);
        switch (_active)
        {
            case Handle.Center:
            {
                var delta = new GizmoPoint(x - _startMouseX, y - _startMouseY);
                if (TrySolve(Project(Vector3.UnitX), Project(Vector3.UnitY), delta, out double u, out double v))
                {
                    PositionX = _startPosX + (u / PixelsPerUnit);
                    PositionY = _startPosY + (v / PixelsPerUnit);
                }

                break;
            }

            case Handle.RotateX:
            case Handle.RotateY:
            case Handle.RotateZ:
            {
                (Vector3 e1, Vector3 e2) = RingBasis(_active);
                if (TrySolve(Project(e1), Project(e2), offset, out double u, out double v))
                {
                    float deg = NormalizeDegrees(Math.Atan2(v, u) * (180.0 / Math.PI));
                    RotationDeg = _active switch
                    {
                        Handle.RotateX => RotationDeg with { X = deg },
                        Handle.RotateY => RotationDeg with { Y = deg },
                        _ => RotationDeg with { Z = deg },
                    };
                }

                break;
            }

            case Handle.XTip:
            case Handle.YTip:
            case Handle.ZTip:
            {
                int index = _active - Handle.XTip;
                GizmoPoint dir = Project(LocalAxes()[index]);
                double len = Math.Sqrt((dir.X * dir.X) + (dir.Y * dir.Y));
                if (len < MinProjectedAxis) break;

                double projected = ((offset.X * dir.X) + (offset.Y * dir.Y)) / len;
                float scale = (float)Math.Max(projected / (AxisLength * len), 0.01);
                Scale = index switch
                {
                    0 => Scale with { X = scale },
                    1 => Scale with { Y = scale },
                    _ => Scale with { Z = scale },
                };
                break;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>ドラッグを終える</summary>
    public void EndDrag() => _active = Handle.None;

    /// <summary>回転リングの折れ線としての取得</summary>
    public IReadOnlyList<GizmoPoint> RingPolyline(GizmoAxis axis)
    {
        (Vector3 e1, Vector3 e2) = RingBasis(RingHandle(axis));
        var points = new GizmoPoint[RingSegments + 1];
        for (int i = 0; i <= RingSegments; i++)
        {
            double a = i * (Math.PI * 2.0 / RingSegments);
            points[i] = ScreenPoint((e1 * (float)Math.Cos(a)) + (e2 * (float)Math.Sin(a)), RingRadius);
        }

        return points;
    }

    /// <summary>回転リングのノブ（そのオイラー角のダイヤル）の位置</summary>
    public GizmoPoint KnobPoint(GizmoAxis axis) => KnobPoint(RingHandle(axis));

    /// <summary>回転を反映したローカル軸の先端（スケールハンドル）の位置</summary>
    public GizmoPoint AxisTip(GizmoAxis axis)
    {
        int index = (int)axis;
        float scale = index switch { 0 => Scale.X, 1 => Scale.Y, _ => Scale.Z };
        return ScreenPoint(LocalAxes()[index], AxisLength * scale);
    }

    private static Handle RingHandle(GizmoAxis axis) => axis switch
    {
        GizmoAxis.X => Handle.RotateX,
        GizmoAxis.Y => Handle.RotateY,
        _ => Handle.RotateZ,
    };

    private static GizmoPoint Project(Vector3 v)
    {
        Vector3 p = Vector3.Transform(v, View);
        return new GizmoPoint(p.X, -p.Y);
    }

    private static GizmoPoint ScreenPoint(Vector3 v, double radius)
    {
        GizmoPoint p = Project(v);
        return new GizmoPoint(Cx + (radius * p.X), Cy + (radius * p.Y));
    }

    private static (Vector3 E1, Vector3 E2) RingBasis(Handle ring) => ring switch
    {
        Handle.RotateX => (Vector3.UnitY, Vector3.UnitZ),
        Handle.RotateY => (Vector3.UnitZ, Vector3.UnitX),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    private double RingAngleRad(Handle ring) => ring switch
    {
        Handle.RotateX => RotationDeg.X * (Math.PI / 180.0),
        Handle.RotateY => RotationDeg.Y * (Math.PI / 180.0),
        _ => RotationDeg.Z * (Math.PI / 180.0),
    };

    private GizmoPoint KnobPoint(Handle ring)
    {
        (Vector3 e1, Vector3 e2) = RingBasis(ring);
        double a = RingAngleRad(ring);
        return ScreenPoint((e1 * (float)Math.Cos(a)) + (e2 * (float)Math.Sin(a)), RingRadius);
    }

    private Vector3[] LocalAxes()
    {
        Matrix4x4 r = EulerRotation.FromDegrees(RotationDeg);
        return
        [
            new Vector3(r.M11, r.M12, r.M13),
            new Vector3(r.M21, r.M22, r.M23),
            new Vector3(r.M31, r.M32, r.M33),
        ];
    }

    private Handle HitTest(GizmoPoint m)
    {
        if (Distance(m, KnobPoint(Handle.RotateX)) <= HitRadius) return Handle.RotateX;
        if (Distance(m, KnobPoint(Handle.RotateY)) <= HitRadius) return Handle.RotateY;
        if (Distance(m, KnobPoint(Handle.RotateZ)) <= HitRadius) return Handle.RotateZ;

        if (Distance(m, AxisTip(GizmoAxis.X)) <= HitRadius) return Handle.XTip;
        if (Distance(m, AxisTip(GizmoAxis.Y)) <= HitRadius) return Handle.YTip;
        if (Distance(m, AxisTip(GizmoAxis.Z)) <= HitRadius) return Handle.ZTip;

        if (Distance(m, Center) <= CenterHitRadius) return Handle.Center;
        return Handle.None;
    }

    private static bool TrySolve(GizmoPoint col1, GizmoPoint col2, GizmoPoint rhs, out double u, out double v)
    {
        double det = (col1.X * col2.Y) - (col2.X * col1.Y);
        if (Math.Abs(det) < 1e-6)
        {
            u = v = 0;
            return false;
        }

        u = ((rhs.X * col2.Y) - (col2.X * rhs.Y)) / det;
        v = ((col1.X * rhs.Y) - (rhs.X * col1.Y)) / det;
        return true;
    }

    private static double Distance(GizmoPoint a, GizmoPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static float NormalizeDegrees(double deg)
    {
        deg %= 360.0;
        if (deg > 180.0) deg -= 360.0;
        if (deg < -180.0) deg += 360.0;
        return (float)deg;
    }
}
