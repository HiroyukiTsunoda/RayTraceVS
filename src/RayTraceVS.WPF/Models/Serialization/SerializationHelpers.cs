using System.Numerics;
using Newtonsoft.Json.Linq;

namespace RayTraceVS.WPF.Models.Serialization
{
    /// <summary>
    /// シリアライズ/デシリアライズ用のヘルパーメソッド
    /// </summary>
    public static class SerializationHelpers
    {
        /// <summary>
        /// Vector3をJSONオブジェクトに変換する
        /// </summary>
        public static JObject ToJson(this Vector3 v)
        {
            return new JObject
            {
                ["x"] = v.X,
                ["y"] = v.Y,
                ["z"] = v.Z
            };
        }

        /// <summary>
        /// Vector4をJSONオブジェクトに変換する
        /// </summary>
        public static JObject ToJson(this Vector4 v)
        {
            return new JObject
            {
                ["x"] = v.X,
                ["y"] = v.Y,
                ["z"] = v.Z,
                ["w"] = v.W
            };
        }

        /// <summary>
        /// QuaternionをJSONオブジェクトに変換する
        /// </summary>
        public static JObject ToJson(this Quaternion q)
        {
            return new JObject
            {
                ["x"] = q.X,
                ["y"] = q.Y,
                ["z"] = q.Z,
                ["w"] = q.W
            };
        }

        /// <summary>
        /// TransformをJSONオブジェクトに変換する
        /// </summary>
        public static JObject ToJson(this Transform t)
        {
            return new JObject
            {
                ["position"] = t.Position.ToJson(),
                ["rotation"] = t.Rotation.ToJson(),
                ["scale"] = t.Scale.ToJson()
            };
        }

        /// <summary>
        /// JSONオブジェクトからVector3を読み取る
        /// </summary>
        public static Vector3 ToVector3(this JToken? token, Vector3 defaultValue = default)
        {
            if (token == null) return defaultValue;
            
            return new Vector3(
                token["x"]?.Value<float>() ?? defaultValue.X,
                token["y"]?.Value<float>() ?? defaultValue.Y,
                token["z"]?.Value<float>() ?? defaultValue.Z
            );
        }

        /// <summary>
        /// JSONオブジェクトからVector4を読み取る
        /// </summary>
        public static Vector4 ToVector4(this JToken? token, Vector4 defaultValue = default)
        {
            if (token == null) return defaultValue;
            
            return new Vector4(
                token["x"]?.Value<float>() ?? defaultValue.X,
                token["y"]?.Value<float>() ?? defaultValue.Y,
                token["z"]?.Value<float>() ?? defaultValue.Z,
                token["w"]?.Value<float>() ?? defaultValue.W
            );
        }

        /// <summary>
        /// JSONオブジェクトからQuaternionを読み取る
        /// </summary>
        public static Quaternion ToQuaternion(this JToken? token, Quaternion defaultValue = default)
        {
            if (token == null) return defaultValue;
            
            return new Quaternion(
                token["x"]?.Value<float>() ?? defaultValue.X,
                token["y"]?.Value<float>() ?? defaultValue.Y,
                token["z"]?.Value<float>() ?? defaultValue.Z,
                token["w"]?.Value<float>() ?? defaultValue.W
            );
        }

        /// <summary>
        /// JSONオブジェクトからTransformを読み取る
        /// </summary>
        public static Transform ToTransform(this JToken? token)
        {
            if (token == null) return Transform.Identity;
            
            return new Transform
            {
                Position = token["position"].ToVector3(),
                Rotation = token["rotation"].ToQuaternion(Quaternion.Identity),
                Scale = token["scale"].ToVector3(Vector3.One)
            };
        }

        /// <summary>
        /// floatを安全に読み取る
        /// </summary>
        public static float GetFloat(this JToken? json, string propertyName, float defaultValue = 0f)
        {
            return json?[propertyName]?.Value<float>() ?? defaultValue;
        }

        /// <summary>
        /// intを安全に読み取る
        /// </summary>
        public static int GetInt(this JToken? json, string propertyName, int defaultValue = 0)
        {
            return json?[propertyName]?.Value<int>() ?? defaultValue;
        }

        /// <summary>
        /// boolを安全に読み取る
        /// </summary>
        public static bool GetBool(this JToken? json, string propertyName, bool defaultValue = false)
        {
            return json?[propertyName]?.Value<bool>() ?? defaultValue;
        }

        /// <summary>
        /// stringを安全に読み取る
        /// </summary>
        public static string GetString(this JToken? json, string propertyName, string defaultValue = "")
        {
            return json?[propertyName]?.Value<string>() ?? defaultValue;
        }
    }
}
