using EmptyEngine.Core;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Mesh;

/// <summary>サブメッシュ＝インデックスバッファ内の連続範囲とマテリアルスロット</summary>
public sealed class MeshSubset
{
    /// <summary>この範囲の先頭インデックス（インデックス数単位）</summary>
    public int IndexOffset;

    /// <summary>この範囲のインデックス数</summary>
    public int IndexCount;

    /// <summary><see cref="MeshAsset.Materials"/> のスロット番号</summary>
    public int MaterialIndex;
}

/// <summary>メッシュのマテリアルスロット</summary>
public sealed class MeshMaterial
{
    /// <summary>外部ファイルへの参照</summary>
    public AssetReference<TextureAsset> Texture = new();

    /// <summary><see cref="MeshAsset.EmbeddedTextures"/> のインデックス</summary>
    public int EmbeddedTexture = -1;

    /// <summary>ベースカラー（テクスチャへ乗算）</summary>
    public Color BaseColor = Color.White;
}

/// <summary>メッシュに埋め込まれたテクスチャ 1 枚分のメタデータ</summary>
public sealed class MeshEmbeddedTexture
{
    /// <summary>テクスチャ幅（px）</summary>
    public int Width;

    /// <summary>テクスチャ高さ（px）</summary>
    public int Height;

    /// <summary>ピクセル本体の格納形式</summary>
    public TexturePixelFormat Format;

    /// <summary><see cref="MeshAsset.TexturePixels"/> 内の開始バイト位置</summary>
    public long Offset;

    /// <summary>この 1 枚分のバイト長</summary>
    public long Length;
}

/// <summary>インポート済み 3D メッシュ</summary>
[Asset]
public sealed class MeshAsset : IAssetResolutionHook
{
    /// <summary>頂点数</summary>
    public int VertexCount;

    /// <summary>インデックス数（uint32）</summary>
    public int IndexCount;

    /// <summary>マテリアル毎の描画範囲</summary>
    public MeshSubset[] Subsets = [];

    /// <summary>マテリアルスロット</summary>
    public MeshMaterial[] Materials = [];

    /// <summary>埋込テクスチャの領域テーブル</summary>
    public MeshEmbeddedTexture[] EmbeddedTextures = [];

    /// <summary>頂点本体（float32 インターリーブ pos3+normal3+uv2）</summary>
    public IAssetBinary Vertices = null!;

    /// <summary>インデックス本体（uint32 連続）</summary>
    public IAssetBinary Indices = null!;

    /// <summary>全埋込テクスチャのピクセル本体を連結したバイナリ</summary>
    public IAssetBinary? TexturePixels;

    private TextureAsset?[]? _resolvedTextures;

    /// <summary>マテリアルスロット毎の解決済みテクスチャ</summary>
    public IReadOnlyList<TextureAsset?> ResolvedTextures => _resolvedTextures ?? [];

    /// <summary>実体化直後の各マテリアルのテクスチャの解決</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        var resolved = new TextureAsset?[Materials.Length];
        for (int i = 0; i < Materials.Length; i++)
        {
            MeshMaterial material = Materials[i];
            if (material.EmbeddedTexture >= 0 && material.EmbeddedTexture < EmbeddedTextures.Length && TexturePixels is not null)
            {
                MeshEmbeddedTexture embedded = EmbeddedTextures[material.EmbeddedTexture];
                resolved[i] = new TextureAsset
                {
                    Width = embedded.Width,
                    Height = embedded.Height,
                    Format = embedded.Format,
                    Pixels = new SliceAssetBinary(TexturePixels, embedded.Offset, embedded.Length),
                };
            }
            else
            {
                resolved[i] = await resolver.ResolveAsync(material.Texture);
            }
        }

        _resolvedTextures = resolved;
    }

    private sealed class SliceAssetBinary(IAssetBinary source, long offset, long length) : IAssetBinary
    {
        public long Length => length;

        public Stream OpenRead()
        {
            Stream stream = source.OpenRead();
            if (stream.CanSeek)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            else
            {
                Span<byte> scratch = stackalloc byte[4096];
                long remaining = offset;
                while (remaining > 0)
                {
                    int read = stream.Read(scratch[..(int)Math.Min(scratch.Length, remaining)]);
                    if (read <= 0) break;
                    remaining -= read;
                }
            }

            return new SliceStream(stream, length);
        }
    }

    private sealed class SliceStream(Stream inner, long length) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            long remaining = length - _read;
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = inner.Read(buffer[..toRead]);
            _read += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
