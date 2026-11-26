#include <windows.h>
#include <stdio.h>
#include <stdint.h>

typedef void (*scriptRegister_t)(HMODULE, void(*)());
typedef void (*scriptWait_t)(DWORD);
typedef void (*nativeInit_t)(uint64_t hash);
typedef void (*nativePush64_t)(uint64_t val);
typedef uint64_t* (*nativeCall_t)();
typedef BYTE* (*getEntityAddr_t)(int handle);
typedef int (*worldGetAllVeh_t)(int* arr, int arrSize);

scriptWait_t pScriptWait = nullptr;
nativeInit_t pNativeInit = nullptr;
nativePush64_t pNativePush64 = nullptr;
nativeCall_t pNativeCall = nullptr;
getEntityAddr_t pGetEntityAddr = nullptr;
worldGetAllVeh_t pWorldGetAllVeh = nullptr;

typedef void (__cdecl* init_native_fn)(void* init, void* push, void* call, void* getAddr, void* getAllVeh);
typedef void (__cdecl* tick_fn)();

tick_fn g_CoreTick = nullptr;
tick_fn* g_ModTicks = nullptr;
int g_ModCount = 0;

void LogMessage(const char* msg) {
    HANDLE hFile = CreateFileA("MiniDotNet.log", FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(hFile, msg, (DWORD)lstrlenA(msg), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        CloseHandle(hFile);
    }
}

void LoadPluginDll(const wchar_t* dllPath, bool isCore) {
    HMODULE hMod = LoadLibraryW(dllPath);
    if (!hMod) return;

    auto initNatives = (init_native_fn)GetProcAddress(hMod, "SetupNatives");
    auto tickFn = (tick_fn)GetProcAddress(hMod, "OnTick");
    if (!tickFn) return;

    if (initNatives) {
        initNatives((void*)pNativeInit, (void*)pNativePush64, (void*)pNativeCall, (void*)pGetEntityAddr, (void*)pWorldGetAllVeh);
    }

    if (isCore) {
        g_CoreTick = tickFn;
        LogMessage("[SUCCESS] MiniDotNet Core Engine Active!");
    } else if (g_ModTicks && g_ModCount < 32) {
        g_ModTicks[g_ModCount++] = tickFn;
        LogMessage("[SUCCESS] Loaded External AOT Mod Plugin!");
    }
}

void LoadAllPlugins() {
    g_ModTicks = (tick_fn*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 32 * sizeof(tick_fn));

    wchar_t* currentDir = new wchar_t[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, currentDir);

    wchar_t* coreDll = new wchar_t[MAX_PATH];
    swprintf_s(coreDll, MAX_PATH, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);
    LoadPluginDll(coreDll, true);
    delete[] coreDll;

    wchar_t* searchPattern = new wchar_t[MAX_PATH];
    swprintf_s(searchPattern, MAX_PATH, L"%s\\scripts\\*.mod.dll", currentDir);

    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(searchPattern, &findData);
    delete[] searchPattern;

    if (hFind != INVALID_HANDLE_VALUE) {
        do {
            if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
                wchar_t* modPath = new wchar_t[MAX_PATH];
                swprintf_s(modPath, MAX_PATH, L"%s\\scripts\\%s", currentDir, findData.cFileName);
                LoadPluginDll(modPath, false);
                delete[] modPath;
            }
        } while (FindNextFileW(hFind, &findData));
        FindClose(hFind);
    }
    delete[] currentDir;
}

void ScriptMain() {
    LoadAllPlugins();
    while (true) {
        if (g_CoreTick) g_CoreTick();
        for (int i = 0; i < g_ModCount; i++) {
            if (g_ModTicks[i]) g_ModTicks[i]();
        }
        if (pScriptWait) pScriptWait(0);
        else Sleep(16);
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        HMODULE hSHV = GetModuleHandleA("ScriptHookV.dll");
        if (!hSHV) hSHV = LoadLibraryA("ScriptHookV.dll");

        if (hSHV) {
            auto pRegister = (scriptRegister_t)GetProcAddress(hSHV, "?scriptRegister@@YAXPEAUHINSTANCE__@@P6AXXZ@Z");
            pScriptWait = (scriptWait_t)GetProcAddress(hSHV, "?scriptWait@@YAXK@Z");
            pNativeInit = (nativeInit_t)GetProcAddress(hSHV, "?nativeInit@@YAX_K@Z");
            pNativePush64 = (nativePush64_t)GetProcAddress(hSHV, "?nativePush64@@YAX_K@Z");
            pNativeCall = (nativeCall_t)GetProcAddress(hSHV, "?nativeCall@@YAPEA_KXZ");
            pGetEntityAddr = (getEntityAddr_t)GetProcAddress(hSHV, "?getScriptHandleBaseAddress@@YAPEAEH@Z");
            pWorldGetAllVeh = (worldGetAllVeh_t)GetProcAddress(hSHV, "?worldGetAllVehicles@@YAHPEAHH@Z");

            if (pRegister) pRegister(hModule, ScriptMain);
        }
    }
    return TRUE;
}
