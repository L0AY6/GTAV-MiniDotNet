#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <stdarg.h>
#include <string.h>

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
typedef int (__cdecl* register_mod_fn)(const wchar_t* fileName);
typedef uint8_t* (__cdecl* get_flags_fn)();

tick_fn g_CoreTick = nullptr;
tick_fn* g_ModTicks = nullptr;
int* g_ModSlots = nullptr;
int g_ModCount = 0;
register_mod_fn g_RegisterMod = nullptr;
uint8_t* g_Flags = nullptr;

typedef void* (*mono_jit_init_version_t)(const char* file, const char* runtime_version);
typedef void (*mono_set_dirs_t)(const char* assembly_dir, const char* config_dir);
typedef void* (*mono_domain_assembly_open_t)(void* domain, const char* name);
typedef void* (*mono_assembly_get_image_t)(void* assembly);
typedef void* (*mono_class_from_name_t)(void* image, const char* name_space, const char* name);
typedef void* (*mono_class_get_method_from_name_t)(void* klass, const char* name, int param_count);
typedef void* (*mono_runtime_invoke_t)(void* method, void* obj, void** params, void** exc);
typedef void* (*mono_object_to_string_t)(void* obj, void** exc);
typedef char* (*mono_string_to_utf8_t)(void* str);
typedef void (*mono_free_t)(void* ptr);

mono_runtime_invoke_t pMonoInvoke = nullptr;
mono_object_to_string_t pMonoObjToStr = nullptr;
mono_string_to_utf8_t pMonoStrToUtf8 = nullptr;
mono_free_t pMonoFree = nullptr;

void* g_MonoTickMethod = nullptr;
bool g_MonoReady = false;
bool g_MonoOff = false;
bool g_MonoExcLogged = false;
int g_MonoWait = 0;
int g_Iter = 0;

void LogMessage(const char* msg) {
    HANDLE hFile = CreateFileA("MiniDotNet.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        SYSTEMTIME st;
        GetLocalTime(&st);
        char* prefix = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 64);
        wsprintfA(prefix, "[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        DWORD written = 0;
        WriteFile(hFile, prefix, (DWORD)lstrlenA(prefix), &written, NULL);
        WriteFile(hFile, msg, (DWORD)lstrlenA(msg), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        HeapFree(GetProcessHeap(), 0, prefix);
        CloseHandle(hFile);
    }
}

void LogF(const char* fmt, ...) {
    char* buf = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 1024);
    va_list ap;
    va_start(ap, fmt);
    int n = _vsnprintf(buf, 1020, fmt, ap);
    va_end(ap);
    if (n < 0) n = 0;
    buf[n] = 0;
    LogMessage(buf);
    HeapFree(GetProcessHeap(), 0, buf);
}

bool PathExists(const char* p) {
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
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

void LogMonoException(void* exc) {
    if (!exc || !pMonoObjToStr || !pMonoStrToUtf8) return;
    void* strExc = nullptr;
    void* monoStr = pMonoObjToStr(exc, &strExc);
    if (!monoStr) return;
    char* utf8 = pMonoStrToUtf8(monoStr);
    if (!utf8) return;
    LogMessage(utf8);
    if (pMonoFree) pMonoFree(utf8);
}
void PreloadCoreDll(void* domain, mono_domain_assembly_open_t pAsmOpen, const char* baseDir, const char* dllName) {
    char* full = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    wsprintfA(full, "%s\\scripts\\mono\\lib\\mono\\4.5\\%s", baseDir, dllName);
    if (PathExists(full)) {
        void* a = pAsmOpen(domain, full);
        LogF("[Mono] Preload %s => %s", dllName, a ? "OK" : "FAIL");
    }
    HeapFree(GetProcessHeap(), 0, full);
}

void InitMonoBridge(const char* baseDir) {
    char* monoDllPath = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    char* libDir = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    char* etcDir = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    char* shimPath = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);

    wsprintfA(monoDllPath, "%s\\scripts\\mono\\mono-2.0-sgen.dll", baseDir);
    wsprintfA(libDir, "%s\\scripts\\mono\\lib", baseDir);
    wsprintfA(etcDir, "%s\\scripts\\mono\\etc", baseDir);
    wsprintfA(shimPath, "%s\\scripts\\ScriptHookVDotNet3.dll", baseDir);

    HMODULE hMono = LoadLibraryExA(monoDllPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!hMono) {
        LogF("[Mono] LoadLibrary FAILED, error %lu", GetLastError());
        return;
    }

    auto pSetDirs = (mono_set_dirs_t)GetProcAddress(hMono, "mono_set_dirs");
    auto pJitInit = (mono_jit_init_version_t)GetProcAddress(hMono, "mono_jit_init_version");
    auto pAsmOpen = (mono_domain_assembly_open_t)GetProcAddress(hMono, "mono_domain_assembly_open");
    auto pGetImage = (mono_assembly_get_image_t)GetProcAddress(hMono, "mono_assembly_get_image");
    auto pClassFromName = (mono_class_from_name_t)GetProcAddress(hMono, "mono_class_from_name");
    auto pGetMethod = (mono_class_get_method_from_name_t)GetProcAddress(hMono, "mono_class_get_method_from_name");
    pMonoInvoke = (mono_runtime_invoke_t)GetProcAddress(hMono, "mono_runtime_invoke");
    pMonoObjToStr = (mono_object_to_string_t)GetProcAddress(hMono, "mono_object_to_string");
    pMonoStrToUtf8 = (mono_string_to_utf8_t)GetProcAddress(hMono, "mono_string_to_utf8");
    pMonoFree = (mono_free_t)GetProcAddress(hMono, "mono_free");

    if (!pJitInit || !pAsmOpen || !pGetImage || !pClassFromName || !pGetMethod || !pMonoInvoke) return;

    if (pSetDirs) pSetDirs(libDir, etcDir);
    void* domain = pJitInit("MiniDotNetDomain", "v4.0.30319");
    if (!domain) return;

    PreloadCoreDll(domain, pAsmOpen, baseDir, "System.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "System.Core.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "System.Xml.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "System.Configuration.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "Mono.Security.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "System.Drawing.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "System.Windows.Forms.dll");
    PreloadCoreDll(domain, pAsmOpen, baseDir, "Microsoft.CSharp.dll");

    void* asmShim = pAsmOpen(domain, shimPath);
    if (!asmShim) {
        LogMessage("[Mono] failed to open ScriptHookVDotNet3.dll");
        return;
    }

    void* img = pGetImage(asmShim);
    void* klass = img ? pClassFromName(img, "GTA", "MonoBridgeHost") : nullptr;
    if (!klass) return;

    g_MonoTickMethod = pGetMethod(klass, "TickAll", 0);
    if (!g_MonoTickMethod) return;

    g_MonoReady = true;
    LogMessage("[SUCCESS] Mono Runtime & ScriptHookVDotNet3 Bridge Active!");
}

void LoadPluginDll(const wchar_t* dllPath, const wchar_t* fileName, bool isCore) {
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
        g_RegisterMod = (register_mod_fn)GetProcAddress(hMod, "RegisterMod");
        auto getFlags = (get_flags_fn)GetProcAddress(hMod, "GetModFlags");
        if (getFlags) g_Flags = getFlags();
        LogMessage("[SUCCESS] MiniDotNet Core Engine Active!");
    } else if (g_ModTicks && g_ModCount < 32) {
        int slot = (g_RegisterMod && fileName) ? g_RegisterMod(fileName) : -1;
        g_ModSlots[g_ModCount] = slot;
        g_ModTicks[g_ModCount++] = tickFn;
        LogMessage("[SUCCESS] Loaded External AOT Mod Plugin!");
    }
}

void LoadAllPlugins() {
    g_ModTicks = (tick_fn*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 32 * sizeof(tick_fn));
    g_ModSlots = (int*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 32 * sizeof(int));
    for (int i = 0; i < 32; i++) g_ModSlots[i] = -1;

    wchar_t* currentDir = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
    GetCurrentDirectoryW(MAX_PATH, currentDir);

    wchar_t* coreDll = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
    wsprintfW(coreDll, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);
    LoadPluginDll(coreDll, nullptr, true);

    wchar_t* searchPattern = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
    wsprintfW(searchPattern, L"%s\\scripts\\*.mod.dll", currentDir);

    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(searchPattern, &findData);
    if (hFind != INVALID_HANDLE_VALUE) {
        do {
            if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
                wchar_t* modPath = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH * sizeof(wchar_t));
                wsprintfW(modPath, L"%s\\scripts\\%s", currentDir, findData.cFileName);
                LoadPluginDll(modPath, findData.cFileName, false);
            }
        } while (FindNextFileW(hFind, &findData));
        FindClose(hFind);
    }
}

void ScriptMain() {
    LogMessage("[ASI] ScriptMain started");
    LoadAllPlugins();

    char* ansiDir = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    GetCurrentDirectoryA(MAX_PATH, ansiDir);

    while (true) {
        if (g_CoreTick) g_CoreTick();
        for (int i = 0; i < g_ModCount; i++) {
            bool on = (g_ModSlots[i] < 0) || !g_Flags || (g_Flags[g_ModSlots[i]] != 0);
            if (g_ModTicks[i] && on) g_ModTicks[i]();
        }

        if (!g_MonoReady && !g_MonoOff) {
            g_MonoWait++;
            if (g_MonoWait == 200) InitMonoBridge(ansiDir);
        } else if (g_MonoReady && g_MonoTickMethod && pMonoInvoke) {
            void* exc = nullptr;
            pMonoInvoke(g_MonoTickMethod, nullptr, nullptr, &exc);
            if (exc && !g_MonoExcLogged) {
                g_MonoExcLogged = true;
                LogMessage("[Mono Exception] Error inside TickAll!");
                LogMonoException(exc);
            }
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
