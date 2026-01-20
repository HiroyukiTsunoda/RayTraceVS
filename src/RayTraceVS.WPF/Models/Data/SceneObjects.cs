using System.Numerics;

namespace RayTraceVS.WPF.Models.Data
{
    /// <summary>
    /// 球のデータ構造体
    /// </summary>
    public struct SphereData
    {
        public Vector3 Position;
        public float Radius;
        public MaterialData Material;
    }

    /// <summary>
    /// 平面のデータ構造体
    /// </summary>
    public struct PlaneData
    {
        public Vector3 Position;
        public Vector3 Normal;
        public MaterialData Material;
    }

    /// <summary>
    /// ボックスのデータ構造体
    /// </summary>
    public struct BoxData
    {
        public Vector3 Center;
        public Vector3 Size;  // half-extents
        public MaterialData Material;
    }
}
