# EmptyEngine: An Editor for Your Game Engine

[English](README.md) | 日本語

EmptyEngineはランタイム実装を持たないエディタのみのゲームエンジンです。  
[Gonso Flood Front](https://unityroom.com/games/floodfront)の開発に利用しました。

![VSCodeのサイドバーに開いたEmptyEngineのヒエラルキーとインスペクタ](emptyengine_vscode_extension.png)

リポジトリ内には次の要素が含まれます。
- エディタ実装本体
- ランタイム実装例（サンプルプロジェクト）
  - [DotnetRuntimeSample](samples/DotnetRuntimeSample)：.NETによる実装。`dotnet new emptyengine`のテンプレートを兼ねる
  - [BevyRuntimeSample](samples/BevyRuntimeSample)：Rust/Bevyによる実装

## 特徴

### 任意のランタイムで利用可能

エディタがランタイムに要求する要件は主に次のとおりです:
- WebSocketでシーン状態を受け取り反映する

通信の仕様は[ランタイム接続プロトコル](docs/runtime-protocol_ja.md)を参照してください。  
また、ランタイムのビルド時に[型カタログ](docs/type-catalog_ja.md)を生成する必要があります。

上記以外に大きな制約はありません。
- 実装言語やフレームワークに依存しません
- 実行プラットフォームに依存しません
- データモデルやシリアライズ形式に依存しません

### Webサーバとして起動

エディタはWebサーバとして起動し、任意のWebブラウザからエディタUIを利用可能です。
- 適切なポート転送等を行えばリモートでのエディタ操作が可能
- VSCode内のペインとしてエディタを開く拡張を提供

また、ヒエラルキーなどの状態を返すエンドポイントを複数提供しています。
これにより、エージェント等がUIを介さずにエディタ内の状態を取得できます。
エンドポイントの一覧は、起動中のエディタの`/api/editor`（既定では<http://127.0.0.1:5170/api/editor>）で確認できます。

### 拡張性

ランタイムは利用者自身が実装するものなので、エンジン側にランタイムを拡張する仕組みはありません。自由に実装できます。

エディタ向けには次の要素を定義するための抽象が用意されています。
- ランタイム/エディタ間の通信データのフォーマット
- アセットのインポータ
- シーン/アセットのシリアライザ
- インスペクタの表示
- ビルドコマンド

いずれもC#/.NETによる拡張実装が可能です。
[エディタ拡張](docs/editor-extension_ja.md)を参照してください。

## 動作要件

エディタを立ち上げる環境には.NET 10以上のSDKが必要です。

## クイックスタート

まずはランタイムを実装します。[samples](samples)の実装例を参考にしてください。  
.NET向けにはプロジェクトテンプレートもあります
```
dotnet new install EmptyEngine.Templates
dotnet new emptyengine
```

### コマンドラインから起動する場合

エディタのインストール
```
dotnet tool install -g EmptyEngine.Host
```
プロジェクトマニフェストを指定して起動
```
emptyengine <Project>.emptyengine
```

### VSCode拡張として起動する場合

拡張をインストール  
<https://marketplace.visualstudio.com/items?itemName=FriendSea.emptyengine-host>

`<Project>.emptyengine`を含むフォルダを開くと拡張が有効になり、アクティビティバーの«EmptyEngine»からエディタのペインを開けます。
`*.emptyengine`が複数ある場合は、コマンド`EmptyEngine: Select Project (*.emptyengine)`で選択します。

テンプレート以外で作ったプロジェクトは、VSCodeへの埋め込みを許可する設定が必要です。
[拡張のREADME](vscode-extension/README.md#requirements)を参照してください。

## ライセンス

[MIT License](LICENSE)
