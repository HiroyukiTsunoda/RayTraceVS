#pragma once

#include <fstream>
#include <cstdio>
#include <Windows.h>

namespace RayTraceVS::DXEngine
{
    // Debug mode flag: 0 = minimal logging (errors only), 1 = verbose logging
    inline int g_DebugMode = 0;

    // Set debug mode (call from NativeBridge or application startup)
    inline void SetDebugMode(int mode) { g_DebugMode = mode; }
    inline int GetDebugMode() { return g_DebugMode; }

    // Internal log function - writes to file
    inline void WriteLogToFile(const char* prefix, const char* message)
    {
        std::ofstream log("C:\\git\\RayTraceVS\\debug.log", std::ios::app);
        if (log.is_open())
        {
            log << prefix << message << std::endl;
            log.close();
        }
    }

    // ERROR log - Always output (critical errors that need immediate attention)
    inline void LogError(const char* message)
    {
        WriteLogToFile("[ERROR] ", message);
        OutputDebugStringA("[ERROR] ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");
    }

    inline void LogError(const char* message, HRESULT hr)
    {
        char buf[512];
        sprintf_s(buf, "%s: 0x%08X", message, hr);
        LogError(buf);
    }

    // WARN log - Always output (warnings that should be addressed)
    inline void LogWarn(const char* message)
    {
        WriteLogToFile("[WARN] ", message);
    }

    // INFO log - Output once during initialization (important milestones)
    inline void LogInfo(const char* message)
    {
        WriteLogToFile("[INFO] ", message);
    }

    // DEBUG log - Only output when DebugMode is enabled (verbose debugging)
    inline void LogDebug(const char* message)
    {
        if (g_DebugMode > 0)
        {
            WriteLogToFile("[DEBUG] ", message);
        }
    }

    inline void LogDebug(const char* message, HRESULT hr)
    {
        if (g_DebugMode > 0)
        {
            char buf[512];
            sprintf_s(buf, "%s: 0x%08X", message, hr);
            LogDebug(buf);
        }
    }

    // Clear log file (call at startup)
    inline void ClearLogFile()
    {
        std::ofstream log("C:\\git\\RayTraceVS\\debug.log", std::ios::trunc);
        log.close();
    }
}

// Convenience macros
#define LOG_ERROR(msg) RayTraceVS::DXEngine::LogError(msg)
#define LOG_ERROR_HR(msg, hr) RayTraceVS::DXEngine::LogError(msg, hr)
#define LOG_WARN(msg) RayTraceVS::DXEngine::LogWarn(msg)
#define LOG_INFO(msg) RayTraceVS::DXEngine::LogInfo(msg)
#define LOG_DEBUG(msg) RayTraceVS::DXEngine::LogDebug(msg)
#define LOG_DEBUG_HR(msg, hr) RayTraceVS::DXEngine::LogDebug(msg, hr)
