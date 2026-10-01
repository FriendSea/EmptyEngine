using System.Numerics;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;

namespace EmptyEngine.Editor.Inspection;

/// <summary><see cref="Vector3"/> 1 つを X/Y/Z の編集欄 3 組として見せる組</summary>
/// <remarks><see cref="Value"/> の読み書きで、X/Y/Z の編集値をまとめて取得・更新できる。</remarks>
public sealed class Vector3FieldGroup
{
    private static readonly string[] AxisNames = ["X", "Y", "Z"];

    private readonly FieldViewModel[] _fields = new FieldViewModel[3];
    private readonly string _editKeyPrefix;
    private readonly Action<string>? _onEdited;

    /// <param name="owner">編集対象。いずれかの欄の操作中はインスペクタの表示更新を抑制する。</param>
    /// <param name="editKeyPrefix">連続した編集を一つの undo にまとめるキーの接頭辞。</param>
    /// <param name="sensitivity">ラベルのドラッグ 1px あたりの増減。</param>
    /// <param name="onEdited">いずれかの欄が編集されたときの通知。引数は編集キー。</param>
    public Vector3FieldGroup(
        AuthoringObjectViewModel owner, string editKeyPrefix, float sensitivity, Action<string>? onEdited = null)
    {
        _editKeyPrefix = editKeyPrefix;
        _onEdited = onEdited;

        for (int axis = 0; axis < 3; axis++)
        {
            int index = axis;
            _fields[index] = new FieldViewModel(
                owner,
                AxisNames[index],
                new FieldTypeInfo("float", FieldKind.Float32),
                new FieldValue { Real = 0 },
                _ => Edited(index))
            {
                ScrubSensitivity = sensitivity,
                AxisHint = index,
            };
        }
    }

    /// <summary>X/Y/Z の編集欄</summary>
    public IReadOnlyList<FieldViewModel> Fields => _fields;

    /// <summary>X/Y/Z の編集欄から取得し、まとめて更新できる値</summary>
    public Vector3 Value
    {
        get => new((float)_fields[0].NumericValue, (float)_fields[1].NumericValue, (float)_fields[2].NumericValue);
        set => SetValue(value);
    }

    /// <summary>表示値の更新。ギズモからの明示的な操作は force で適用する。</summary>
    public void SetValue(Vector3 value, bool force = false)
    {
        for (int axis = 0; axis < 3; axis++)
            _fields[axis].Apply(new FieldValue { Real = Axis(value, axis) }, force);
    }

    private void Edited(int axis)
    {
        _onEdited?.Invoke($"{_editKeyPrefix}.{AxisNames[axis].ToLowerInvariant()}");
    }

    private static float Axis(Vector3 value, int index) => index switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };
}
