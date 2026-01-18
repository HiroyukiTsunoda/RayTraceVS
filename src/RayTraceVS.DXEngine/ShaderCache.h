#pragma once

#include <d3d12.h>
#include <d3dcompiler.h>
#include <dxcapi.h>
#include <wrl/client.h>
#include <string>
#include <unordered_map>
#include <vector>
#include <memory>

using Microsoft::WRL::ComPtr;

namespace RayTraceVS::DXEngine
{
    class DXContext;

    // Shader type for compilation
    enum class ShaderType
    {
        DXRLibrary,     // lib_6_3 for DXR shaders
        Compute         // cs_5_1 for compute shaders
    };

    // Shader dependency information
    struct ShaderDependency
    {
        std::wstring filename;
        std::string hash;
    };

    // Per-shader cache information
    struct ShaderCacheInfo
    {
        std::string sourceHash;
        std::vector<ShaderDependency> dependencies;
        std::string compiledAt;
    };

    // Cache metadata structure
    struct CacheMetadata
    {
        int version = 1;
        std::string driverVersion;
        uint64_t adapterLUID = 0;
        std::unordered_map<std::wstring, ShaderCacheInfo> shaders;
    };

    // Shader definition for registration
    struct ShaderDefinition
    {
        std::wstring name;
        ShaderType type;
        std::wstring entryPoint;  // Only for compute shaders
        std::vector<std::wstring> dependencies;
    };

    class ShaderCache
    {
    public:
        ShaderCache(DXContext* context);
        ~ShaderCache();

        // Initialize the cache system
        // cacheDirectory: path to store .cso files and metadata
        // sourceDirectory: path to .hlsl source files
        bool Initialize(const std::wstring& cacheDirectory, const std::wstring& sourceDirectory);

        // Get a compiled shader (from cache or compile if needed)
        // Returns true if successful, shader blob returned in 'shader'
        bool GetShader(const std::wstring& shaderName, ID3DBlob** shader);

        // Get a compute shader with specific entry point
        bool GetComputeShader(const std::wstring& shaderName, const std::wstring& entryPoint, ID3DBlob** shader);

        // Clear all cached shaders (forces recompilation on next access)
        void ClearCache();

        // Check if any shaders need recompilation
        bool NeedsRecompilation() const;

        // Get compilation status message (for UI display)
        std::wstring GetStatusMessage() const;

        // Pre-compile all registered shaders
        bool PrecompileAll();

    private:
        // Register known shaders and their dependencies
        void RegisterShaders();

        // Check if a specific shader's cache is valid
        bool IsCacheValid(const std::wstring& shaderName);

        // Check if global cache is valid (driver version, etc.)
        bool IsGlobalCacheValid();

        // Compile a shader and save to cache
        bool CompileAndCache(const std::wstring& shaderName, ID3DBlob** shader);

        // Compile a compute shader and save to cache
        bool CompileComputeAndCache(const std::wstring& shaderName, const std::wstring& entryPoint, ID3DBlob** shader);

        // Load a shader from cache
        bool LoadFromCache(const std::wstring& shaderName, ID3DBlob** shader);

        // Load and save metadata
        bool LoadMetadata();
        bool SaveMetadata();

        // Get driver information from the adapter
        std::string GetDriverVersion();
        uint64_t GetAdapterLUID();

        // Calculate SHA-256 hash of a file
        std::string CalculateFileHash(const std::wstring& path);

        // Get the full path for a shader source file
        std::wstring GetSourcePath(const std::wstring& shaderName) const;

        // Get the full path for a cached shader file
        std::wstring GetCachePath(const std::wstring& shaderName) const;

        // Get current timestamp as string
        std::string GetCurrentTimestamp();

        // Compile DXR library shader using DXC
        bool CompileDXRLibrary(const std::wstring& sourcePath, ID3DBlob** shader);

        // Compile compute shader using D3DCompile
        bool CompileComputeShader(const std::wstring& sourcePath, const std::wstring& entryPoint, ID3DBlob** shader);

        // Save compiled shader to file
        bool SaveShaderToFile(ID3DBlob* shader, const std::wstring& path);

        // Logging helper
        void Log(const char* message);
        void Log(const char* message, HRESULT hr);

        DXContext* dxContext;
        std::wstring cacheDir;
        std::wstring sourceDir;

        CacheMetadata metadata;
        bool metadataLoaded = false;
        bool globalCacheValid = false;

        // Registered shader definitions
        std::unordered_map<std::wstring, ShaderDefinition> shaderDefinitions;

        // Status message for UI
        std::wstring statusMessage;
    };
}
