using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models
{
    /// <summary>
    /// Model層（FBXMeshNode）がメッシュキャッシュへアクセスするための抽象。
    /// 実装は Services.MeshCacheService。App.OnStartup で Node.MeshCacheProvider に注入され、
    /// Model層からApp層（App.MeshCacheService）への直接依存を断つ。
    /// </summary>
    public interface IMeshCacheProvider
    {
        /// <summary>
        /// メッシュ名からキャッシュ済みメッシュデータを取得する（存在しなければnull）
        /// </summary>
        CachedMeshData? GetMesh(string meshName);
    }
}
