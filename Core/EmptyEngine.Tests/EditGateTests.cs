using System.Numerics;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Inspection;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>触っている最中の欄を、ランタイムからの配り直しで押し戻さないこと</summary>
public sealed class EditGateTests
{
    private static FieldViewModel NumberField(double initial = 0d)
    {
        return new FieldViewModel("N", TestSchemas.Scalar("System.Single"), new FieldValue { Real = initial });
    }

    /// <remarks>掴んでいる間は時間で開かない。手が止まってもドラッグは続いている</remarks>
    [Fact]
    public void Runtime_values_are_ignored_until_the_field_is_released()
    {
        FieldViewModel field = NumberField();
        field.Edits.Hold();
        field.Apply(new FieldValue { Real = 9 });
        Assert.Equal(0d, field.NumericValue);

        field.Edits.Release();
        field.Apply(new FieldValue { Real = 9 });
        Assert.Equal(9d, field.NumericValue);
    }

    /// <summary>複数スレッドから編集の保持・解除を行っても、すべての解除後に更新できることを検証する</summary>
    [Fact]
    public async Task Overlapping_worker_holds_keep_the_gate_closed_until_both_release()
    {
        var gate = new EditGate();
        using var held = new CountdownEvent(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Worker(Task release) => Task.Run(async () =>
        {
            await start.Task;
            gate.Hold();
            held.Signal();
            await release;
            gate.Release();
        });
        Task first = Worker(releaseFirst.Task);
        Task last = Worker(releaseLast.Task);
        try
        {
            start.SetResult();
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(gate.IsEditing);

            releaseFirst.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.IsEditing);

            releaseLast.SetResult();
            await last.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gate.IsEditing);
        }
        finally
        {
            start.TrySetResult();
            releaseFirst.TrySetResult();
            releaseLast.TrySetResult();
            await Task.WhenAll(first, last).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <remarks>対より多く離しても勘定を負にしないこと。負にすると次の掴みが効かない</remarks>
    [Fact]
    public void An_unmatched_release_does_not_spoil_the_next_hold()
    {
        var gate = new EditGate();
        gate.Release();

        gate.Hold();

        Assert.True(gate.IsEditing);
    }

    /// <remarks>掴みは入れ子になる（欄そのものと、それを含む組の両方が掴む）ので、
    /// 最後のひとつが離すまで閉じたまま</remarks>
    [Fact]
    public void Nested_holds_keep_the_gate_shut_until_the_last_one_lets_go()
    {
        var gate = new EditGate();
        gate.Hold();
        gate.Hold();

        gate.Release();
        Assert.True(gate.IsEditing);

        gate.Release();
        Assert.False(gate.IsEditing);
    }

    /// <remarks>離しすぎても負にならない。戻し損ねより二重に戻すほうが起きやすい</remarks>
    /// <summary>欄を掴むと、それを出しているコンポーネント全体が閉じること</summary>
    [Fact]
    public void Holding_one_axis_stops_the_whole_inspector_from_re_reading()
    {
        AuthoringObjectViewModel component = Probe();
        var group = new Vector3FieldGroup(component, "pos", 0.01f);

        Assert.False(component.IsUserEditing);

        group.Fields[1].Edits.Hold();
        Assert.True(component.IsUserEditing);

        group.Fields[1].Edits.Release();
        Assert.False(component.IsUserEditing);
    }

    /// <summary>専用インスペクタが読み直す値を、掴んでいる間は押し戻さないこと</summary>
    [Fact]
    public void A_held_axis_survives_a_push_from_the_group()
    {
        AuthoringObjectViewModel component = Probe();
        var group = new Vector3FieldGroup(component, "pos", 0.01f);

        group.Fields[0].Edits.Hold();
        group.Value = new Vector3(5f, 6f, 7f);

        Assert.Equal(0d, group.Fields[0].NumericValue);
        Assert.Equal(6d, group.Fields[1].NumericValue);
    }

    /// <summary>持ち主を渡して作った欄が、掴んでいる間そのコンポーネントを閉じること</summary>
    [Fact]
    public void A_field_made_for_a_component_shuts_it_while_held()
    {
        AuthoringObjectViewModel component = Probe();
        var field = new FieldViewModel(component, "N", TestSchemas.Scalar("System.Single"), new FieldValue());

        field.Edits.Hold();
        Assert.True(component.IsUserEditing);

        field.Edits.Release();
        Assert.False(component.IsUserEditing);
    }

    /// <summary>複合 struct の中の 1 メンバでもコンポーネント全体が閉じること</summary>
    [Fact]
    public void A_nested_member_shuts_the_whole_component()
    {
        AuthoringObjectViewModel component = Probe();
        FieldViewModel field = CompositeField(component, "V", "X", "Y", "Z");

        FieldViewModel member = Assert.Single(field.Children, child => child.Name == "Y");
        member.Edits.Hold();

        Assert.True(field.Edits.IsEditing);
        Assert.True(component.IsUserEditing);
    }

    /// <summary>欄を作る公開の口が、持ち主を必ず取ること</summary>
    private static AuthoringObjectViewModel Probe(Action<string?>? onEdited = null) =>
        new(
            "Probe", "Probe",
            new AuthoringObject(TestSchemas.Object("Probe"), new FieldValue()),
            onEdited ?? (_ => { }));

    /// <summary>数値メンバを持つ複合 struct の欄</summary>
    private static FieldViewModel CompositeField(AuthoringObjectViewModel owner, string name, params string[] members)
    {
        var value = new FieldValue();
        foreach (string member in members)
            value.Add(member, new FieldValue() { Real = 0 });

        var data = new FieldValue();
        data.Add(name, value);

        var declared = new FieldTypeInfo("Probe.Axes", FieldKind.Map)
        {
            Members = members.ToDictionary(
                m => m,
                _ => new FieldTypeInfo("System.Single", FieldKind.Float32),
                StringComparer.Ordinal),
        };

        var schema = new ObjectSchema
        {
            TypeName = "Probe",
            DisplayName = "Probe",
            Fields = new Dictionary<string, FieldTypeInfo>(StringComparer.Ordinal) { [name] = declared },
        };

        return new FieldViewModel(owner, name, declared, value);
    }
}
