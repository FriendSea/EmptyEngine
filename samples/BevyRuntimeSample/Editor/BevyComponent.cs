using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace BevyRuntimeSample.Editor;

/// <summary>Bevy 側の実コンポーネント（RON 値）と <see cref="AuthoringObject"/> の相互変換</summary>
internal static class BevyComponent
{
    /// <summary>RON の値 1 個から <see cref="AuthoringObject"/> への変換</summary>
    public static AuthoringObject FromRon(RonValue value, ObjectSchema schema, string? assetKey = null)
    {
        FieldValue data = RonFieldCodec.Decode(value, schema.Root);

        if (!data.IsNull)
        {
            foreach (string field in SyntheticFields(schema))
            {
                if (data.Get(field) is not null) continue;

                data.Add(field, new FieldValue { Text = assetKey ?? string.Empty });
            }
        }

        return new AuthoringObject(schema, data);
    }

    /// <summary><see cref="AuthoringObject"/> から RON 値への復元と合成アセット参照 GUID の取り出し</summary>
    public static (RonValue Value, IReadOnlyList<string> AssetKeys) ToRon(AuthoringObject component)
    {
        FieldValue data = component.Data;

        var body = new FieldValue();
        var assetKeys = new List<string>();

        foreach (FieldEntry entry in data.Entries)
        {
            if (component.Schema.Root.Member(entry.Key).Kind == FieldKind.AssetReference)
            {
                string key = entry.Value.ReferenceKey;
                if (!string.IsNullOrEmpty(key)) assetKeys.Add(key);
                continue;
            }

            body.Entries.Add(entry);
        }

        return (RonFieldCodec.Encode(body, component.Schema.Root), assetKeys);
    }

    /// <summary>スキーマが申告する合成アセット参照フィールドの名前</summary>
    private static IEnumerable<string> SyntheticFields(ObjectSchema schema) =>
        schema.Fields.Where(f => f.Value.Kind == FieldKind.AssetReference).Select(f => f.Key);
}
