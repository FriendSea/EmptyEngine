using System.Globalization;
using EmptyEngine.Core;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>スキーマに従ってフィールド値を表示・編集する。</summary>
public sealed class FieldViewModel : ViewModelBase
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private readonly Action<string?>? _onEdited;
    private readonly Func<string, string?>? _assetDisplayResolver;
    private readonly Func<string, string?>? _objectDisplayResolver;
    private FieldViewModel? _parent;
    private long _integer;
    private ulong _unsigned;
    private double _real;
    private bool _bool;
    private string _text = string.Empty;
    private IAssetBinary? _binary;

    public FieldViewModel(AuthoringObjectViewModel owner, string name, FieldTypeInfo declaredType,
        FieldValue? value = null, Action<string?>? onEdited = null)
        : this(name, declaredType, value, onEdited ?? owner.OnEdited,
            owner.AssetDisplayResolver, owner.ObjectDisplayResolver)
    {
        Edits.LinkTo(owner.Edits);
        if (owner.Root is { IsReadOnly: true }) MarkReadOnly();
    }

    internal FieldViewModel(string name, FieldTypeInfo declaredType, FieldValue? value = null,
        Action<string?>? onEdited = null, Func<string, string?>? assetDisplayResolver = null,
        Func<string, string?>? objectDisplayResolver = null)
    {
        Name = name;
        DeclaredType = declaredType;
        _onEdited = onEdited;
        _assetDisplayResolver = assetDisplayResolver;
        _objectDisplayResolver = objectDisplayResolver;
        Apply(value, force: true);
    }

    public string Name { get; private set; }
    public FieldTypeInfo DeclaredType { get; private set; }
    public bool IsPresent { get; private set; }
    public bool IsNull { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool IsBool => DeclaredType.Kind == FieldKind.Bool;
    public bool IsInteger => DeclaredType.Kind is FieldKind.Int or FieldKind.UInt;
    public bool IsNumeric => IsInteger || DeclaredType.Kind is FieldKind.Float32 or FieldKind.Float64;
    public bool IsEnum => DeclaredType.Kind == FieldKind.Enum;
    public bool IsArray => DeclaredType.Kind == FieldKind.Array;
    public bool IsBinary => DeclaredType.Kind == FieldKind.Binary;
    public bool IsAssetReference => DeclaredType.Kind == FieldKind.AssetReference;
    public bool IsObjectReference => DeclaredType.Kind == FieldKind.ObjectReference;
    public bool IsComposite => DeclaredType.Kind is FieldKind.Map or FieldKind.Union && !IsColor;
    public bool IsInlineComposite => IsComposite && Children.Count is > 0 and <= 4
        && Children.All(child => child.Name.Length == 1 && child.DeclaredType.Kind is FieldKind.Float32 or FieldKind.Float64);
    public SnapshotCollection<FieldViewModel> Children { get; } = new();
    public string ArrayLengthText => IsArray ? $"[{Children.Count}]" : string.Empty;
    public string BinaryText => _binary is { } body ? $"{body.Length:N0} bytes" : "(none)";
    public IReadOnlyList<string> EnumOptions => DeclaredType.EnumMembers.Select(member => member.Name).ToArray();
    internal EditGate Edits { get; } = new();
    public FieldViewModel? Get(string name) => Children.FirstOrDefault(child => child.Name == name);

    /// <summary>宣言型ごと取り込む。型が変わっていれば欄を起こし直す。</summary>
    /// <remarks>カタログの焼き直しで宣言が入れ替わったときの入口。入力中は貼り替えず、次の取り込みに任せる。</remarks>
    internal void Rebind(FieldTypeInfo declaredType, FieldValue? value, bool force)
    {
        if (DeclaredType == declaredType)
        {
            Apply(value, force);
            return;
        }

        if (!force && Edits.IsEditing) return;

        DeclaredType = declaredType;
        Apply(value, force: true);
    }

    /// <summary>DTO の値を取り込む。既存の欄は保ち、入力中の値は押し戻さない。</summary>
    public void Apply(FieldValue? value, bool force = false)
    {
        if (!force && Edits.IsEditing) return;
        IsPresent = value is not null;
        IsNull = value?.IsNull == true || DeclaredType.Kind == FieldKind.Nil;
        _integer = value?.Integer ?? 0;
        _unsigned = value?.Unsigned ?? 0;
        _real = value?.Real ?? 0;
        _bool = value?.Bool ?? false;
        _text = value?.Text ?? string.Empty;
        _binary = value?.Binary;

        var next = new List<FieldViewModel>();
        if (IsArray)
        {
            if (value is { IsNull: false })
                for (int i = 0; i < value.Items.Count; i++)
                    Bind($"[{i}]", DeclaredType.Element!, value.Items[i]);
        }
        else if (DeclaredType.Kind == FieldKind.Union)
        {
            if (value is { IsNull: false })
                foreach (FieldEntry entry in value.Entries)
                    Bind(entry.Key, DeclaredType.Member(entry.Key), entry.Value);
        }
        else if (DeclaredType.Members is { } members)
            foreach (var member in members)
                Bind(member.Key, member.Value, value is { IsNull: false } ? value.Get(member.Key) : null);

        foreach (FieldViewModel removed in Children.Except(next)) removed._parent = null;
        if (!Children.SequenceEqual(next)) Children.ReplaceAll(next);
        OnPropertyChanged(string.Empty);

        void Bind(string name, FieldTypeInfo type, FieldValue? data)
        {
            FieldViewModel? child = Get(name);
            if (child is null || child.DeclaredType != type) child = CreateChild(name, type, data);
            else child.Apply(data, force);
            next.Add(child);
        }
    }

    /// <summary>編集値の独立したスナップショットを取得する。未設定の値は補完しない。</summary>
    public FieldValue Capture()
    {
        if (IsNull) return FieldValue.Nil();
        var result = new FieldValue
        {
            Integer = _integer, Unsigned = _unsigned, Real = _real, Bool = _bool, Text = _text, Binary = _binary,
        };
        foreach (FieldViewModel child in Children)
            if (IsArray) result.Items.Add(child.Capture());
            else if (child.IsPresent) result.Add(child.Name, child.Capture());
        return result;
    }

    private FieldViewModel CreateChild(string name, FieldTypeInfo type, FieldValue? value)
    {
        var child = new FieldViewModel(name, type, value, assetDisplayResolver: _assetDisplayResolver,
            objectDisplayResolver: _objectDisplayResolver) { _parent = this };
        child.Edits.LinkTo(Edits);
        if (IsReadOnly) child.MarkReadOnly();
        return child;
    }

    /// <summary>値全体の置換を一つの編集として確定する。</summary>
    public void Replace(FieldValue value)
    {
        if (IsReadOnly) return;
        Apply(value, force: true);
        Changed(materialize: false);
    }

    public void MarkReadOnly()
    {
        IsReadOnly = true;
        foreach (FieldViewModel child in Children) child.MarkReadOnly();
        OnPropertyChanged(nameof(IsReadOnly));
    }

    private void Changed(string? path = null, bool materialize = true)
    {
        if (materialize) { IsPresent = true; IsNull = false; }
        OnPropertyChanged(string.Empty);
        if (_parent is { } parent) parent.Changed(path is null ? Name : $"{Name}/{path}");
        else _onEdited?.Invoke(path ?? Name);
    }

    private void Set<T>(ref T field, T value)
    {
        if (IsReadOnly || (IsPresent && !IsNull && EqualityComparer<T>.Default.Equals(field, value))) return;
        field = value;
        Changed();
    }

    public bool BoolValue { get => _bool; set => Set(ref _bool, value); }
    public string TextValue { get => _text; set => Set(ref _text, value); }
    public long IntegerValue { get => _integer; set => Set(ref _integer, value); }
    public ulong UnsignedValue { get => _unsigned; set => Set(ref _unsigned, value); }
    public double NumericValue
    {
        get => DeclaredType.Kind switch { FieldKind.Int => _integer, FieldKind.UInt => _unsigned, _ => _real };
        set
        {
            if (!double.IsFinite(value)) return;
            switch (DeclaredType.Kind)
            {
                case FieldKind.Int:
                    IntegerValue = value >= long.MaxValue ? long.MaxValue : value <= long.MinValue ? long.MinValue : (long)Math.Round(value);
                    break;
                case FieldKind.UInt:
                    UnsignedValue = value >= ulong.MaxValue ? ulong.MaxValue : value <= 0 ? 0 : (ulong)Math.Round(value);
                    break;
                case FieldKind.Float32 when float.IsFinite((float)value): Set(ref _real, (double)(float)value); break;
                case FieldKind.Float64: Set(ref _real, value); break;
            }
        }
    }

    public string NumericText => DeclaredType.Kind switch
    {
        FieldKind.Int => _integer.ToString(Culture), FieldKind.UInt => _unsigned.ToString(Culture),
        _ => _real.ToString("R", Culture),
    };

    /// <summary>数値文字列を反映する。整数は 64bit の精度を保持する。</summary>
    /// <returns>有効な数値として反映できた場合は <c>true</c>。</returns>
    public bool SetNumericText(string text)
    {
        if (!IsNumeric) return false;
        if (DeclaredType.Kind == FieldKind.Int && long.TryParse(text, NumberStyles.Integer, Culture, out long signed))
            IntegerValue = signed;
        else if (DeclaredType.Kind == FieldKind.UInt && ulong.TryParse(text, NumberStyles.Integer, Culture, out ulong unsigned))
            UnsignedValue = unsigned;
        else if (!IsInteger && double.TryParse(text, NumberStyles.Float, Culture, out double real) && double.IsFinite(real)
            && (DeclaredType.Kind != FieldKind.Float32 || float.IsFinite((float)real)))
            NumericValue = real;
        else return false;
        return true;
    }

    public string EnumValue
    {
        get => DeclaredType.EnumNameOf(_integer);
        set
        {
            if (DeclaredType.EnumMembers.FirstOrDefault(member => member.Name == value) is { } member)
                IntegerValue = member.Value;
            else if (long.TryParse(value, NumberStyles.Integer, Culture, out long number)) IntegerValue = number;
        }
    }

    public double ScrubSensitivity { get; init; }
    public int? AxisHint { get; init; }
    public bool IsScrubbable => IsNumeric;
    public double EffectiveScrubSensitivity => ScrubSensitivity > 0 ? ScrubSensitivity : IsInteger ? 0.2 : 0.05;
    public string DisplayText => IsBinary ? BinaryText : IsBool ? BoolValue.ToString() : IsNumeric ? NumericText
        : IsEnum ? EnumValue : IsColor ? ColorValue.ToHexRgb() + $" (A {ColorValue.A})"
        : IsAssetReference ? AssetReferenceText : IsObjectReference ? ObjectReferenceText : TextValue;

    /// <summary>宣言された順（R, G, B, A）のチャンネル欄。1 つでも引けなければ <c>null</c></summary>
    private IReadOnlyList<FieldViewModel>? Channels
    {
        get
        {
            IReadOnlyList<string> names = DeclaredType.ColorChannels;
            if (names.Count != 4) return null;
            var fields = new FieldViewModel[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                if (Get(names[i]) is not { } channel) return null;
                fields[i] = channel;
            }
            return fields;
        }
    }

    /// <summary>色として描く型か（判断するのはカタログの宣言で、いま入っている値ではない）</summary>
    public bool IsColor => DeclaredType.ColorChannels.Count == 4;
    public EditorColor ColorValue
    {
        get => Channels is { } channels
            ? EditorColor.FromArgb(ToByte(channels[3]._real), ToByte(channels[0]._real), ToByte(channels[1]._real), ToByte(channels[2]._real))
            : default;
        set
        {
            if (IsReadOnly || Channels is not { } channels) return;
            if (IsPresent && !IsNull && channels.All(item => item.IsPresent && !item.IsNull) && ColorValue == value) return;
            double[] values = [value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f];
            for (int i = 0; i < channels.Count; i++)
            {
                channels[i]._real = values[i];
                channels[i].IsPresent = true;
                channels[i].IsNull = false;
                channels[i].OnPropertyChanged(string.Empty);
            }
            Changed();
        }
    }
    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    public string? AssetKey => IsAssetReference ? NonEmpty(TextValue) : null;
    public string? ObjectReferenceId => IsObjectReference ? NonEmpty(TextValue) : null;
    public string AssetReferenceText => AssetKey is { } key ? _assetDisplayResolver?.Invoke(key) ?? key : "(none)";
    public string ObjectReferenceText => ObjectReferenceId is { } key ? _objectDisplayResolver?.Invoke(key) ?? key : "(none)";
    public string? AssetTypeConstraint => IsAssetReference ? NonEmpty(DeclaredType.TypeName) : null;
    public string? ObjectTypeConstraint => IsObjectReference ? NonEmpty(DeclaredType.TypeName) : null;
    public string? AssetTypeConstraintDisplay => AssetTypeConstraint is { } type ? ObjectSchema.ShortNameOf(type) : null;
    public string? ObjectTypeConstraintDisplay => ObjectTypeConstraint is { } type ? ObjectSchema.ShortNameOf(type) : null;
    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    public void AssignAssetReference(AssetKey reference) { if (IsAssetReference && !IsReadOnly) TextValue = reference.Value ?? string.Empty; }
    public void ClearAssetReference() => AssignAssetReference(default);
    public void AssignObjectReference(string targetId) { if (IsObjectReference && !IsReadOnly) TextValue = targetId; }
    public void ClearObjectReference() => AssignObjectReference(string.Empty);

    public void AddArrayElement()
    {
        if (!IsArray || IsReadOnly) return;
        Children.Add(CreateChild($"[{Children.Count}]", DeclaredType.Element!, DeclaredType.Element!.CreateDefault()));
        Changed();
    }

    public void RemoveSelfFromArray() => _parent?.RemoveArrayElement(this);
    public void RemoveArrayElement(FieldViewModel element)
    {
        if (!IsArray || IsReadOnly || !Children.Remove(element)) return;
        element._parent = null;
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Name = $"[{i}]";
            Children[i].OnPropertyChanged(nameof(Name));
        }
        Changed();
    }
}
