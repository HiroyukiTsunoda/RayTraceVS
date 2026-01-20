using Newtonsoft.Json.Linq;

namespace RayTraceVS.WPF.Models.Serialization
{
    /// <summary>
    /// ノードのシリアライズ/デシリアライズを行うためのインターフェース
    /// 各ノードタイプがこのインターフェースを実装することで、
    /// SceneFileServiceの巨大なswitch文を削減できる
    /// </summary>
    public interface ISerializableNode
    {
        /// <summary>
        /// ノードのプロパティをJSONオブジェクトにシリアライズする
        /// 共通プロパティ（Id, Type, Position）は呼び出し側で処理されるため、
        /// ノード固有のプロパティのみを追加する
        /// </summary>
        /// <param name="json">プロパティを追加するJSONオブジェクト</param>
        void SerializeProperties(JObject json);

        /// <summary>
        /// JSONオブジェクトからノードのプロパティをデシリアライズする
        /// 共通プロパティ（Id, Type, Position）は呼び出し側で処理されるため、
        /// ノード固有のプロパティのみを読み取る
        /// </summary>
        /// <param name="json">読み取るJSONオブジェクト</param>
        void DeserializeProperties(JObject json);
    }
}
