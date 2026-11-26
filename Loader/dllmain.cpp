#include <windows.h>
#include <stdio.h>
#include <stdint.h>

typedef void (*scriptRegister_t)(HMODULE, void(*)());
typedef void (*scriptUnregister_t)(HMODULE);
typedef void (*scriptWait_t)(DWORD);
typedef void (*nativeInit_t)(uint64_t hash);
typedef void (*nativePush64_t)(uint64_t val);
typedef uint64_t* (*nativeCall_t)();

scriptWait_t pScriptWait = nullptr;
nativeInit_t pNativeInit = nullptr;
nativePush64_t pNativePush64 = nullptr;
nativeCall_t pNativeCall = nullptr;

typedef void (__cdecl* init_native_fn)(void* init, void* push, void* call);
typedef void (__cdecl* tick_fn)();

tick_fn g_ManagedTick = nullptr;

void LogMessage(const char* msg) {
    HANDLE hFile = CreateFileA("MiniDotNet.log", FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(hFile, msg, (DWORD)lstrlenA(msg), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        CloseHandle(hFile);
    }
}

bool LoadAotCore() {
    wchar_t* currentDir = new wchar_t[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, currentDir);

    wchar_t* coreDll = new wchar_t[MAX_PATH];
    swprintf_s(coreDll, MAX_PATH, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);
    delete[] currentDir;

    LogMessage("[INFO] Loading Native AOT MiniDotNet.Core.dll...");
    HMODULE hCore = LoadLibraryW(coreDll);
    delete[] coreDll;

    if (!hCore) {
        char* buf = new char[256];
        sprintf_s(buf, 256, "[ERROR] Failed to load MiniDotNet.Core.dll. Error: %lu", GetLastError());
        LogMessage(buf);
        delete[] buf;
        return false;
    }

    auto initNatives = (init_native_fn)GetProcAddress(hCore, "SetupNatives");
    g_ManagedTick = (tick_fn)GetProcAddress(hCore, "OnTick");

    if (!g_ManagedTick) {
        LogMessage("[ERROR] Could not find OnTick export in MiniDotNet.Core.dll");
        return false;
    }

    if (initNatives) {
        initNatives((void*)pNativeInit, (void*)pNativePush64, (void*)pNativeCall);
        LogMessage("[SUCCESS] Native pointers passed to C# AOT Core!");
    }

    LogMessage("[SUCCESS] MiniDotNet AOT C# Engine Active!");
    return true;
}

void ScriptMain() {
    LogMessage("--- ScriptMain Started (Native AOT Mode) ---");
    if (!LoadAotCore()) return;

    while (true) {
        if (g_ManagedTick) {
            g_ManagedTick();
        }
        if (pScriptWait) pScriptWait(0);
        else Sleep(16);
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        LogMessage("--- MiniDotNet DLL Loaded! ---");

        HMODULE hSHV = GetModuleHandleA("ScriptHookV.dll");
        if (!hSHV) hSHV = LoadLibraryA("ScriptHookV.dll");

        if (hSHV) {
            auto pRegister = (scriptRegister_t)GetProcAddress(hSHV, "?scriptRegister@@YAXPEAUHINSTANCE__@@P6AXXZ@Z");
            pScriptWait = (scriptWait_t)GetProcAddress(hSHV, "?scriptWait@@YAXK@Z");
            pNativeInit = (nativeInit_t)GetProcAddress(hSHV, "?nativeInit@@YAX_K@Z");
            pNativePush64 = (nativePush64_t)GetProcAddress(hSHV, "?nativePush64@@YAX_K@Z");
            pNativeCall = (nativeCall_t)GetProcAddress(hSHV, "?nativeCall@@YAPEA_KXZ");

            if (pRegister) {
                pRegister(hModule, ScriptMain);
            }
        }
    }
    return TRUE;
}
