// netstandard には無いが、init アクセサ（record の位置指定パラメータを含む）が modreq として要求する印。
// 型そのものに実体は無く、名前だけが契約＝MessagePack 側の init 判定も FullName で見ている。
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
