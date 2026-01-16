using System;
using System.Numerics;

namespace RayTraceVS.WPF.Models
{
    /// <summary>
    /// 3D空間における位置・回転・スケールを表すTransform構造体
    /// </summary>
    public struct Transform
    {
        /// <summary>
        /// 位置（ワールド座標）
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// 回転（オイラー角：度数法）
        /// X: Pitch（ピッチ）, Y: Yaw（ヨー）, Z: Roll（ロール）
        /// </summary>
        public Vector3 Rotation { get; set; }

        /// <summary>
        /// スケール（各軸の拡大率）
        /// </summary>
        public Vector3 Scale { get; set; }

        /// <summary>
        /// デフォルトTransform（原点、回転なし、スケール1）
        /// </summary>
        public static Transform Identity => new Transform
        {
            Position = Vector3.Zero,
            Rotation = Vector3.Zero,
            Scale = Vector3.One
        };

        /// <summary>
        /// 回転をQuaternionに変換
        /// </summary>
        public Quaternion GetQuaternion()
        {
            // オイラー角（度）からラジアンに変換
            var pitch = Rotation.X * ((float)Math.PI / 180.0f);
            var yaw = Rotation.Y * ((float)Math.PI / 180.0f);
            var roll = Rotation.Z * ((float)Math.PI / 180.0f);

            // Quaternionを作成（YXZ順）
            return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        }

        /// <summary>
        /// Quaternionから回転を設定
        /// </summary>
        public void SetFromQuaternion(Quaternion quaternion)
        {
            // QuaternionからYaw-Pitch-Rollを計算
            var yaw = (float)Math.Atan2(2.0f * (quaternion.Y * quaternion.W + quaternion.X * quaternion.Z),
                                  1.0f - 2.0f * (quaternion.X * quaternion.X + quaternion.Y * quaternion.Y));
            var pitch = (float)Math.Asin(2.0f * (quaternion.X * quaternion.W - quaternion.Y * quaternion.Z));
            var roll = (float)Math.Atan2(2.0f * (quaternion.X * quaternion.Y + quaternion.Z * quaternion.W),
                                   1.0f - 2.0f * (quaternion.X * quaternion.X + quaternion.Z * quaternion.Z));

            // ラジアンから度に変換
            Rotation = new Vector3(
                pitch * (180.0f / (float)Math.PI),
                yaw * (180.0f / (float)Math.PI),
                roll * (180.0f / (float)Math.PI)
            );
        }

        /// <summary>
        /// Transform行列を取得
        /// </summary>
        public Matrix4x4 GetMatrix()
        {
            var translationMatrix = Matrix4x4.CreateTranslation(Position);
            var rotationMatrix = Matrix4x4.CreateFromQuaternion(GetQuaternion());
            var scaleMatrix = Matrix4x4.CreateScale(Scale);

            // スケール → 回転 → 平行移動の順で適用
            return scaleMatrix * rotationMatrix * translationMatrix;
        }
    }
}
