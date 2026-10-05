# エディタ拡張

[English](editor-extension.md) | 日本語

エディタ拡張は、利用者のランタイムに合わせてエディタの振る舞いを決めるC#のコードです。シーンをどんなバイト列でランタイムへ送るか、ソースアセットをどう取り込んでどこへ置くか、などを決めます。

[Modules](../Modules)以下には、ランタイムとエディタ拡張の実装例がモジュールとして入っています。その実装例を使う場合は、参照するだけで済みます（[DotnetRuntimeSample](../samples/DotnetRuntimeSample/Editor/Editor.targets)）。エディタ拡張を自分で実装する例は、[BevyRuntimeSample](../samples/BevyRuntimeSample/Editor)が参考になります。

## 組み込み方

エディタのプロジェクトはHostが生成します。利用者が書くのは、プロジェクトマニフェスト（`*.emptyengine`）の`editorProps`で指定するMSBuildのtargetsファイルだけです。生成されたプロジェクトは、このファイルをImportします。

```json
{
    "runtimeCommand": "cargo run",
    "editorProps": "MyGame.Editor.targets"
}
```

```xml
<Project>
  <PropertyGroup>
    <AssetsPath>$(MSBuildThisFileDirectory)Assets</AssetsPath>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(MSBuildThisFileDirectory)Editor/**/*.cs" />
    <PackageReference Include="EmptyEngine.Editor" Version="*" />
  </ItemGroup>
</Project>
```

- 生成されたプロジェクトは既定のCompile項目を持たないので、エディタ拡張のソースは`Compile`で明示的に取り込んでください
- エディタは起動時に、読み込んだ全アセンブリから下記のインターフェイスの実装を探して使います。登録のコードは要りません
- 実装のコンストラクタの引数は、DIで解決されます。`ISchemaSource`（型カタログ）や`ILogger<T>`を受け取れます

エディタの中では、シーンは`HierarchyNode`の木、アセット/コンポーネントは`AuthoringObject`で表します。エディタ拡張の仕事は、主にこれらとランタイムの形式の間の変換です。

## 最低限実装するもの

### targetsの宣言

| 項目 | 内容 |
|---|---|
| `AssetsPath` | ソースアセットのディレクトリ。絶対パスで書きます |
| `EmptyEngine.Editor`の参照 | エディタ本体です |

### インターフェイス

次の4つは、それぞれちょうど1つの実装が必要です。無い場合も2つ以上ある場合も、エディタは起動時にエラーになります。

| インターフェイス | 役割 |
|---|---|
| `IHierarchyBlobSerializer` | ヒエラルキー状態と、ランタイムとの間で送るバイト列の相互変換。形式はランタイムが決めたものに合わせます（[ランタイム接続プロトコル](runtime-protocol_ja.md)） |
| `ISceneArtifactStore` | ヒエラルキーの部分木を、ランタイムが読める形で保存・削除・読み込みする |
| `IAssetArtifactStore` | アセットを、ランタイムが読める形で保存・削除・読み込みする |
| `IDistributionBuilder` | 配布物の仕上げ。起動するシーンの一覧を書き出し、必要ならアセットをまとめるなどの後処理をする。配布ビルドを使わない場合は、何もしない実装で構いません |

`ISceneArtifactStore`と`IAssetArtifactStore`は、`DistributionRoot`を受け取るコンストラクタも用意してください。

配布ビルドでは、エディタが`DistributionBuild`で宣言したコマンドを実行したあと、次の順に処理します（`DeployBeforeBuild`を指定したビルドでは、コマンドの前に一時ディレクトリへ処理します）。

1. `DistributionRoot`を受け取るコンストラクタで配布先用のStoreを作り、起動するシーンから参照をたどれるシーンとアセットを、配布先へ書き出す
2. `IDistributionBuilder.Build`を呼ぶ

シーンとアセットを配布先へ書き出すのはエディタで、`IDistributionBuilder`はそのあとの仕上げだけを受け持ちます。例えばModules以下の実装例では、書き出されたシーンとアセットを1つのアーカイブにまとめています。

### シーンのインポータ

シーンを編集するには、シーンのソースファイルを読み書きする`ISceneImporter`が少なくとも1つ必要です。これが無いと、エディタはシーンを開けません。

- `SupportedExtensions`：受け持つ拡張子
- `ImportAsync`：ソースファイルを読んで、`HierarchyNode`の木を返す
- `SaveAsync(HierarchyNode, string, ...)`：編集したシーンをソースファイルへ書き戻す

シーンのソースの形式にこだわりが無ければ、実装例の[EmptyEngine.SceneSource.Editor](../Modules/EmptyEngine.SceneSource.Editor)を参照するだけで済みます。JSONのシーン（`.scene`）のインポータで、型カタログだけを頼りに読み書きするので、ランタイムの実装によらず使えます。ネストしたプレハブ、プレハブバリアント（`.variant`）、任意の型のアセット（`.asset`）のインポータも入っています。

### 型カタログ

エディタは、コンポーネントやアセットのフィールドの型を、型カタログ（`.artifacts/TypeCatalog.json`）から知ります。ランタイムのビルドの中で、ランタイムの型から生成してください。形式はJSON Schemaです（[型カタログ](type-catalog_ja.md)）。生成の例は、Rustの型から作るBevyRuntimeSampleの[build.rs](../samples/BevyRuntimeSample/build.rs)と、C#の型から作る[EmptyEngine.TypeCatalog](../Modules/EmptyEngine.TypeCatalog)を参照してください。

カタログが無くてもエディタは起動しますが、コンポーネントの追加やインスペクタでの編集ができません。

アセット参照のフィールドは、参照先の型（`x-target`）のアセットだけをピッカーの候補に出します。シーンを入れるフィールドは、`x-target`を予約値の`@scene`にしてください。例えばCatalogToolは、`AssetReference<IObject>`をそう出力しています。

## 任意で実装できるもの

### アセットのインポータ

| インターフェイス | 役割 |
|---|---|
| `IAssetImporter` | シーン以外のソースアセット（画像、音声など）を取り込む。拡張子ごとにいくつでも実装できます |
| `IVariantImporter` | 元のシーンとの差分だけを持つバリアントを作れるようにする |
| `INestedPrefabImporter` | シーンの中に別のシーンを子として配置できるようにする |

`IAssetImporter`は、次のメンバを実装すると機能が増えます。

- `IsSaveSupported`と`SaveAsync`：インスペクタで編集したアセットを、ソースファイルへ書き戻せるようにする
- `AssetImportResult.Dependencies`：取り込み結果が依存する他のアセットのキー。依存先を再インポートしたとき、このアセットも再インポートされます

### 専用のインスペクタ

`[InspectorFor]`属性を付けたRazorコンポーネントは、指定した型の専用インスペクタになります。無い型は、型カタログから組み立てた既定のインスペクタで表示します。

```xml
<RazorComponent Include="$(MSBuildThisFileDirectory)Editor/**/*.razor" />
```

例はBevyRuntimeSampleの[BevyTransformInspector.razor](../samples/BevyRuntimeSample/Editor/BevyTransformInspector.razor)を参照してください。

### targetsの宣言

| 項目 | 内容 |
|---|---|
| `DistributionBuild` | 配布ビルドの名前と、実行するコマンド（`Command`）。出力先は`{output}`で受け取ります |
| `BuildWorkingPath` | 配布ビルドのコマンドを実行するディレクトリ。`DistributionBuild`を宣言した場合は必須です |
| `EditorFrameAncestor` | エディタのページを埋め込めるオリジン。VSCode拡張で開く場合は必要です（[拡張のREADME](../vscode-extension/README.md#requirements)） |
| `DistributionRoot` | シーンから参照されなくても配布するアセットのキー。ランタイムのコードがキーを直接持って読み込むアセットに使います。その参照先も配布されます |
| `EmptyEngineAssetRoot` | パッケージが同梱するソースアセットのディレクトリ。パッケージの側で宣言します（下記） |

### パッケージに同梱するアセット

targetsから参照するパッケージには、ソースアセット（画像、フォント、シェーダ、プレハブなど）を同梱できます。パッケージが自分の`buildTransitive/<パッケージID>.props`で`EmptyEngineAssetRoot`を宣言すると、エディタはそのディレクトリも取り込みます。

```xml
<Project>
  <ItemGroup>
    <EmptyEngineAssetRoot Include="$(MSBuildThisFileDirectory)../assets/MyPackage" Name="MyPackage" />
  </ItemGroup>
</Project>
```
