#include <filesystem>
#include <fstream>
#include <iostream>
#include <regex>
#include <stdexcept>
#include <chrono>
#include "../src/RayTraceVS.DXEngine/ShaderCache.h"

namespace fs = std::filesystem;
using RayTraceVS::DXEngine::ShaderCache;

static int checks = 0;
static void Require(bool condition, const char* message)
{
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

static void Write(const fs::path& path, const std::string& contents)
{
    std::ofstream file(path, std::ios::binary);
    file << contents;
    if (!file) throw std::runtime_error("Could not write test fixture");
}

static std::string Read(const fs::path& path)
{
    std::ifstream file(path, std::ios::binary);
    return { std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>() };
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        if (argc != 2) throw std::runtime_error("Expected an output directory");
        const fs::path fixture = fs::path(argv[1]) / (L"shader-cache-" + std::to_wstring(GetCurrentProcessId()));
        const fs::path source = fixture / L"source";
        const fs::path cacheDir = fixture / L"cache";
        fs::create_directories(source);
        fs::create_directories(cacheDir);
        Write(source / L"SharedTypes.h", "#define CACHE_VALUE 0.1\n");
        Write(source / L"BoxIntersection.hlsli", "#define CACHE_SCALE 1.0\n");
        Write(source / L"RayTraceCompute.hlsl",
            "#include \"SharedTypes.h\"\n#include \"BoxIntersection.hlsli\"\n"
            "RWStructuredBuffer<float> Output : register(u0);\n"
            "[numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { Output[id.x] = CACHE_VALUE * CACHE_SCALE; }\n");

        auto Load = [&] {
            ShaderCache cache(nullptr); // Compilation and hashing require no GPU device.
            if (!cache.Initialize(cacheDir.wstring() + L"\\", source.wstring() + L"\\"))
                throw std::runtime_error("Cache initialization failed");
            ComPtr<ID3DBlob> shader;
            if (!cache.GetComputeShader(L"RayTraceCompute", L"CSMain", &shader) || !shader)
                throw std::runtime_error("Compute shader compilation/loading failed");
        };

        Load();
        const fs::path metadataFile = cacheDir / L"shader_cache.json";
        const fs::path compiledFile = cacheDir / L"RayTraceCompute.cso";
        auto metadata = Read(metadataFile);
        Require(metadata.find("SharedTypes.h") != std::string::npos && metadata.find("BoxIntersection.hlsli") != std::string::npos,
                "Compute shader metadata must retain all include dependencies");

        const auto sentinel = fs::file_time_type::clock::now() - std::chrono::hours(24);
        fs::last_write_time(compiledFile, sentinel);
        const auto preservedTime = fs::last_write_time(compiledFile);
        Load();
        Require(fs::last_write_time(compiledFile) == preservedTime, "Unchanged shader must load from cache after restart");

        const std::regex dependencies("(\"SharedTypes\\.h\"\\s*:\\s*\"[a-f0-9]+\"),\\s*(\"BoxIntersection\\.hlsli\"\\s*:\\s*\"[a-f0-9]+\")");
        const auto reordered = std::regex_replace(metadata, dependencies, "$2, $1");
        Require(reordered != metadata, "Dependency-order fixture was not modified");
        Write(metadataFile, reordered);
        Load();
        Require(fs::last_write_time(compiledFile) == preservedTime, "Dependency object order must not invalidate cache");

        Write(source / L"SharedTypes.h", "#define CACHE_VALUE 0.75\n");
        Load();
        Require(fs::last_write_time(compiledFile) != preservedTime, "Changed include must recompile the shader");

        metadata = Read(metadataFile);
        metadata.replace(metadata.find("SharedTypes.h"), std::string("SharedTypes.h").size(), "RenamedTypes.h");
        Write(metadataFile, metadata);
        fs::last_write_time(compiledFile, sentinel);
        Load();
        Require(fs::last_write_time(compiledFile) != preservedTime, "Matching hashes with a different dependency name must not be accepted");

        std::cout << "Shader cache persistence checks passed: " << checks << '\n';
        return 0;
    }
    catch (const std::exception& ex)
    {
        std::cerr << ex.what() << '\n';
        return 1;
    }
}
