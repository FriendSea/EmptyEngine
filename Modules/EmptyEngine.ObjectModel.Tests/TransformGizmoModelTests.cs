using System.Numerics;
using EmptyEngine.ObjectModel.Editor;

using Xunit;

namespace EmptyEngine.ObjectModel.Tests;

/// <summary>ギズモの当たり判定と逆投影の検証</summary>
public class TransformGizmoModelTests
{
    [Fact]
    public void Grabbing_the_center_moves_the_xy_position()
    {
        var gizmo = new TransformGizmoModel();
        int changes = 0;
        gizmo.Changed += () => changes++;

        GizmoPoint center = TransformGizmoModel.Center;
        Assert.True(gizmo.BeginDrag(center.X, center.Y));

        gizmo.DragTo(center.X + 40, center.Y);
        gizmo.EndDrag();

        Assert.True(changes > 0);
        Assert.NotEqual(0d, gizmo.PositionX, precision: 6);
        Assert.False(gizmo.IsDragging);
    }

    [Fact]
    public void Grabbing_a_rotation_knob_dials_that_axis_only()
    {
        var gizmo = new TransformGizmoModel();
        GizmoPoint knob = gizmo.KnobPoint(GizmoAxis.Z);

        Assert.True(gizmo.BeginDrag(knob.X, knob.Y));
        GizmoPoint center = TransformGizmoModel.Center;
        gizmo.DragTo(center.X, center.Y + TransformGizmoModel.RingRadius);
        gizmo.EndDrag();

        Assert.NotEqual(0f, gizmo.RotationDeg.Z);
        Assert.Equal(0f, gizmo.RotationDeg.X);
        Assert.Equal(0f, gizmo.RotationDeg.Y);
    }

    [Fact]
    public void Grabbing_an_axis_tip_scales_that_axis_only()
    {
        var gizmo = new TransformGizmoModel();
        GizmoPoint tip = gizmo.AxisTip(GizmoAxis.X);

        Assert.True(gizmo.BeginDrag(tip.X, tip.Y));
        GizmoPoint center = TransformGizmoModel.Center;
        gizmo.DragTo(center.X + ((tip.X - center.X) * 2), center.Y + ((tip.Y - center.Y) * 2));
        gizmo.EndDrag();

        Assert.True(gizmo.Scale.X > 1.5f);
        Assert.Equal(1f, gizmo.Scale.Y);
        Assert.Equal(1f, gizmo.Scale.Z);
    }

    [Fact]
    public void Empty_space_is_not_a_handle()
    {
        var gizmo = new TransformGizmoModel();

        Assert.False(gizmo.BeginDrag(2, 2));

        gizmo.DragTo(100, 100);
        Assert.Equal(0d, gizmo.PositionX);
        Assert.Equal(0d, gizmo.PositionY);
        Assert.Equal(Vector3.Zero, gizmo.RotationDeg);
        Assert.Equal(Vector3.One, gizmo.Scale);
    }

    [Fact]
    public void SetState_does_not_raise_Changed()
    {
        var gizmo = new TransformGizmoModel();
        int changes = 0;
        gizmo.Changed += () => changes++;

        gizmo.SetState(1, 2, new Vector3(10, 20, 30), new Vector3(2, 2, 2));

        Assert.Equal(0, changes);
        Assert.Equal(new Vector3(10, 20, 30), gizmo.RotationDeg);
    }

    [Fact]
    public void Ring_polyline_closes_on_itself()
    {
        var gizmo = new TransformGizmoModel();

        IReadOnlyList<GizmoPoint> ring = gizmo.RingPolyline(GizmoAxis.Y);

        Assert.True(ring.Count > 2);
        Assert.Equal(ring[0].X, ring[^1].X, precision: 6);
        Assert.Equal(ring[0].Y, ring[^1].Y, precision: 6);
    }
}
