#pragma once

#include <fstream>
#include <cstdio>
#include <Windows.h>

namespace RayTraceVS::DXEngine
{
    // Logging control: 0 = errors only, 1 = enable info/warn/debug
    inline int g_LogEnabled = 0;
    // Debug mode flag: 0 = debug disabled, 1 = debug enabled
    inline int g_DebugMode = 0;

    // Set logging mode (call from NativeBridge or application startup)
    inline void SetLogEnabled(int enabled) { g_LogEnabled = enabled; }
    inline int GetLogEnabled() { return g_LogEnabled; }
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

    // WARN log - Output only when logging is enabled
    inline void LogWarn(const char* message)
    {
        if (g_LogEnabled > 0)
        {
            WriteLogToFile("[WARN] ", message);
        }
    }

    // INFO log - Output only when logging is enabled
    inline void LogInfo(const char* message)
    {
        if (g_LogEnabled > 0)
        {
            WriteLogToFile("[INFO] ", message);
        }
    }

    // DEBUG log - Output only when logging and debug mode are enabled
    inline void LogDebug(const char* message)
    {
        if (g_LogEnabled > 0 && g_DebugMode > 0)
        {
            WriteLogToFile("[DEBUG] ", message);
        }
    }

    inline void LogDebug(const char* message, HRESULT hr)
    {
        if (g_LogEnabled > 0 && g_DebugMode > 0)
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
