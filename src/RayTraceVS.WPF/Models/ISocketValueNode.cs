namespace RayTraceVS.WPF.Models
{
    /// <summary>
    /// ソケット経由で個別のfloat値を読み書きできるノードの共通インターフェース。
    /// Vector3Node / Vector4Node / ColorNode が実装する。
    /// </summary>
    public interface ISocketValueNode
    {
        float GetSocketValue(string socketName);
        void SetSocketValue(string socketName, float value);
    }
}
