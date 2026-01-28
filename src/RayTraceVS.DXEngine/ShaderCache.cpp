#include "ShaderCache.h"
#include "DXContext.h"
#include "DebugLog.h"
#include <fstream>
#include <sstream>
#include <filesystem>
#include <chrono>
#include <iomanip>
#include <bcrypt.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "dxcompiler.lib")

namespace RayTraceVS::DXEngine
{
    // Simple JSON parser/writer for metadata
    // (Avoiding external dependencies like nlohmann/json)
    namespace JsonHelper
    {
        std::string EscapeString(const std::string& s)
        {
            std::string result;
            for (char c : s)
            {
                switch (c)
                {
                    case '"': result += "\\\""; break;
                    case '\\': result += "\\\\"; break;
                    case '\n': result += "\\n"; break;
                    case '\r': result += "\\r"; break;
                    case '\t': result += "\\t"; break;
                    default: result += c; break;
                }
            }
            return result;
        }

        std::string WStringToString(const std::wstring& ws)
        {
            if (ws.empty()) return "";
            int size = WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, nullptr, 0, nullptr, nullptr);
            std::string result(size - 1, 0);
            WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, &result[0], size, nullptr, nullptr);
            return result;
        }

        std::wstring StringToWString(const std::string& s)
        {
            if (s.empty()) return L"";
            int size = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
            std::wstring result(size - 1, 0);
            MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, &result[0], size);
            return result;
        }
    }

    // ShaderCache log helper - uses centralized logging
    static void ShaderCacheLog(const char* message)
    {
        std::string prefixedMsg = std::string("[ShaderCache] ") + message;
        LOG_INFO(prefixedMsg.c_str());
    }

    ShaderCache::ShaderCache(DXContext* context)
        : dxContext(context)
    {
    }

    ShaderCache::~ShaderCache()
    {
    }

    void ShaderCache::Log(const char* message)
    {
        ShaderCacheLog(message);
    }

    void ShaderCache::Log(const char* message, HRESULT hr)
    {
        char buf[512];
        sprintf_s(buf, "%s: 0x%08X", message, hr);
        ShaderCacheLog(buf);
    }

    bool ShaderCache::Initialize(const std::wstring& cacheDirectory, const std::wstring& sourceDirectory)
    {
        Log("ShaderCache::Initialize started");

        cacheDir = cacheDirectory;
        sourceDir = sourceDirectory;

        // Ensure cache directory exists
        try
        {
            std::filesystem::create_directories(cacheDir);
        }
        catch (const std::exception& e)
        {
            Log(("Failed to create cache directory: " + std::string(e.what())).c_str());
            return false;
        }

        // Register known shaders
        RegisterShaders();

        // Load existing metadata
        metadataLoaded = LoadMetadata();

        // Check if global cache is valid (driver version, etc.)
        globalCacheValid = IsGlobalCacheValid();

        if (!globalCacheValid)
        {
            Log("Global cache invalid (driver changed or first run) - will recompile all shaders");
            statusMessage = L"Shaders need recompilation (driver changed or first run)";
        }
        else
        {
            Log("Global cache valid - checking individual shaders");
            statusMessage = L"Shader cache initialized";
        }

        Log("ShaderCache::Initialize completed");
        return true;
    }

    void ShaderCache::RegisterShaders()
    {
        // DXR library shaders
        shaderDefinitions[L"RayGen"] = {
            L"RayGen", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli", L"NRDEncoding.hlsli" }
        };

        shaderDefinitions[L"ClosestHit"] = {
            L"ClosestHit", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli", L"NRDEncoding.hlsli" }
        };

        shaderDefinitions[L"ClosestHit_Triangle"] = {
            L"ClosestHit_Triangle", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli", L"NRDEncoding.hlsli" }
        };

        shaderDefinitions[L"Miss"] = {
            L"Miss", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli" }
        };

        shaderDefinitions[L"Intersection"] = {
            L"Intersection", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli" }
        };

        shaderDefinitions[L"AnyHit_Shadow"] = {
            L"AnyHit_Shadow", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli" }
        };

        shaderDefinitions[L"AnyHit_SkipSelf"] = {
            L"AnyHit_SkipSelf", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli" }
        };

        shaderDefinitions[L"PhotonEmit"] = {
            L"PhotonEmit", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli" }
        };

        shaderDefinitions[L"PhotonTrace"] = {
            L"PhotonTrace", ShaderType::DXRLibrary, L"",
            { L"Common.hlsli" }
        };

        // Photon hash table compute shaders (spatial hash for O(1) photon lookup)
        shaderDefinitions[L"BuildPhotonHashClear"] = {
            L"BuildPhotonHash", ShaderType::Compute, L"ClearPhotonHash",
            {}
        };

        shaderDefinitions[L"BuildPhotonHashBuild"] = {
            L"BuildPhotonHash", ShaderType::Compute, L"BuildPhotonHash",
            {}
        };

        // Compute shaders
        shaderDefinitions[L"RayTraceCompute"] = {
            L"RayTraceCompute", ShaderType::Compute, L"CSMain",
            {}
        };

        shaderDefinitions[L"Composite"] = {
            L"Composite", ShaderType::Compute, L"CSMain",
            {}
        };
    }

    bool ShaderCache::GetShader(const std::wstring& shaderName, ID3DBlob** shader)
    {
        auto it = shaderDefinitions.find(shaderName);
        if (it == shaderDefinitions.end())
        {
            Log(("Unknown shader: " + JsonHelper::WStringToString(shaderName)).c_str());
            return false;
        }

        const auto& def = it->second;

        // Check if cache is valid for this shader
        if (globalCacheValid && IsCacheValid(shaderName))
        {
            // Try to load from cache
            if (LoadFromCache(shaderName, shader))
            {
                Log(("Loaded shader from cache: " + JsonHelper::WStringToString(shaderName)).c_str());
                return true;
            }
        }

        // Cache invalid or failed to load - compile and cache
        Log(("Compiling shader: " + JsonHelper::WStringToString(shaderName)).c_str());

        if (def.type == ShaderType::DXRLibrary)
        {
            return CompileAndCache(shaderName, shader);
        }
        else
        {
            return CompileComputeAndCache(shaderName, def.entryPoint, shader);
        }
    }

    bool ShaderCache::GetComputeShader(const std::wstring& shaderName, const std::wstring& entryPoint, ID3DBlob** shader)
    {
        // Check if cache is valid
        if (globalCacheValid && IsCacheValid(shaderName))
        {
            if (LoadFromCache(shaderName, shader))
            {
                Log(("Loaded compute shader from cache: " + JsonHelper::WStringToString(shaderName)).c_str());
                return true;
            }
        }

        return CompileComputeAndCache(shaderName, entryPoint, shader);
    }

    bool ShaderCache::IsCacheValid(const std::wstring& shaderName)
    {
        // Check if we have metadata for this shader
        auto it = metadata.shaders.find(shaderName);
        if (it == metadata.shaders.end())
        {
            return false;
        }

        const auto& info = it->second;

        // Check if cached file exists
        std::wstring cachePath = GetCachePath(shaderName);
        if (!std::filesystem::exists(cachePath))
        {
            return false;
        }

        // Check source file hash
        std::wstring sourcePath = GetSourcePath(shaderName);
        std::string currentHash = CalculateFileHash(sourcePath);
        
        // ハッシュが空の場合はファイルが読めなかった = キャッシュ無効として再コンパイル
        if (currentHash.empty())
        {
            Log(("Failed to hash source: " + JsonHelper::WStringToString(sourcePath)).c_str());
            return false;
        }
        
        if (currentHash != info.sourceHash)
        {
            Log(("Source changed: " + JsonHelper::WStringToString(shaderName)).c_str());
            return false;
        }

        // Check dependency hashes
        auto defIt = shaderDefinitions.find(shaderName);
        if (defIt != shaderDefinitions.end())
        {
            const auto& deps = defIt->second.dependencies;
            
            // 依存関係の個数が変わったらキャッシュ無効
            if (deps.size() != info.dependencies.size())
            {
                Log(("Dependency count changed for: " + JsonHelper::WStringToString(shaderName)).c_str());
                return false;
            }
            
            for (size_t i = 0; i < deps.size(); i++)
            {
                std::wstring depPath = sourceDir + deps[i];
                std::string depHash = CalculateFileHash(depPath);
                
                // 依存ファイルのハッシュが空の場合もキャッシュ無効
                if (depHash.empty())
                {
                    Log(("Failed to hash dependency: " + JsonHelper::WStringToString(deps[i])).c_str());
                    return false;
                }
                
                if (depHash != info.dependencies[i].hash)
                {
                    Log(("Dependency changed: " + JsonHelper::WStringToString(deps[i])).c_str());
                    return false;
                }
            }
        }

        return true;
    }

    bool ShaderCache::IsGlobalCacheValid()
    {
        if (!metadataLoaded)
        {
            return false;
        }

        // Check driver version
        std::string currentDriverVersion = GetDriverVersion();
        if (currentDriverVersion != metadata.driverVersion)
        {
            Log(("Driver version changed: " + metadata.driverVersion + " -> " + currentDriverVersion).c_str());
            return false;
        }

        // Check adapter LUID
        uint64_t currentLUID = GetAdapterLUID();
        if (currentLUID != metadata.adapterLUID)
        {
            Log("Adapter LUID changed");
            return false;
        }

        return true;
    }

    bool ShaderCache::CompileAndCache(const std::wstring& shaderName, ID3DBlob** shader)
    {
        std::wstring sourcePath = GetSourcePath(shaderName);

        if (!CompileDXRLibrary(sourcePath, shader))
        {
            Log(("Failed to compile DXR shader: " + JsonHelper::WStringToString(shaderName)).c_str());
            return false;
        }

        // Save to cache
        std::wstring cachePath = GetCachePath(shaderName);
        if (!SaveShaderToFile(*shader, cachePath))
        {
            Log(("Failed to save shader to cache: " + JsonHelper::WStringToString(shaderName)).c_str());
            // Continue anyway - shader is compiled
        }

        // Update metadata
        ShaderCacheInfo info;
        info.sourceHash = CalculateFileHash(sourcePath);
        info.compiledAt = GetCurrentTimestamp();

        // Calculate dependency hashes
        auto defIt = shaderDefinitions.find(shaderName);
        if (defIt != shaderDefinitions.end())
        {
            for (const auto& dep : defIt->second.dependencies)
            {
                std::wstring depPath = sourceDir + dep;
                ShaderDependency depInfo;
                depInfo.filename = dep;
                depInfo.hash = CalculateFileHash(depPath);
                info.dependencies.push_back(depInfo);
            }
        }

        metadata.shaders[shaderName] = info;
        metadata.driverVersion = GetDriverVersion();
        metadata.adapterLUID = GetAdapterLUID();

        SaveMetadata();

        Log(("Compiled and cached: " + JsonHelper::WStringToString(shaderName)).c_str());
        return true;
    }

    bool ShaderCache::CompileComputeAndCache(const std::wstring& shaderName, const std::wstring& entryPoint, ID3DBlob** shader)
    {
        std::wstring sourcePath = GetSourcePath(shaderName);

        if (!CompileComputeShader(sourcePath, entryPoint, shader))
        {
            Log(("Failed to compile compute shader: " + JsonHelper::WStringToString(shaderName)).c_str());
            return false;
        }

        // Save to cache
        std::wstring cachePath = GetCachePath(shaderName);
        if (!SaveShaderToFile(*shader, cachePath))
        {
            Log(("Failed to save compute shader to cache: " + JsonHelper::WStringToString(shaderName)).c_str());
        }

        // Update metadata
        ShaderCacheInfo info;
        info.sourceHash = CalculateFileHash(sourcePath);
        info.compiledAt = GetCurrentTimestamp();

        metadata.shaders[shaderName] = info;
        metadata.driverVersion = GetDriverVersion();
        metadata.adapterLUID = GetAdapterLUID();

        SaveMetadata();

        Log(("Compiled and cached compute shader: " + JsonHelper::WStringToString(shaderName)).c_str());
        return true;
    }

    bool ShaderCache::LoadFromCache(const std::wstring& shaderName, ID3DBlob** shader)
    {
        std::wstring cachePath = GetCachePath(shaderName);

        std::ifstream file(cachePath, std::ios::binary | std::ios::ate);
        if (!file.is_open())
        {
            return false;
        }

        std::streamsize size = file.tellg();
        file.seekg(0, std::ios::beg);

        if (size <= 0)
        {
            return false;
        }

        HRESULT hr = D3DCreateBlob(static_cast<SIZE_T>(size), shader);
        if (FAILED(hr))
        {
            return false;
        }

        if (!file.read(static_cast<char*>((*shader)->GetBufferPointer()), size))
        {
            (*shader)->Release();
            *shader = nullptr;
            return false;
        }

        return true;
    }

    bool ShaderCache::LoadMetadata()
    {
        std::wstring metadataPath = cacheDir + L"shader_cache.json";
        std::ifstream file(metadataPath);
        if (!file.is_open())
        {
            return false;
        }

        std::stringstream buffer;
        buffer << file.rdbuf();
        std::string content = buffer.str();

        // Simple JSON parsing (minimal implementation)
        // Looking for: version, driverVersion, adapterLUID, shaders
        
        auto findValue = [&content](const std::string& key) -> std::string {
            std::string searchKey = "\"" + key + "\":";
            size_t pos = content.find(searchKey);
            if (pos == std::string::npos) return "";
            pos += searchKey.length();
            while (pos < content.length() && (content[pos] == ' ' || content[pos] == '\t')) pos++;
            if (pos >= content.length()) return "";
            
            if (content[pos] == '"')
            {
                pos++;
                size_t end = content.find('"', pos);
                if (end == std::string::npos) return "";
                return content.substr(pos, end - pos);
            }
            else
            {
                size_t end = pos;
                while (end < content.length() && content[end] != ',' && content[end] != '}' && content[end] != '\n')
                    end++;
                std::string val = content.substr(pos, end - pos);
                // Trim whitespace
                while (!val.empty() && (val.back() == ' ' || val.back() == '\t' || val.back() == '\r'))
                    val.pop_back();
                return val;
            }
        };

        try
        {
            std::string versionStr = findValue("version");
            if (!versionStr.empty())
                metadata.version = std::stoi(versionStr);

            metadata.driverVersion = findValue("driverVersion");

            std::string luidStr = findValue("adapterLUID");
            if (!luidStr.empty())
                metadata.adapterLUID = std::stoull(luidStr);

            // Parse shaders section (simplified - just looking for shader names and hashes)
            size_t shadersPos = content.find("\"shaders\"");
            if (shadersPos != std::string::npos)
            {
                size_t startBrace = content.find('{', shadersPos);
                if (startBrace != std::string::npos)
                {
                    // Find each shader entry
                    size_t pos = startBrace;
                    while (pos < content.length())
                    {
                        // Find shader name
                        size_t nameStart = content.find('"', pos + 1);
                        if (nameStart == std::string::npos) break;
                        nameStart++;
                        size_t nameEnd = content.find('"', nameStart);
                        if (nameEnd == std::string::npos) break;

                        std::string shaderName = content.substr(nameStart, nameEnd - nameStart);
                        
                        // Skip if it's a known key
                        if (shaderName == "sourceHash" || shaderName == "dependencies" || 
                            shaderName == "compiledAt" || shaderName == "version" ||
                            shaderName == "driverVersion" || shaderName == "adapterLUID" ||
                            shaderName == "shaders")
                        {
                            pos = nameEnd + 1;
                            continue;
                        }

                        // Find sourceHash for this shader
                        size_t shaderSection = content.find('{', nameEnd);
                        if (shaderSection == std::string::npos) break;
                        
                        size_t sectionEnd = content.find('}', shaderSection);
                        if (sectionEnd == std::string::npos) break;

                        std::string section = content.substr(shaderSection, sectionEnd - shaderSection + 1);
                        
                        ShaderCacheInfo info;
                        
                        // Find sourceHash in section
                        size_t hashPos = section.find("\"sourceHash\"");
                        if (hashPos != std::string::npos)
                        {
                            size_t hashStart = section.find('"', hashPos + 12);
                            if (hashStart != std::string::npos)
                            {
                                hashStart++;
                                size_t hashEnd = section.find('"', hashStart);
                                if (hashEnd != std::string::npos)
                                {
                                    info.sourceHash = section.substr(hashStart, hashEnd - hashStart);
                                }
                            }
                        }

                        // Parse dependencies
                        size_t depsPos = section.find("\"dependencies\"");
                        if (depsPos != std::string::npos)
                        {
                            size_t depsStart = section.find('{', depsPos);
                            size_t depsEnd = section.find('}', depsStart);
                            if (depsStart != std::string::npos && depsEnd != std::string::npos)
                            {
                                std::string depsSection = section.substr(depsStart + 1, depsEnd - depsStart - 1);
                                
                                // Parse each dependency
                                size_t depPos = 0;
                                while (depPos < depsSection.length())
                                {
                                    size_t depNameStart = depsSection.find('"', depPos);
                                    if (depNameStart == std::string::npos) break;
                                    depNameStart++;
                                    size_t depNameEnd = depsSection.find('"', depNameStart);
                                    if (depNameEnd == std::string::npos) break;
                                    
                                    std::string depName = depsSection.substr(depNameStart, depNameEnd - depNameStart);
                                    
                                    size_t depHashStart = depsSection.find('"', depNameEnd + 1);
                                    if (depHashStart == std::string::npos) break;
                                    depHashStart++;
                                    size_t depHashEnd = depsSection.find('"', depHashStart);
                                    if (depHashEnd == std::string::npos) break;
                                    
                                    ShaderDependency dep;
                                    dep.filename = JsonHelper::StringToWString(depName);
                                    dep.hash = depsSection.substr(depHashStart, depHashEnd - depHashStart);
                                    info.dependencies.push_back(dep);
                                    
                                    depPos = depHashEnd + 1;
                                }
                            }
                        }

                        metadata.shaders[JsonHelper::StringToWString(shaderName)] = info;
                        pos = sectionEnd + 1;
                    }
                }
            }

            Log(("Loaded metadata: driver=" + metadata.driverVersion + 
                 ", shaders=" + std::to_string(metadata.shaders.size())).c_str());
            return true;
        }
        catch (const std::exception& e)
        {
            Log(("Failed to parse metadata: " + std::string(e.what())).c_str());
            return false;
        }
    }

    bool ShaderCache::SaveMetadata()
    {
        std::wstring metadataPath = cacheDir + L"shader_cache.json";
        std::ofstream file(metadataPath);
        if (!file.is_open())
        {
            Log("Failed to open metadata file for writing");
            return false;
        }

        // Write JSON manually
        file << "{\n";
        file << "  \"version\": " << metadata.version << ",\n";
        file << "  \"driverVersion\": \"" << JsonHelper::EscapeString(metadata.driverVersion) << "\",\n";
        file << "  \"adapterLUID\": " << metadata.adapterLUID << ",\n";
        file << "  \"shaders\": {\n";

        bool first = true;
        for (const auto& [name, info] : metadata.shaders)
        {
            if (!first) file << ",\n";
            first = false;

            file << "    \"" << JsonHelper::WStringToString(name) << "\": {\n";
            file << "      \"sourceHash\": \"" << JsonHelper::EscapeString(info.sourceHash) << "\",\n";
            file << "      \"compiledAt\": \"" << JsonHelper::EscapeString(info.compiledAt) << "\",\n";
            file << "      \"dependencies\": {";

            bool firstDep = true;
            for (const auto& dep : info.dependencies)
            {
                if (!firstDep) file << ",";
                firstDep = false;
                file << "\n        \"" << JsonHelper::WStringToString(dep.filename) 
                     << "\": \"" << JsonHelper::EscapeString(dep.hash) << "\"";
            }

            if (!info.dependencies.empty()) file << "\n      ";
            file << "}\n";
            file << "    }";
        }

        file << "\n  }\n";
        file << "}\n";

        return true;
    }

    std::string ShaderCache::GetDriverVersion()
    {
        if (!dxContext)
            return "unknown";

        IDXGIAdapter1* adapter = dxContext->GetAdapter();
        if (!adapter)
            return "unknown";

        DXGI_ADAPTER_DESC1 desc;
        if (FAILED(adapter->GetDesc1(&desc)))
            return "unknown";

        // Get driver version from registry using adapter LUID
        HKEY hKey;
        std::wstring keyPath = L"SOFTWARE\\Microsoft\\DirectX\\";
        
        // Build version string from LUID and device info
        // Format: VendorId-DeviceId-SubSysId-Revision
        char buffer[256];
        sprintf_s(buffer, "%04X-%04X-%08X-%04X",
            desc.VendorId, desc.DeviceId, desc.SubSysId, desc.Revision);

        return std::string(buffer);
    }

    uint64_t ShaderCache::GetAdapterLUID()
    {
        if (!dxContext)
            return 0;

        IDXGIAdapter1* adapter = dxContext->GetAdapter();
        if (!adapter)
            return 0;

        DXGI_ADAPTER_DESC1 desc;
        if (FAILED(adapter->GetDesc1(&desc)))
            return 0;

        // Combine LUID into single 64-bit value
        return (static_cast<uint64_t>(desc.AdapterLuid.HighPart) << 32) | 
               static_cast<uint64_t>(desc.AdapterLuid.LowPart);
    }

    std::string ShaderCache::CalculateFileHash(const std::wstring& path)
    {
        // Read file contents
        std::ifstream file(path, std::ios::binary);
        if (!file.is_open())
        {
            return "";
        }

        std::stringstream buffer;
        buffer << file.rdbuf();
        std::string content = buffer.str();

        // Calculate SHA-256 using BCrypt
        BCRYPT_ALG_HANDLE hAlg = nullptr;
        BCRYPT_HASH_HANDLE hHash = nullptr;
        NTSTATUS status;

        status = BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
        if (!BCRYPT_SUCCESS(status))
        {
            return "";
        }

        DWORD hashLength = 0;
        DWORD resultLength = 0;
        BCryptGetProperty(hAlg, BCRYPT_HASH_LENGTH, (PUCHAR)&hashLength, sizeof(hashLength), &resultLength, 0);

        std::vector<BYTE> hash(hashLength);

        status = BCryptCreateHash(hAlg, &hHash, nullptr, 0, nullptr, 0, 0);
        if (!BCRYPT_SUCCESS(status))
        {
            BCryptCloseAlgorithmProvider(hAlg, 0);
            return "";
        }

        status = BCryptHashData(hHash, (PUCHAR)content.data(), (ULONG)content.size(), 0);
        if (!BCRYPT_SUCCESS(status))
        {
            BCryptDestroyHash(hHash);
            BCryptCloseAlgorithmProvider(hAlg, 0);
            return "";
        }

        status = BCryptFinishHash(hHash, hash.data(), hashLength, 0);
        BCryptDestroyHash(hHash);
        BCryptCloseAlgorithmProvider(hAlg, 0);

        if (!BCRYPT_SUCCESS(status))
        {
            return "";
        }

        // Convert to hex string
        std::stringstream ss;
        for (BYTE b : hash)
        {
            ss << std::hex << std::setfill('0') << std::setw(2) << (int)b;
        }

        return ss.str();
    }

    std::wstring ShaderCache::GetSourcePath(const std::wstring& shaderName) const
    {
        // shaderDefinitions から実際のソースファイル名を取得
        // (例: BuildPhotonHashClear -> BuildPhotonHash.hlsl)
        auto it = shaderDefinitions.find(shaderName);
        if (it != shaderDefinitions.end())
        {
            return sourceDir + it->second.name + L".hlsl";
        }
        // フォールバック: shaderName をそのまま使用
        return sourceDir + shaderName + L".hlsl";
    }

    std::wstring ShaderCache::GetCachePath(const std::wstring& shaderName) const
    {
        return cacheDir + shaderName + L".cso";
    }

    std::string ShaderCache::GetCurrentTimestamp()
    {
        auto now = std::chrono::system_clock::now();
        auto time = std::chrono::system_clock::to_time_t(now);
        std::tm tm;
        localtime_s(&tm, &time);

        std::stringstream ss;
        ss << std::put_time(&tm, "%Y-%m-%dT%H:%M:%S");
        return ss.str();
    }

    bool ShaderCache::CompileDXRLibrary(const std::wstring& sourcePath, ID3DBlob** shader)
    {
        ComPtr<IDxcUtils> utils;
        ComPtr<IDxcCompiler3> compiler;
        ComPtr<IDxcIncludeHandler> includeHandler;

        HRESULT hr = DxcCreateInstance(CLSID_DxcUtils, IID_PPV_ARGS(&utils));
        if (FAILED(hr))
        {
            Log("Failed to create IDxcUtils", hr);
            return false;
        }

        hr = DxcCreateInstance(CLSID_DxcCompiler, IID_PPV_ARGS(&compiler));
        if (FAILED(hr))
        {
            Log("Failed to create IDxcCompiler3", hr);
            return false;
        }

        hr = utils->CreateDefaultIncludeHandler(&includeHandler);
        if (FAILED(hr))
        {
            Log("Failed to create include handler", hr);
            return false;
        }

        // Load source file
        UINT32 codePage = CP_UTF8;
        ComPtr<IDxcBlobEncoding> sourceBlob;
        hr = utils->LoadFile(sourcePath.c_str(), &codePage, &sourceBlob);
        if (FAILED(hr) || !sourceBlob)
        {
            Log("Failed to load shader source", hr);
            return false;
        }

        DxcBuffer sourceBuffer = {};
        sourceBuffer.Ptr = sourceBlob->GetBufferPointer();
        sourceBuffer.Size = sourceBlob->GetBufferSize();
        sourceBuffer.Encoding = DXC_CP_UTF8;

        // Compile arguments
        std::vector<LPCWSTR> arguments;
        arguments.push_back(L"-T");
        arguments.push_back(L"lib_6_3");
        arguments.push_back(L"-Zpr");  // Row-major matrices
        arguments.push_back(L"-Zi");   // Debug info
        arguments.push_back(L"-Qembed_debug");
        arguments.push_back(L"-I");
        arguments.push_back(sourceDir.c_str());
        arguments.push_back(L"-D");
        arguments.push_back(L"ENABLE_NRD_GBUFFER=1");

        ComPtr<IDxcResult> result;
        hr = compiler->Compile(
            &sourceBuffer,
            arguments.data(),
            static_cast<UINT32>(arguments.size()),
            includeHandler.Get(),
            IID_PPV_ARGS(&result));

        if (FAILED(hr) || !result)
        {
            Log("DXC compile call failed", hr);
            return false;
        }

        HRESULT status = S_OK;
        result->GetStatus(&status);

        // Get error messages
        ComPtr<IDxcBlobUtf8> errors;
        result->GetOutput(DXC_OUT_ERRORS, IID_PPV_ARGS(&errors), nullptr);
        if (errors && errors->GetStringLength() > 0)
        {
            Log(errors->GetStringPointer());
        }

        if (FAILED(status))
        {
            Log("DXC compilation failed");
            return false;
        }

        // Get compiled shader
        ComPtr<IDxcBlob> dxilBlob;
        hr = result->GetOutput(DXC_OUT_OBJECT, IID_PPV_ARGS(&dxilBlob), nullptr);
        if (FAILED(hr) || !dxilBlob)
        {
            Log("Failed to get DXIL output", hr);
            return false;
        }

        // Create D3D blob from DXIL
        hr = D3DCreateBlob(static_cast<SIZE_T>(dxilBlob->GetBufferSize()), shader);
        if (FAILED(hr))
        {
            Log("Failed to create blob for DXIL", hr);
            return false;
        }

        memcpy((*shader)->GetBufferPointer(), dxilBlob->GetBufferPointer(), dxilBlob->GetBufferSize());
        return true;
    }

    bool ShaderCache::CompileComputeShader(const std::wstring& sourcePath, const std::wstring& entryPoint, ID3DBlob** shader)
    {
        ComPtr<ID3DBlob> errorBlob;
        std::string entryPointStr = JsonHelper::WStringToString(entryPoint);

        HRESULT hr = D3DCompileFromFile(
            sourcePath.c_str(),
            nullptr,
            D3D_COMPILE_STANDARD_FILE_INCLUDE,
            entryPointStr.c_str(),
            "cs_5_1",
            D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_DEBUG,
            0,
            shader,
            &errorBlob);

        if (FAILED(hr))
        {
            if (errorBlob)
            {
                Log((char*)errorBlob->GetBufferPointer());
            }
            Log("Compute shader compilation failed", hr);
            return false;
        }

        return true;
    }

    bool ShaderCache::SaveShaderToFile(ID3DBlob* shader, const std::wstring& path)
    {
        std::ofstream file(path, std::ios::binary);
        if (!file.is_open())
        {
            return false;
        }

        file.write(static_cast<const char*>(shader->GetBufferPointer()), shader->GetBufferSize());
        return file.good();
    }

    void ShaderCache::ClearCache()
    {
        Log("Clearing shader cache");

        // Delete all .cso files and metadata
        try
        {
            for (const auto& entry : std::filesystem::directory_iterator(cacheDir))
            {
                if (entry.path().extension() == L".cso" || 
                    entry.path().filename() == L"shader_cache.json")
                {
                    std::filesystem::remove(entry.path());
                }
            }
        }
        catch (const std::exception& e)
        {
            Log(("Failed to clear cache: " + std::string(e.what())).c_str());
        }

        metadata.shaders.clear();
        globalCacheValid = false;
    }

    bool ShaderCache::NeedsRecompilation() const
    {
        if (!globalCacheValid)
            return true;

        for (const auto& [name, def] : shaderDefinitions)
        {
            auto it = metadata.shaders.find(name);
            if (it == metadata.shaders.end())
                return true;

            // Check if cache file exists
            std::wstring cachePath = cacheDir + name + L".cso";
            if (!std::filesystem::exists(cachePath))
                return true;
        }

        return false;
    }

    std::wstring ShaderCache::GetStatusMessage() const
    {
        return statusMessage;
    }

    bool ShaderCache::PrecompileAll()
    {
        Log("Pre-compiling all shaders...");
        statusMessage = L"Compiling shaders...";

        bool success = true;
        int compiled = 0;
        int total = static_cast<int>(shaderDefinitions.size());

        for (const auto& [name, def] : shaderDefinitions)
        {
            ComPtr<ID3DBlob> shader;
            if (!GetShader(name, &shader))
            {
                Log(("Failed to compile: " + JsonHelper::WStringToString(name)).c_str());
                success = false;
            }
            else
            {
                compiled++;
            }

            statusMessage = L"Compiled " + std::to_wstring(compiled) + L"/" + std::to_wstring(total) + L" shaders";
        }

        if (success)
        {
            statusMessage = L"All shaders compiled successfully";
            Log("All shaders compiled successfully");
        }
        else
        {
            statusMessage = L"Some shaders failed to compile";
            Log("Some shaders failed to compile");
        }

        return success;
    }

    bool ShaderCache::TryGetHlslDefineUInt(const std::wstring& sourcePath, const std::string& defineName, uint32_t* outValue)
    {
        if (!outValue)
            return false;

        std::ifstream file(sourcePath);
        if (!file.is_open())
        {
            Log("Failed to open HLSL source for define parsing");
            return false;
        }

        std::string line;
        while (std::getline(file, line))
        {
            size_t start = line.find_first_not_of(" \t");
            if (start == std::string::npos)
                continue;

            if (line.compare(start, 7, "#define") != 0)
                continue;

            std::istringstream iss(line.substr(start));
            std::string directive;
            std::string name;
            std::string value;
            iss >> directive >> name >> value;
            if (directive != "#define" || name != defineName || value.empty())
                continue;

            try
            {
                unsigned long parsed = std::stoul(value, nullptr, 0);
                *outValue = static_cast<uint32_t>(parsed);
                return true;
            }
            catch (...)
            {
                return false;
            }
        }

        return false;
    }
}
