using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace RayTraceVS.WPF.Models.Serialization
{
    /// <summary>
    /// シリアライズ/デシリアライズ用のヘルパーメソッド
    /// </summary>
    public static class SerializationHelpers
    {
        // ============================================================
        // Dictionary から値を読み取るヘルパー（Newtonsoftデシリアライズ後の
        // object? は JToken/JObject/JValue になっている前提）
        // ============================================================

        /// <summary>
        /// object? を Vector3 に変換する
        /// Newtonsoftデシリアライズ後は JObject (PascalCaseキー) になる
        /// </summary>
        public static Vector3 ConvertToVector3(object? obj)
        {
            if (obj == null)
                return Vector3.Zero;

            if (obj is Vector3 vec3)
                return vec3;

            if (obj is JObject jobj)
            {
                return ReadVector3(jobj, Vector3.Zero);
            }

            return Vector3.Zero;
        }

        /// <summary>
        /// object? を Vector4 に変換する
        /// </summary>
        public static Vector4 ConvertToVector4(object? obj)
        {
            if (obj == null)
                return Vector4.One;

            if (obj is Vector4 vec4)
                return vec4;

            if (obj is JObject jobj)
            {
                return new Vector4(
                    GetComponent(jobj, "X")?.Value<float>() ?? 0,
                    GetComponent(jobj, "Y")?.Value<float>() ?? 0,
                    GetComponent(jobj, "Z")?.Value<float>() ?? 0,
                    GetComponent(jobj, "W")?.Value<float>() ?? 1
                );
            }

            return Vector4.One;
        }

        /// <summary>
        /// object? を Transform に変換する
        /// </summary>
        public static Transform ConvertToTransform(object? obj)
        {
            if (obj == null)
                return Transform.Identity;

            if (obj is Transform transform)
                return transform;

            if (obj is JObject jobj)
            {
                var result = Transform.Identity;
                result.Position = ReadVector3(GetComponent(jobj, "Position"), Vector3.Zero);
                result.Scale = ReadVector3(GetComponent(jobj, "Scale"), Vector3.One);

                var rotation = GetComponent(jobj, "Rotation") as JObject;
                if (rotation != null && GetComponent(rotation, "W") != null)
                {
                    // Current files store Rotation as a quaternion. Do not reinterpret
                    // its XYZ components as Euler angles or round-trip through EulerAngles.
                    result.Rotation = new Quaternion(
                        GetComponent(rotation, "X")?.Value<float>() ?? 0,
                        GetComponent(rotation, "Y")?.Value<float>() ?? 0,
                        GetComponent(rotation, "Z")?.Value<float>() ?? 0,
                        GetComponent(rotation, "W")!.Value<float>());
                }
                else
                {
                    // Legacy files stored degrees in Rotation, or only EulerAngles.
                    result.EulerAngles = ReadVector3(
                        rotation ?? GetComponent(jobj, "EulerAngles"), Vector3.Zero);
                }
                return result;
            }

            return Transform.Identity;
        }

        private static JToken? GetComponent(JObject value, string name)
        {
            return value.GetValue(name, StringComparison.OrdinalIgnoreCase);
        }

        private static Vector3 ReadVector3(JToken? token, Vector3 defaultValue)
        {
            if (token is not JObject value)
                return defaultValue;

            return new Vector3(
                GetComponent(value, "X")?.Value<float>() ?? defaultValue.X,
                GetComponent(value, "Y")?.Value<float>() ?? defaultValue.Y,
                GetComponent(value, "Z")?.Value<float>() ?? defaultValue.Z);
        }

        // ============================================================
        // JObject ベースのヘルパー（レガシー互換用に残置）
        // ============================================================

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
