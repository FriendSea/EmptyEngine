// Bevy の登録型から、型カタログとアセット用 JSON Schema を生成する。

use std::any::TypeId;
use std::borrow::Cow;
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use bevy::reflect::std_traits::ReflectDefault;
use bevy::reflect::{Reflect, ReflectRef, Struct, TypeInfo, TypeRegistry, VariantInfo, VariantType};

include!("src/reflect_types.rs");

// Rust 型に存在しない画像参照の編集欄を、カタログに追加する。
const SYNTHETIC_ASSET_FIELDS: &[(&str, &str)] = &[("bevy_sprite::sprite::Sprite", "image")];

// ヒエラルキーの属性は追加用コンポーネントとして表示しない。
const HIERARCHY_OWNED_COMPONENTS: &[&str] = &[
    "emptyengine::ObjectId",
    "bevy_core::name::Name",
    "bevy_render::view::visibility::Visibility",
    "bevy_hierarchy::components::parent::Parent",
];

// 合成アセット参照フィールドの型 id。対応する Rust 型が無いので、型 id もこちらで名乗る（形式上 id は
// 単なるキーで、読み手は中身を解釈しない）。
const SYNTHETIC_ASSET_TYPE_ID: &str = "emptyengine::asset_ref";

// 合成アセット参照が指せる型 id（`x-target`＝ピッカーの絞り込み）。このスタックの非シーンアセットは
// エディタ側の受け皿 1 種類しか無く、それは Rust に対応物が無いので型 id 同様こちらで名乗る。
const SYNTHETIC_ASSET_TARGET: &str = "BevyRuntimeSample.Editor.BevyRawAsset";

// 合成アセット参照の値。実 Rust 型に無い＝reflect 値には現れないので、既定値は常に空の参照。
// 参照はワイヤでもキーの文字列 1 個なので、空は空文字列。
const EMPTY_ASSET_REF: &str = "\"\"";

// reflect に色チャンネルの印がないため、RGBA のメンバ名を列挙する。
const COLOR_CHANNELS: &[(&str, [&str; 4])] = &[
    ("bevy_color::linear_rgba::LinearRgba", ["red", "green", "blue", "alpha"]),
    ("bevy_color::srgba::Srgba", ["red", "green", "blue", "alpha"]),
];

// 入れ子 struct を辿る深さの上限（保険）。
const MAX_FIELD_DEPTH: usize = 6;

// 出力は 2 ファイル。役割で割ってある——型の表は人（とエディタ）が読むもの、.asset のルートは判別子の
// 定型で VSCode しか読まない。1 ファイルに混ぜると、開いたときに型の表が定型に埋もれる。
const CATALOG_FILE: &str = "TypeCatalog.json";
const ASSET_SCHEMA_FILE: &str = "asset.schema.json";

/// カタログに出力する型の記述。
#[derive(Default)]
struct TypeDesc {
    kind: &'static str,
    title: Option<String>,
    attachable: bool,
    /// 既定値の JSON テキスト。
    default_json: Option<String>,
    /// `struct` のフィールド／`union` のバリアント（名前 -> 型 id。型を書けないバリアントは None）。
    members: Vec<(String, Option<String>)>,
    /// `enum` の (メンバ名, 格納値)。
    values: Vec<(String, i64)>,
    /// `array` の要素型 id。
    element: Option<String>,
    /// 参照（`assetRef`）が指せる型 id。
    target: Option<String>,
}

impl TypeDesc {
    fn of(kind: &'static str) -> Self {
        TypeDesc {
            kind,
            ..Default::default()
        }
    }
}

fn main() {
    // 対象型の一覧は reflect_types.rs にしか無いので、build.rs 自身に加えてそちらの変更も拾う。
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=src/reflect_types.rs");

    let mut registry = TypeRegistry::default();
    register_reflect_types(&mut registry);

    // カタログの出力順を安定させるため、型 ID で整列する。
    let mut table: BTreeMap<String, TypeDesc> = BTreeMap::new();

    let mut components: Vec<(String, TypeId)> = registry
        .iter()
        // ReflectComponent が無い＝コンポーネントではなくフィールド値型（Vec2/Vec3/Quat/Color 等）。
        .filter(|registration| registration.data::<ReflectComponent>().is_some())
        // ヒエラルキーの語彙へ畳まれるものは「追加できるコンポーネント」ではない。
        .filter(|registration| {
            !HIERARCHY_OWNED_COMPONENTS.contains(&registration.type_info().type_path())
        })
        .map(|registration| {
            let info = registration.type_info();
            (info.type_path().to_string(), info.type_id())
        })
        .collect();
    components.sort();

    let mut authoring: Vec<String> = Vec::new();
    for (type_path, type_id) in &components {
        let fields = describe_fields(type_path, *type_id, &registry, &mut table, 1);
        table.insert(
            type_path.clone(),
            TypeDesc {
                kind: "struct",
                title: Some(display_name(type_path).to_string()),
                attachable: true,
                members: fields.into_iter().map(|(n, id)| (n, Some(id))).collect(),
                ..Default::default()
            },
        );
        authoring.push(type_path.clone());
    }

    // 既定値は表が完成してから焼く。値の写し方（enum か union か、要素型は何か）は型の記述が決めるので、
    // 記述の途中では引けない。既定値そのものはランタイムと同じクレートの `Default::default()` を 1 回呼ぶだけ。
    for (type_path, type_id) in &components {
        let default_value = registry
            .get(*type_id)
            .unwrap_or_else(|| panic!("{type_path} vanished from the registry"))
            .data::<ReflectDefault>()
            .unwrap_or_else(|| panic!("{type_path} has no ReflectDefault (missing #[reflect(Default)]?)"))
            .default();
        let json = emit_value(&*default_value, type_path, &table);
        table
            .get_mut(type_path)
            .expect("component was just inserted")
            .default_json = Some(json);
    }

    // 出力順: コンポーネント（＝「Add Component」メニュー順）→ 参照された型（id 順）。
    let referenced: Vec<String> = table
        .keys()
        .filter(|id| !authoring.contains(id))
        .cloned()
        .collect();
    let ids: Vec<String> = authoring.into_iter().chain(referenced).collect();

    // .artifacts は既にこのリポジトリの「生成物置き場」規約（.gitignore 済み）なので、そこに合わせる。
    // OUT_DIR（cargo 標準の生成物置き場）はビルドのたびにパスが変わり、.NET 側から素直に見つけられない。
    let out_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".artifacts");
    fs::create_dir_all(&out_dir).expect("create .artifacts dir");
    fs::write(out_dir.join(CATALOG_FILE), render_catalog(&table, &ids)).expect("write type catalog");
    fs::write(out_dir.join(ASSET_SCHEMA_FILE), render_asset_schema(&table, &ids))
        .expect("write asset schema");
}

/// 型の表（`$defs`）1 枚。エディタの読み手が読むのはこちら。
fn render_catalog(table: &BTreeMap<String, TypeDesc>, ids: &[String]) -> String {
    let mut out = String::new();
    out.push_str("{\n");
    out.push_str("  \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",\n");
    out.push_str("  \"title\": \"EmptyEngine type catalog\",\n");
    out.push_str("  \"x-formatVersion\": 7,\n");
    out.push_str("  \"$defs\": {");
    for (index, id) in ids.iter().enumerate() {
        out.push_str(if index == 0 { "\n" } else { ",\n" });
        out.push_str(&render_type(id, &table[id]));
    }
    out.push_str(if ids.is_empty() { "}\n" } else { "\n  }\n" });
    out.push_str("}\n");
    out
}

/// `.asset` ドキュメントの JSON スキーマを返す。型の定義は型カタログへの `$ref` で参照する。
fn render_asset_schema(table: &BTreeMap<String, TypeDesc>, ids: &[String]) -> String {
    // `.asset` のルートに名乗れる型＝名前つきフィールドの map になる型だけ。
    let roots: Vec<&str> = ids
        .iter()
        .filter(|id| table[*id].kind == "struct")
        .map(|id| id.as_str())
        .collect();

    let mut out = String::new();
    out.push_str("{\n");
    out.push_str("  \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",\n");
    out.push_str("  \"title\": \"EmptyEngine asset\",\n");
    out.push_str("  \"type\": \"object\",\n");
    out.push_str("  \"required\": [\"TypeName\"],\n");
    out.push_str("  \"properties\": {\n");
    out.push_str("    \"$schema\": { \"type\": \"string\" },\n");
    out.push_str("    \"TypeName\": {\n      \"type\": \"string\",\n      \"enum\": [");
    for (index, id) in roots.iter().copied().enumerate() {
        out.push_str(if index == 0 { "\n" } else { ",\n" });
        out.push_str(&format!("        \"{}\"", escape(id)));
    }
    out.push_str(if roots.is_empty() { "]\n" } else { "\n      ]\n" });
    out.push_str("    },\n");
    out.push_str("    \"Data\": { \"type\": \"object\" }\n");
    out.push_str("  },\n");

    // TypeName の値で Data のスキーマを切り替える。JSON Schema に判別子は無いので if/then を並べる：
    // TypeName が一致しない枝は if が外れて「制約なし」で素通しになり、一致した 1 枝だけが Data へ型を課す。
    out.push_str("  \"allOf\": [");
    for (index, id) in roots.iter().copied().enumerate() {
        out.push_str(if index == 0 { "\n" } else { ",\n" });
        out.push_str(&format!(
            "    {{ \"if\": {{ \"required\": [\"TypeName\"], \"properties\": {{ \"TypeName\": {{ \"const\": \"{id}\" }} }} }},\n      \"then\": {{ \"properties\": {{ \"Data\": {{ \"$ref\": \"{r}\" }} }} }} }}",
            id = escape(id),
            r = schema_ref_in(CATALOG_FILE, id)
        ));
    }
    out.push_str(if roots.is_empty() { "]\n" } else { "\n  ]\n" });

    out.push_str("}\n");
    out
}

/// 型を JSON Schema の `$defs` エントリとして記述する。スカラーの幅は `x-kind`、参照先の型は `x-target` で表す。
fn render_type(id: &str, desc: &TypeDesc) -> String {
    let mut keys: Vec<String> = Vec::new();
    keys.push(format!("\"type\": \"{}\"", value_shape(desc.kind)));
    if desc.kind == "uint" {
        keys.push("\"minimum\": 0".to_string());
    }
    if desc.kind == "float" || desc.kind == "double" {
        let format = if desc.kind == "float" { "float" } else { "double" };
        keys.push(format!("\"format\": \"{format}\""));
    }
    keys.push(format!("\"x-kind\": \"{}\"", desc.kind));
    if let Some(title) = &desc.title {
        keys.push(format!("\"title\": \"{}\"", escape(title)));
    }
    if desc.attachable {
        keys.push("\"x-attachable\": true".to_string());
    }
    if let Some(element) = &desc.element {
        keys.push(format!("\"items\": {{ \"$ref\": \"{}\" }}", schema_ref(element)));
    }
    if let Some(target) = &desc.target {
        keys.push(format!("\"x-target\": \"{}\"", escape(target)));
    }
    if let Some((_, channels)) = COLOR_CHANNELS.iter().find(|(name, _)| *name == id) {
        let names: Vec<String> = channels.iter().map(|name| format!("\"{}\"", escape(name))).collect();
        keys.push(format!("\"x-color\": [{}]", names.join(", ")));
    }
    if let Some(default_json) = &desc.default_json {
        keys.push(format!("\"default\": {default_json}"));
    }

    // 同じ値を持つ列挙メンバの別名を許可するため、anyOf を使う。
    if !desc.values.is_empty() {
        let choices: Vec<String> = desc
            .values
            .iter()
            .map(|(name, value)| format!("{{ \"const\": {value}, \"title\": \"{}\" }}", escape(name)))
            .collect();
        keys.push(format!("\"anyOf\": [{}]", join_items(&choices, 8)));
    }

    // struct のフィールドも union のバリアントも properties。型参照を持たないものは unit のみ。
    if desc.kind == "struct" || desc.kind == "union" {
        let members: Vec<String> = desc
            .members
            .iter()
            .map(|(name, type_id)| match type_id {
                Some(id) => format!("\"{}\": {{ \"$ref\": \"{}\" }}", escape(name), schema_ref(id)),
                None => format!("\"{}\": true", escape(name)),
            })
            .collect();
        keys.push(format!("\"properties\": {{{}}}", join_items(&members, 8)));
    }

    // union の値はタグ 1 個の map。
    if desc.kind == "union" {
        keys.push("\"minProperties\": 1".to_string());
        keys.push("\"maxProperties\": 1".to_string());
    }

    format!(
        "    \"{}\": {{\n      {}\n    }}",
        escape(id),
        keys.join(",\n      ")
    )
}

/// 値の種類に対応する JSON Schema の型を返す。
fn value_shape(kind: &str) -> &'static str {
    match kind {
        "float" | "double" => "number",
        "int" | "uint" | "enum" => "integer",
        "bool" => "boolean",
        // 参照はキーの文字列 1 個。指せる型は x-target が言う。
        "string" | "assetRef" | "objectRef" => "string",
        "array" => "array",
        // struct / union は map。
        _ => "object",
    }
}

/// struct の全フィールドを「名前 -> 型 id」へ。合成参照で置き換えるもの以外はスキーマ必須。
fn describe_fields(
    type_path: &str,
    type_id: TypeId,
    registry: &TypeRegistry,
    table: &mut BTreeMap<String, TypeDesc>,
    depth: usize,
) -> Vec<(String, String)> {
    let mut fields: Vec<(String, String)> = Vec::new();

    let field_list: Vec<(String, String, TypeId)> = match registry.get(type_id).map(|r| r.type_info()) {
        Some(TypeInfo::Struct(info)) => info
            .iter()
            .map(|field| {
                (
                    field.name().to_string(),
                    field.type_path().to_string(),
                    field.type_id(),
                )
            })
            .collect(),
        _ => Vec::new(),
    };

    for (name, field_type_path, field_type_id) in field_list {
        if SYNTHETIC_ASSET_FIELDS.iter().any(|(owner, field)| *owner == type_path && *field == name) {
            continue;
        }
        let id = ensure(&field_type_path, field_type_id, registry, table, depth)
            .unwrap_or_else(|| panic!("{type_path}.{name} has no authoring schema: {field_type_path}"));
        fields.push((name, id));
    }

    // 実 Rust 型に無い合成フィールドは最後に足す（値ツリーにも現れないので順序の対応も無い）。
    for (owner, field) in SYNTHETIC_ASSET_FIELDS {
        if *owner != type_path {
            continue;
        }

        table.insert(
            SYNTHETIC_ASSET_TYPE_ID.to_string(),
            TypeDesc {
                target: Some(SYNTHETIC_ASSET_TARGET.to_string()),
                ..TypeDesc::of("assetRef")
            },
        );
        fields.push((field.to_string(), SYNTHETIC_ASSET_TYPE_ID.to_string()));
    }

    fields
}

/// 型を表へ入れて型 id を返す。種類へ写せない型は None（＝その型を指すフィールドは載せない）。
fn ensure(
    type_path: &str,
    type_id: TypeId,
    registry: &TypeRegistry,
    table: &mut BTreeMap<String, TypeDesc>,
    depth: usize,
) -> Option<String> {
    if depth > MAX_FIELD_DEPTH {
        return None;
    }
    if table.contains_key(type_path) {
        return Some(type_path.to_string());
    }

    if let Some(kind) = scalar_kind(type_path) {
        table.insert(type_path.to_string(), TypeDesc::of(kind));
        return Some(type_path.to_string());
    }

    // プリミティブでない型は、レジストリに登録されているものだけ記述できる（未登録＝Handle<Image> や
    // Option<Rect> など）。登録は reflect_types.rs が名指しで行う。
    let info = registry.get(type_id)?.type_info();
    let desc = match info {
        TypeInfo::Struct(_) => {
            // 記述の途中で自分自身へ戻ってきても止まるよう、先に枠を置いてから中身を作る。
            table.insert(type_path.to_string(), TypeDesc::of("struct"));
            let fields = describe_fields(type_path, type_id, registry, table, depth + 1);
            TypeDesc {
                kind: "struct",
                members: fields.into_iter().map(|(n, id)| (n, Some(id))).collect(),
                ..Default::default()
            }
        }

        // 全部ユニットバリアントなら enum＝「ワイヤに数値 1 個が載る選択肢」。
        TypeInfo::Enum(enum_info)
            if enum_info.iter().all(|variant| matches!(variant, VariantInfo::Unit(_))) =>
        {
            TypeDesc {
                kind: "enum",
                values: enum_info
                    .iter()
                    .enumerate()
                    .map(|(index, variant)| (variant_name(variant).to_string(), index as i64))
                    .collect(),
                ..Default::default()
            }
        }

        TypeInfo::Enum(enum_info) => {
            table.insert(type_path.to_string(), TypeDesc::of("union"));
            let variants: Vec<(String, Option<String>)> = enum_info
                .iter()
                .map(|variant| describe_variant(variant, registry, table, depth))
                .collect();
            TypeDesc {
                kind: "union",
                members: variants,
                ..Default::default()
            }
        }

        TypeInfo::List(list_info) => TypeDesc {
            kind: "array",
            element: Some(ensure(
                list_info.item_type_path_table().path(),
                list_info.item_type_id(),
                registry,
                table,
                depth + 1,
            )?),
            ..Default::default()
        },

        TypeInfo::Array(array_info) => TypeDesc {
            kind: "array",
            element: Some(ensure(
                array_info.item_type_path_table().path(),
                array_info.item_type_id(),
                registry,
                table,
                depth + 1,
            )?),
            ..Default::default()
        },

        _ => {
            table.remove(type_path);
            return None;
        }
    };

    table.insert(type_path.to_string(), desc);
    Some(type_path.to_string())
}

/// バリアントのペイロードを記述する。unit 以外は型参照が必須。
fn describe_variant(
    variant: &VariantInfo,
    registry: &TypeRegistry,
    table: &mut BTreeMap<String, TypeDesc>,
    depth: usize,
) -> (String, Option<String>) {
    let payload = match variant {
        VariantInfo::Unit(_) => None,
        VariantInfo::Tuple(tuple) if tuple.field_len() == 1 => Some(tuple
            .field_at(0)
            .and_then(|field| ensure(field.type_path(), field.type_id(), registry, table, depth + 1))
            .unwrap_or_else(|| panic!("{} has no payload schema", variant_name(variant)))),
        _ => panic!("{} has an unsupported variant shape", variant_name(variant)),
    };

    (variant_name(variant).to_string(), payload)
}

fn variant_name(variant: &VariantInfo) -> &str {
    match variant {
        VariantInfo::Unit(unit) => unit.name(),
        VariantInfo::Tuple(tuple) => tuple.name(),
        VariantInfo::Struct(compound) => compound.name(),
    }
}

/// 型の記述 `table` に従って値を JSON テキストへ変換する。
/// 記述済みの型の値を変換できない場合は panic する。記述のないフィールドは警告して省略する。
fn emit_value(value: &dyn Reflect, type_path: &str, table: &BTreeMap<String, TypeDesc>) -> String {
    let desc = table
        .get(type_path)
        .unwrap_or_else(|| panic!("no type description for {type_path}; its default value cannot be written"));

    match desc.kind {
        "struct" => {
            let ReflectRef::Struct(source) = value.reflect_ref() else {
                panic!("{type_path} is described as a struct but its value is not one");
            };

            for index in 0..source.field_len() {
                let name = source.name_at(index).unwrap_or_default();
                if !desc.members.iter().any(|(member, _)| member == name) {
                    println!(
                        "cargo:warning={type_path}.{name} is not in the type catalog (its type could not \
                         be described), so it is left out of the default value"
                    );
                }
            }

            let members: Vec<String> = desc
                .members
                .iter()
                .map(|(name, member_id)| {
                    let id = member_id
                        .as_ref()
                        .unwrap_or_else(|| panic!("{type_path}.{name} has no type id"));
                    format!(
                        "\"{}\": {}",
                        escape(name),
                        emit_member(source, name, id, type_path, table)
                    )
                })
                .collect();

            wrap(&members, '{', '}')
        }

        // タグ 1 個の map。値を運ばないバリアントはペイロード null。
        "union" => {
            let ReflectRef::Enum(source) = value.reflect_ref() else {
                panic!("{type_path} is described as a union but its value is not an enum");
            };

            let tag = source.variant_name();
            match source.variant_type() {
                VariantType::Unit => format!("{{ \"{}\": null }}", escape(tag)),
                VariantType::Tuple if source.field_len() == 1 => {
                    let payload = desc
                        .members
                        .iter()
                        .find(|(member, _)| member == tag)
                        .and_then(|(_, id)| id.as_ref())
                        .unwrap_or_else(|| panic!("{type_path}::{tag} carries a payload the catalog cannot type"));
                    let field = source.field_at(0).expect("tuple variant with exactly one field");
                    format!("{{ \"{}\": {} }}", escape(tag), emit_value(field, payload, table))
                }
                _ => panic!("{type_path}::{tag} has a shape the catalog cannot describe"),
            }
        }

        // ユニットバリアントだけの型は「ワイヤに数値 1 個」＝表が振った番号で書く。
        "enum" => {
            let ReflectRef::Enum(source) = value.reflect_ref() else {
                panic!("{type_path} is described as an enum but its value is not one");
            };

            let tag = source.variant_name();
            desc.values
                .iter()
                .find(|(name, _)| name == tag)
                .map(|(_, number)| number.to_string())
                .unwrap_or_else(|| panic!("{type_path}::{tag} is not in the catalog's variant list"))
        }

        "array" => {
            let element = desc
                .element
                .as_ref()
                .unwrap_or_else(|| panic!("{type_path} is described as an array with no element type"));
            let items: Vec<String> = match value.reflect_ref() {
                ReflectRef::List(list) => list.iter().map(|item| emit_value(item, element, table)).collect(),
                ReflectRef::Array(array) => array.iter().map(|item| emit_value(item, element, table)).collect(),
                _ => panic!("{type_path} is described as an array but its value is not one"),
            };

            wrap(&items, '[', ']')
        }

        "assetRef" => EMPTY_ASSET_REF.to_string(),

        kind => emit_scalar(value, type_path, kind),
    }
}

/// struct のメンバを JSON へ変換する。合成されたアセット参照には空の参照を書き出す。
fn emit_member(
    source: &dyn Struct,
    name: &str,
    type_id: &str,
    owner: &str,
    table: &BTreeMap<String, TypeDesc>,
) -> String {
    if table.get(type_id).map(|desc| desc.kind) == Some("assetRef") {
        return EMPTY_ASSET_REF.to_string();
    }

    let field = source
        .field(name)
        .unwrap_or_else(|| panic!("{owner}.{name} is in the type catalog but not in the value"));

    emit_value(field, type_id, table)
}

/// スカラー値を JSON に変換する。整数と浮動小数点数を区別し、値の幅は `x-kind` で表す。
fn emit_scalar(value: &dyn Reflect, type_path: &str, kind: &str) -> String {
    let any = value.as_any();

    // 浮動小数点値の表記が型の拡張で変わらないよう、元の幅で出力する。
    let text = match type_path {
        "f32" => any.downcast_ref::<f32>().map(|v| {
            assert!(v.is_finite(), "default value of {type_path} is not finite ({v})");
            format!("{v:?}")
        }),
        "f64" => any.downcast_ref::<f64>().map(|v| {
            assert!(v.is_finite(), "default value of {type_path} is not finite ({v})");
            format!("{v:?}")
        }),
        "i8" => any.downcast_ref::<i8>().map(|v| v.to_string()),
        "i16" => any.downcast_ref::<i16>().map(|v| v.to_string()),
        "i32" => any.downcast_ref::<i32>().map(|v| v.to_string()),
        "i64" => any.downcast_ref::<i64>().map(|v| v.to_string()),
        "isize" => any.downcast_ref::<isize>().map(|v| v.to_string()),
        "u8" => any.downcast_ref::<u8>().map(|v| v.to_string()),
        "u16" => any.downcast_ref::<u16>().map(|v| v.to_string()),
        "u32" => any.downcast_ref::<u32>().map(|v| v.to_string()),
        "u64" => any.downcast_ref::<u64>().map(|v| v.to_string()),
        "usize" => any.downcast_ref::<usize>().map(|v| v.to_string()),
        "bool" => any.downcast_ref::<bool>().map(|v| v.to_string()),
        "alloc::string::String" => any.downcast_ref::<String>().map(|v| format!("\"{}\"", escape(v))),
        "alloc::borrow::Cow<str>" => any
            .downcast_ref::<Cow<'static, str>>()
            .map(|v| format!("\"{}\"", escape(v))),
        // 128bit は JSON の数値で正確に運べない（i128/u128 を持つ型は既定値を焼けない）。
        _ => None,
    };

    text.unwrap_or_else(|| panic!("cannot write the default value of {type_path} ({kind})"))
}

/// 値の並びを指定した括弧で囲む。
fn wrap(items: &[String], open: char, close: char) -> String {
    if items.is_empty() {
        return format!("{open}{close}");
    }

    format!("{open} {} {close}", items.join(", "))
}

/// 型記述への参照。型 id は JSON Pointer（RFC 6901）の 1 セグメントとして書く。
fn schema_ref(type_id: &str) -> String {
    format!("#/$defs/{}", pointer_token(type_id))
}

/// 隣のファイルの型記述への参照（相対 URI＝同じディレクトリ）。
fn schema_ref_in(document: &str, type_id: &str) -> String {
    format!("{document}{}", schema_ref(type_id))
}

/// 型 ID を JSON Pointer のトークンとしてエスケープし、URI 断片に使える形式で返す。
fn pointer_token(type_id: &str) -> String {
    let escaped = type_id.replace('~', "~0").replace('/', "~1");
    let mut token = String::with_capacity(escaped.len());
    for byte in escaped.bytes() {
        // unreserved / sub-delims / ":" / "@"。"/" はセグメントの区切りなので外す（上で ~1 へ逃がしてある）。
        let safe = byte.is_ascii_alphanumeric()
            || matches!(
                byte,
                b'-' | b'.'
                    | b'_'
                    | b'~'
                    | b'!'
                    | b'$'
                    | b'&'
                    | b'\''
                    | b'('
                    | b')'
                    | b'*'
                    | b'+'
                    | b','
                    | b';'
                    | b'='
                    | b':'
                    | b'@'
            );
        if safe {
            token.push(byte as char);
        } else {
            token.push_str(&format!("%{byte:02X}"));
        }
    }

    token
}

/// 配列/オブジェクト 1 個分の中身（括弧の間）を組む。空なら空文字＝`[]` / `{}`。
fn join_items(items: &[String], indent: usize) -> String {
    if items.is_empty() {
        return String::new();
    }

    let pad = " ".repeat(indent);
    let close = " ".repeat(indent - 2);
    format!("\n{pad}{}\n{close}", items.join(&format!(",\n{pad}")))
}

/// Rust のプリミティブ（と文字列）→ 種類。それ以外は None。
fn scalar_kind(type_path: &str) -> Option<&'static str> {
    Some(match type_path {
        "f32" => "float",
        "f64" => "double",
        "i8" | "i16" | "i32" | "i64" | "i128" | "isize" => "int",
        "u8" | "u16" | "u32" | "u64" | "u128" | "usize" => "uint",
        "bool" => "bool",
        "alloc::string::String" | "str" | "alloc::borrow::Cow<str>" => "string",
        _ => return None,
    })
}

/// "bevy_transform::components::transform::Transform" -> "Transform"。
fn display_name(type_path: &str) -> &str {
    match type_path.rfind("::") {
        Some(index) => &type_path[index + 2..],
        None => type_path,
    }
}

fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}
