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

typedef void* (*mono_jit_init_version_t)(const char* file, const char* runtime_version);
typedef void (*mono_set_dirs_t)(const char* assembly_dir, const char* config_dir);
typedef void* (*mono_domain_assembly_open_t)(void* domain, const char* name);
typedef void* (*mono_assembly_get_image_t)(void* assembly);
typedef void* (*mono_class_from_name_t)(void* image, const char* name_space, const char* name);
typedef void* (*mono_class_get_method_from_name_t)(void* klass, const char* name, int param_count);
typedef void* (*mono_runtime_invoke_t)(void* method, void* obj, void** params, void** exc);

mono_runtime_invoke_t pMonoInvoke = nullptr;
void* g_MonoTickMethod = nullptr;
bool g_MonoReady = false;
int g_MonoWait = 0;

void LogMessage(const char* msg) {
    HANDLE hFile = CreateFileA("MiniDotNet.log", FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(hFile, msg, (DWORD)lstrlenA(msg), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        CloseHandle(hFile);
    }
}

extern "C" __declspec(dllexport) void __cdecl BridgeNativeInit(uint64_t hash) {
    if (pNativeInit) pNativeInit(hash);
}
extern "C" __declspec(dllexport) void __cdecl BridgeNativePush64(uint64_t val) {
    if (pNativePush64) pNativePush64(val);
}
extern "C" __declspec(dllexport) uint64_t __cdecl BridgeNativeCall() {
    if (!pNativeCall) return 0;
    uint64_t* res = pNativeCall();
    return res ? *res : 0;
}
extern "C" __declspec(dllexport) BYTE* __cdecl BridgeGetEntityAddr(int handle) {
    return pGetEntityAddr ? pGetEntityAddr(handle) : nullptr;
}
extern "C" __declspec(dllexport) int __cdecl BridgeGetAllVeh(int* arr, int arrSize) {
    return pWorldGetAllVeh ? pWorldGetAllVeh(arr, arrSize) : 0;
}

void InitMonoBridge(const char* baseDir) {
    char* monoDllPath = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    char* libDir = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    char* shimPath = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);

    wsprintfA(monoDllPath, "%s\\scripts\\mono\\mono-2.0-sgen.dll", baseDir);
    wsprintfA(libDir, "%s\\scripts\\mono\\lib", baseDir);
    wsprintfA(shimPath, "%s\\scripts\\ScriptHookVDotNet3.dll", baseDir);

    HMODULE hMono = LoadLibraryA(monoDllPath);
    if (!hMono) {
        LogMessage("[Mono] mono-2.0-sgen.dll not found, skipping Mono bridge.");
        return;
    }

    auto pSetDirs = (mono_set_dirs_t)GetProcAddress(hMono, "mono_set_dirs");
    auto pJitInit = (mono_jit_init_version_t)GetProcAddress(hMono, "mono_jit_init_version");
    auto pAsmOpen = (mono_domain_assembly_open_t)GetProcAddress(hMono, "mono_domain_assembly_open");
    auto pGetImage = (mono_assembly_get_image_t)GetProcAddress(hMono, "mono_assembly_get_image");
    auto pClassFromName = (mono_class_from_name_t)GetProcAddress(hMono, "mono_class_from_name");
    auto pGetMethod = (mono_class_get_method_from_name_t)GetProcAddress(hMono, "mono_class_get_method_from_name");
    pMonoInvoke = (mono_runtime_invoke_t)GetProcAddress(hMono, "mono_runtime_invoke");

    if (!pJitInit || !pAsmOpen || !pGetImage || !pClassFromName || !pGetMethod || !pMonoInvoke) return;

    if (pSetDirs) pSetDirs(libDir, libDir);
    void* domain = pJitInit("MiniDotNetDomain", "v4.0.30319");
    if (!domain) return;

    void* asmShim = pAsmOpen(domain, shimPath);
    if (!asmShim) {
        LogMessage("[Mono] Failed to open ScriptHookVDotNet3.dll");
        return;
    }

    void* img = pGetImage(asmShim);
    void* klass = pClassFromName(img, "GTA", "MonoBridgeHost");
    if (klass) {
        g_MonoTickMethod = pGetMethod(klass, "TickAll", 0);
        if (g_MonoTickMethod) {
            g_MonoReady = true;
            LogMessage("[SUCCESS] Mono Runtime & ScriptHookVDotNet3 Bridge Active!");
        }
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

    wchar_t* currentDir = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
    GetCurrentDirectoryW(MAX_PATH, currentDir);

    wchar_t* coreDll = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
    wsprintfW(coreDll, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);
    LoadPluginDll(coreDll, true);

    wchar_t* searchPattern = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
    wsprintfW(searchPattern, L"%s\\scripts\\*.mod.dll", currentDir);

    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(searchPattern, &findData);

    if (hFind != INVALID_HANDLE_VALUE) {
        do {
            if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
                wchar_t* modPath = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
                wsprintfW(modPath, L"%s\\scripts\\%s", currentDir, findData.cFileName);
                LoadPluginDll(modPath, false);
            }
        } while (FindNextFileW(hFind, &findData));
        FindClose(hFind);
    }
}

void ScriptMain() {
    LoadAllPlugins();

    char* ansiDir = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    GetCurrentDirectoryA(MAX_PATH, ansiDir);

    while (true) {
        if (g_CoreTick) g_CoreTick();
        for (int i = 0; i < g_ModCount; i++) {
            if (g_ModTicks[i]) g_ModTicks[i]();
        }

        if (!g_MonoReady) {
            g_MonoWait++;
            if (g_MonoWait == 200) InitMonoBridge(ansiDir);
        } else if (g_MonoTickMethod && pMonoInvoke) {
            void* exc = nullptr;
            pMonoInvoke(g_MonoTickMethod, nullptr, nullptr, &exc);
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
