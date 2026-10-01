# 型カタログ

[English](type-catalog.md) | 日本語

エディタが、ランタイムのコンポーネントやアセットの型を知るためのファイルの仕様です。

エディタはランタイムのコードを読み込みません。インスペクタに出す欄、「Add Component」の候補、新しく追加したコンポーネントの既定値は、全て型カタログから知ります。型カタログはランタイムのビルドの中で、ランタイムの型から生成してください。

コンポーネントとアセットを[EmptyEngine.ObjectModel](../Modules/EmptyEngine.ObjectModel)の`IAttachable`と`[Asset]`で表す場合は、[EmptyEngine.TypeCatalog](../Modules/EmptyEngine.TypeCatalog)を参照すればビルドのたびに生成されるので、このドキュメントを読む必要はありません。それ以外の場合は自分で生成してください。Rustの型から生成する[BevyRuntimeSample](../samples/BevyRuntimeSample/build.rs)の`build.rs`が参考になります。

## 全体像

形式はJSON Schema（draft 2020-12）です。型1つの記述が`$defs`の1エントリで、コンポーネントやアセットのフィールドは、他の型の記述を`$ref`で指します。

### 置き場所

`TypeCatalog.json`という名前で、プロジェクトの`.artifacts/`に置きます。UTF-8のJSONです。エディタは、自分の実行ファイルの場所から親へ遡って最初に見つかった`.artifacts/`の`TypeCatalog.json`を読みます。

### 書き直し

ランタイムのビルドのたびに、丸ごと書き直してかまいません。エディタは起動したままファイルの変化を検知して読み直し、開いているインスペクタも新しい記述に追従します。書き込みの途中で読んで壊れていた場合や、ファイルが一瞬消えた場合、エディタは直前の内容を使い続けます。

カタログが無くてもエディタは起動しますが、コンポーネントの追加やインスペクタでの編集ができません。

## TypeCatalog.json

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "EmptyEngine type catalog",
  "x-formatVersion": 7,
  "$defs": {
    "MyGame.Player": {
      "x-kind": "struct",
      "title": "Player",
      "x-attachable": true,
      "default": { "Speed": 5, "Mode": 0, "Tint": { "R": 1, "G": 1, "B": 1, "A": 1 } },
      "properties": {
        "Speed": { "$ref": "#/$defs/System.Single" },
        "Mode":  { "$ref": "#/$defs/MyGame.MoveMode" },
        "Tint":  { "$ref": "#/$defs/MyGame.Color" }
      }
    },
    "System.Single": { "x-kind": "float" },
    "MyGame.MoveMode": {
      "x-kind": "enum",
      "anyOf": [ { "const": 0, "title": "Idle" }, { "const": 1, "title": "Run" } ]
    },
    "MyGame.Color": {
      "x-kind": "struct",
      "x-color": ["R", "G", "B", "A"],
      "properties": {
        "R": { "$ref": "#/$defs/System.Single" },
        "G": { "$ref": "#/$defs/System.Single" },
        "B": { "$ref": "#/$defs/System.Single" },
        "A": { "$ref": "#/$defs/System.Single" }
      }
    }
  }
}
```

### トップレベル

| キー | 必須 | 内容 |
|---|---|---|
| `$schema` | ✔ | `https://json-schema.org/draft/2020-12/schema` |
| `x-formatVersion` | ✔ | この仕様のバージョン。現在は`7` |
| `$defs` | ✔ | 型の記述の表。キーが型id |

`$defs`の並び順は、「Add Component」メニューの並び順になります。

### 型id

型idは、型を表す文字列です。**シーンやアセットのデータの中で、その型を表すのに使う識別子**にしてください。エディタは、ランタイムから受け取ったシーンの中の型名で`$defs`を引きます。

エディタは型idを解釈しません。表示名を型idから作ることもないので、表示名は`title`で指定してください。

例えば、リポジトリ内のサンプルでは次の文字列を型idにしています。

| サンプル | 型id | 例 |
|---|---|---|
| DotnetRuntimeSample | 型の完全修飾名 | `MyGame.Player` |
| BevyRuntimeSample | Rustの型パス | `bevy_transform::components::transform::Transform` |

フィールドの型は`{"$ref": "#/$defs/<型id>"}`で指します。`$defs`のキーには型idをそのまま書き、`$ref`はJSON Schemaの規則どおりにエスケープします。

### 型の記述

| キー | 内容 |
|---|---|
| `x-kind` | 種類（下の表）。省略すると`struct` |
| `title` | インスペクタと「Add Component」に出す表示名。省略すると型id |
| `x-attachable` | `true`なら「Add Component」の候補に出す。省略すると`false` |
| `x-assignableTo` | この型を代入できる型idの配列（自分自身、基底、インターフェイス）。アセットピッカーの絞り込みに使う |
| `default` | 既定値（[既定値](#既定値)を参照） |
| `properties` | `struct`のフィールド、または`union`のバリアントのテーブル。各キーに対応する値は`$ref`1つ |
| `anyOf` | `enum`の選択肢。`{"const": 数値, "title": 名前}`の配列で、並び順がドロップダウンの順 |
| `items` | `array`の要素の型の`$ref` |
| `x-target` | 参照が指せる型id（[参照](#参照)を参照） |
| `x-color` | 色を表す`struct`のRGBAのフィールド名を、R、G、B、Aの順に並べた配列 |
| `x-sourcePath`、`x-sourceLine` | 型を宣言しているソースファイルの絶対パスと行番号（1始まり）。インスペクタからソースを開くのに使う |

これ以外のJSON Schemaのキーワード（`type`、`format`など）は、エディタは読みません。JSONの検証に使いたければ自由に書けます。

### 種類

| `x-kind` | 値 | インスペクタ |
|---|---|---|
| `float`、`double` | 数値 | 数値欄 |
| `int`、`uint` | 整数 | 数値欄 |
| `bool` | 真偽値 | チェックボックス |
| `string` | 文字列 | テキスト欄 |
| `struct` | フィールド名をキーとするmap | フィールドごとの欄 |
| `array` | 配列 | 要素を追加・削除できる一覧 |
| `enum` | 数値 | ドロップダウン |
| `union` | タグ1つのmap | 選ばれているバリアントの欄 |
| `assetRef` | アセットのキー | アセットピッカー |
| `objectRef` | シーン内オブジェクトのId | オブジェクトピッカー |
| `binary` | バイト列（下記） | 大きさだけの読み取り専用の行 |

エディタ内の値は、`x-kind`から決まるスカラーの種類（32bit/64bitの浮動小数点数、符号の有無）を持っています。エディタ拡張がシーンやアセットをシリアライズするときは、この種類を見て書き分けられます。

`binary`は、テクスチャのピクセルのような大きなバイト列のフィールドです。エディタは他のフィールドとは別に扱い、インスペクタでは編集できません。どう永続化するかはエディタ拡張が決めます。

### union

いくつかのバリアントのうち1つを選び、そのバリアントが値を持つ型です（Rustのenumなど）。値はタグ1つのmap`{"<タグ>": <値>}`で、値を持たないバリアントは`{"<タグ>": null}`です。

```json
"core::option::Option<glam::Vec2>": {
  "x-kind": "union",
  "properties": {
    "None": true,
    "Some": { "$ref": "#/$defs/glam::Vec2" }
  }
}
```

`properties`はタグ名から値の型への表です。値の型を1つの型idで表せないバリアント（値を持たないもの、複数の値を持つもの）は`true`にしてください。

インスペクタでは、選ばれているバリアントの中身を編集できます。バリアントを切り替えることはできません。

### 参照

`assetRef`と`objectRef`の値は、キーの文字列1つです。未設定は`null`か空文字列です。

`x-target`には、プロパティが指す参照型の型idを書きます。アセットピッカーは、`x-assignableTo`にその型idを含むアセットだけを候補に出します（`x-assignableTo`を持たない型のアセットは候補に残ります）。`x-target`を省略すると、シーンを含むどのアセットでも指せます。

シーンを指すフィールドは、`x-target`を予約値の`@scene`にしてください。ピッカーはシーンだけを候補に出します。`@`で始まる値は予約値で、型idには使えません。

### 色

`x-color`を持つ`struct`は、インスペクタでカラーピッカーになります。指定するフィールドはどれも`properties`にある、0から1の浮動小数点数にしてください。

### 載せる型

シリアライズの対象になる型を、カタログに載せてください。ランタイムの全ての型を載せる必要はありません。例えばEmptyEngine.TypeCatalogは、`IAttachable`を実装する型と`[Asset]`の付いた型から、シリアライズされるメンバの型を再帰的にたどって載せています（参照の先の型はたどりません）。

`properties`には、載せたいフィールドだけを載せればかまいません。`properties`に無いフィールドはインスペクタに出ず、シーンファイルなどにも保存されません。

逆に、ランタイムがシーンのデータの中に持たないフィールドを`properties`に載せることもできます。BevyRuntimeSampleの`Sprite.image`は、シーンの外で運ぶアセット参照をこの方法で編集できるようにしています。

## 既定値

`default`には、その型の値1つを、そのままJSONの値として書きます。スカラーの種類は、フィールドの型の`x-kind`から決まります。

```json
"default": { "Volume": 1, "Clip": null, "Offset": { "X": 0, "Y": 0, "Z": 0 } }
```

既定値の有無で、コンポーネントを追加したときにエディタがランタイムへ送るものが変わります。

| 既定値 | エディタが送るもの | 既定値を入れるのは |
|---|---|---|
| 無し | フィールドを1つも持たない値（空のmap） | ランタイム |
| 有り | 既定値をそのまま写した値 | 型カタログ |

ランタイムが、一部のフィールドが欠けた値を受け取って残りを既定値で埋められるなら、既定値は書かなくてもかまいません。インスペクタには、ランタイムから値が返ってくるまでゼロが表示されます。受け取れないランタイムは、既定値を書いてください。

既定値は、ビルドの中でランタイムと同じコードでその型の値を1つ作り、シリアライズして作成するのが確実です。リポジトリ内の生成の例は次のとおりです。

- EmptyEngine.TypeCatalog：コンストラクタを1回通し、ランタイムと同じ方法でシリアライズします。コンストラクタの参照型の引数には`null`を渡すので、注入された依存の値で決まる既定値は反映されません
- BevyRuntimeSample：`Default::default()`の値を、型カタログの記述をたどりながらJSONへ書き出します

キーが文字列でないmapや128bit整数のように、JSONで正しく表せない値を含む型は、`default`を省略してください。

## 互換性

- エディタは、未知のキーを無視します
- 型カタログに無い型や、`x-kind`が未知の型を指すフィールドは、型の情報なしとして扱い、値の形だけで表示します
- `properties`の順序は保証されません。エディタは文書の順で欄を並べますが、順序に意味を持たせないでください

## おまけ：JSONでの記述の検証

型カタログはJSON Schemaなので、型の値をJSONで記述するファイルを書くときに、`$defs`の型を`$ref`で指せば、テキストエディタで検証と補完を効かせられます。

例えば、[EmptyEngine.SceneSource.Editor](../Modules/EmptyEngine.SceneSource.Editor)の`.asset`ファイル（`TypeName`で型を、`Data`で値を表すJSON）はこれを利用しています。EmptyEngine.TypeCatalogは`TypeCatalog.json`の隣に、`TypeName`の値ごとに`Data`の型（`TypeCatalog.json#/$defs/<型id>`）を選ぶスキーマ`asset.schema.json`を書き出し、`.asset`の`$schema`はエディタが保存のたびにこのファイルへ向けて書き直します。VSCodeでは、`.vscode/settings.json`の`files.associations`で`*.asset`をJSONとして開くようにすれば効きます。
