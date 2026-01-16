// 共通定義

#define MAX_RECURSION_DEPTH 5

// レイペイロード
struct RayPayload
{
    float3 color;
    uint depth;
    bool hit;
};

// 球の属性
struct SphereAttributes
{
    float3 normal;
};

// シーンバッファ
struct SceneConstantBuffer
{
    float4x4 viewProjectionMatrix;
    float3 cameraPosition;
    uint numLights;
};

// 球データ
struct SphereData
{
    float3 center;
    float radius;
    float4 color;
    float reflectivity;
    float transparency;
    float ior;
};

// 平面データ
struct PlaneData
{
    float3 position;
    float3 normal;
    float4 color;
    float reflectivity;
};

// 円柱データ
struct CylinderData
{
    float3 position;
    float3 axis;
    float radius;
    float height;
    float4 color;
    float reflectivity;
};

// ライトデータ
struct LightData
{
    float3 position;
    float4 color;
    float intensity;
};

// レイトレーシングリソース
RaytracingAccelerationStructure SceneBVH : register(t0);
RWTexture2D<float4> RenderTarget : register(u0);
ConstantBuffer<SceneConstantBuffer> Scene : register(b0);

// ユーティリティ関数
float3 CreateCameraRay(float2 ndc, float3 cameraPos, float4x4 invViewProj)
{
    float4 worldPos = mul(invViewProj, float4(ndc, 0.0, 1.0));
    worldPos /= worldPos.w;
    return normalize(worldPos.xyz - cameraPos);
}

// ランベルト拡散反射
float3 CalculateDiffuse(float3 normal, float3 lightDir, float3 lightColor, float3 objectColor)
{
    float ndotl = max(0.0, dot(normal, lightDir));
    return objectColor * lightColor * ndotl;
}

// スペキュラー反射
float3 CalculateSpecular(float3 normal, float3 lightDir, float3 viewDir, float3 lightColor, float shininess)
{
    float3 reflectDir = reflect(-lightDir, normal);
    float spec = pow(max(0.0, dot(viewDir, reflectDir)), shininess);
    return lightColor * spec;
}
