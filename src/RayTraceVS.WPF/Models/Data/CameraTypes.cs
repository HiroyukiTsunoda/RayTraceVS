using System.Numerics;

namespace RayTraceVS.WPF.Models.Data
{
    /// <summary>
    /// カメラデータ構造体
    /// </summary>
    public struct CameraData
    {
        public Vector3 Position;
        public Vector3 LookAt;
        public Vector3 Up;
        public float FieldOfView;
        public float Near;
        public float Far;
        public float ApertureSize;
        public float FocusDistance;
    }
}
