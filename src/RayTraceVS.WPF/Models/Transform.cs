using System;
using System.Numerics;

namespace RayTraceVS.WPF.Models
{
    /// <summary>
    /// 3D空間における位置・回転・スケールを表すTransform構造体
    /// UE5 FTransform に準拠した設計（内部表現はQuaternion）
    /// </summary>
    public struct Transform
    {
        /// <summary>
        /// 位置（ワールド座標）
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// 回転（内部表現: Quaternion）- UE5スタイル
        /// </summary>
        public Quaternion Rotation { get; set; }

        /// <summary>
        /// スケール（各軸の拡大率）
        /// </summary>
        public Vector3 Scale { get; set; }

        /// <summary>
        /// 回転（オイラー角: UI編集用）
        /// X: Pitch（ピッチ）, Y: Yaw（ヨー）, Z: Roll（ロール）（度数法）
        /// </summary>
        public Vector3 EulerAngles
        {
            get => QuaternionToEuler(Rotation);
            set => Rotation = EulerToQuaternion(value);
        }

        /// <summary>
        /// デフォルトTransform（原点、回転なし、スケール1）
        /// </summary>
        public static Transform Identity => new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One
        };

        /// <summary>
        /// オイラー角（度数法）からQuaternionに変換
        /// </summary>
        private static Quaternion EulerToQuaternion(Vector3 euler)
        {
            // オイラー角（度）からラジアンに変換
            var pitch = euler.X * (MathF.PI / 180.0f);
            var yaw = euler.Y * (MathF.PI / 180.0f);
            var roll = euler.Z * (MathF.PI / 180.0f);

            // Quaternionを作成（YXZ順）
            return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        }

        /// <summary>
        /// Quaternionからオイラー角（度数法）に変換
        /// </summary>
        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            // QuaternionからYaw-Pitch-Rollを計算
            var yaw = MathF.Atan2(2.0f * (q.Y * q.W + q.X * q.Z),
                                  1.0f - 2.0f * (q.X * q.X + q.Y * q.Y));
            
            // Clampしてasinの範囲外エラーを防止
            var sinp = 2.0f * (q.X * q.W - q.Y * q.Z);
            var pitch = MathF.Abs(sinp) >= 1.0f 
                ? MathF.CopySign(MathF.PI / 2.0f, sinp) 
                : MathF.Asin(sinp);
            
            var roll = MathF.Atan2(2.0f * (q.X * q.Y + q.Z * q.W),
                                   1.0f - 2.0f * (q.X * q.X + q.Z * q.Z));

            // ラジアンから度に変換
            return new Vector3(
                pitch * (180.0f / MathF.PI),
                yaw * (180.0f / MathF.PI),
                roll * (180.0f / MathF.PI)
            );
        }

        /// <summary>
        /// Transform行列を取得（Scale → Rotate → Translate）
        /// </summary>
        public Matrix4x4 GetMatrix()
        {
            var scaleMatrix = Matrix4x4.CreateScale(Scale);
            var rotationMatrix = Matrix4x4.CreateFromQuaternion(Rotation);
            var translationMatrix = Matrix4x4.CreateTranslation(Position);

            // スケール → 回転 → 平行移動の順で適用
            return scaleMatrix * rotationMatrix * translationMatrix;
        }

        /// <summary>
        /// 行列からTransformを分解して作成
        /// </summary>
        public static Transform FromMatrix(Matrix4x4 matrix)
        {
            Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation);
            return new Transform
            {
                Position = translation,
                Rotation = rotation,
                Scale = scale
            };
        }

        /// <summary>
        /// 2つのTransformを合成（this * other の順で適用）
        /// 親子階層で使用: localTransform.Combine(parentTransform)
        /// </summary>
        public Transform Combine(Transform parent)
        {
            // 行列で合成（非一様スケール対応）
            var combinedMatrix = this.GetMatrix() * parent.GetMatrix();
            return FromMatrix(combinedMatrix);
        }
    }
}
