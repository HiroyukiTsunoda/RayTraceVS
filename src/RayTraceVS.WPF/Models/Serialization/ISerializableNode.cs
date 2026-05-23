using System.Collections.Generic;

namespace RayTraceVS.WPF.Models.Serialization
{
    /// <summary>
    /// ノードのシリアライズ/デシリアライズを行うためのインターフェース
    /// 各ノードタイプがこのインターフェースを実装することで、
    /// SceneFileServiceやクリップボードの巨大switch文を削減できる
    /// </summary>
    public interface ISerializableNode
    {
        /// <summary>
        /// ノードのプロパティをDictionaryにシリアライズする
        /// 共通プロパティ（Id, Type, Position）は呼び出し側で処理されるため、
        /// ノード固有のプロパティのみを追加する
        /// </summary>
        /// <param name="properties">プロパティを追加するDictionary</param>
        void SerializeProperties(IDictionary<string, object?> properties);

        /// <summary>
        /// Dictionaryからノードのプロパティをデシリアライズする
        /// 共通プロパティ（Id, Type, Position）は呼び出し側で処理されるため、
        /// ノード固有のプロパティのみを読み取る
        /// </summary>
        /// <param name="properties">読み取るDictionary</param>
        void DeserializeProperties(IReadOnlyDictionary<string, object?> properties);
    }
}
