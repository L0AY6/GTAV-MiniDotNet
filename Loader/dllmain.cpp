#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <stdarg.h>
#include <string.h>

// ======================= ScriptHookV function types =======================
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

// ======================= plugin (Core / *.mod.dll) =======================
typedef void (__cdecl* init_native_fn)(void* init, void* push, void* call, void* getAddr, void* getAllVeh);
typedef void (__cdecl* tick_fn)();
typedef int (__cdecl* register_mod_fn)(const wchar_t* fileName);
typedef uint8_t* (__cdecl* get_flags_fn)();

tick_fn g_CoreTick = nullptr;
tick_fn* g_ModTicks = nullptr;
int g_ModSlots[32];
int g_ModCount = 0;
register_mod_fn g_RegisterMod = nullptr;
uint8_t* g_Flags = nullptr;

// ======================= Mono embedding API =======================
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
int g_MonoTicks = 0;
int g_Iter = 0;

// ======================= logging =======================
// Every line is written with open/append/close, so it survives a hard crash.
void LogMessage(const char* msg) {
    HANDLE hFile = CreateFileA("MiniDotNet.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        SYSTEMTIME st;
        GetLocalTime(&st);
        char prefix[48];
        wsprintfA(prefix, "[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        DWORD written = 0;
        WriteFile(hFile, prefix, (DWORD)lstrlenA(prefix), &written, NULL);
        WriteFile(hFile, msg, (DWORD)lstrlenA(msg), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        CloseHandle(hFile);
    }
}

void LogF(const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = _vsnprintf(buf, sizeof(buf) - 1, fmt, ap);
    va_end(ap);
    if (n < 0 || n >= (int)sizeof(buf) - 1) n = (int)sizeof(buf) - 1;
    buf[n] = 0;
    LogMessage(buf);
}

void LogWide(const char* prefix, const wchar_t* name) {
    char nameA[300];
    nameA[0] = 0;
    WideCharToMultiByte(CP_ACP, 0, name, -1, nameA, sizeof(nameA) - 1, NULL, NULL);
    nameA[sizeof(nameA) - 1] = 0;
    LogF("%s %s", prefix, nameA);
}

bool PathExists(const char* p) {
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

// ======================= crash diagnostics =======================
// Logs the first fatal-type exceptions (module + offset + stack range) so we can see where it dies.
volatile LONG g_VehCount = 0;

LONG CALLBACK CrashVeh(PEXCEPTION_POINTERS ep) {
    DWORD code = ep->ExceptionRecord->ExceptionCode;
    switch (code) {
        case EXCEPTION_ACCESS_VIOLATION:
        case EXCEPTION_IN_PAGE_ERROR:
        case EXCEPTION_STACK_OVERFLOW:
        case EXCEPTION_ILLEGAL_INSTRUCTION:
        case EXCEPTION_PRIV_INSTRUCTION:
        case EXCEPTION_INT_DIVIDE_BY_ZERO:
        case EXCEPTION_DATATYPE_MISALIGNMENT:
        case 0xC0000374:
        case 0xC0000409:
            break;
        default:
            return EXCEPTION_CONTINUE_SEARCH;
    }

    LONG n = InterlockedIncrement(&g_VehCount);
    if (n > 150) return EXCEPTION_CONTINUE_SEARCH;

    PVOID addr = ep->ExceptionRecord->ExceptionAddress;
    char mod[MAX_PATH];
    mod[0] = 0;
    unsigned long long off = 0;
    HMODULE hm = NULL;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)addr, &hm) && hm) {
        GetModuleFileNameA(hm, mod, MAX_PATH);
        off = (unsigned long long)((ULONG_PTR)addr - (ULONG_PTR)hm);
    }
    const char* name = mod[0] ? strrchr(mod, '\\') : NULL;
    name = name ? name + 1 : (mod[0] ? mod : "(no module: JIT or unknown code)");

    unsigned long long info0 = 0, info1 = 0;
    if (ep->ExceptionRecord->NumberParameters >= 2) {
        info0 = (unsigned long long)ep->ExceptionRecord->ExceptionInformation[0];
        info1 = (unsigned long long)ep->ExceptionRecord->ExceptionInformation[1];
    }

    NT_TIB* tib = (NT_TIB*)NtCurrentTeb();
    LogF("[VEH #%ld] code=%08lX at %p in %s+0x%llX | info0=%llu info1=0x%llX | rsp=%p stack=[%p..%p] tid=%lu",
         (long)n, (unsigned long)code, addr, name, off, info0, info1,
         (void*)ep->ContextRecord->Rsp, tib->StackLimit, tib->StackBase, (unsigned long)GetCurrentThreadId());
    return EXCEPTION_CONTINUE_SEARCH;
}

// ======================= bridge exports (called from the Mono shim) =======================
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

// ======================= Mono bridge =======================
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

void InitMonoBridge(const char* baseDir) {
    char monoDllPath[MAX_PATH], libDir[MAX_PATH], etcDir[MAX_PATH], shimPath[MAX_PATH], mscorlibPath[MAX_PATH], cfgPath[MAX_PATH];
    wsprintfA(monoDllPath, "%s\\scripts\\mono\\mono-2.0-sgen.dll", baseDir);
    wsprintfA(libDir, "%s\\scripts\\mono\\lib", baseDir);
    wsprintfA(etcDir, "%s\\scripts\\mono\\etc", baseDir);
    wsprintfA(shimPath, "%s\\scripts\\ScriptHookVDotNet3.dll", baseDir);
    wsprintfA(mscorlibPath, "%s\\scripts\\mono\\lib\\mono\\4.5\\mscorlib.dll", baseDir);
    wsprintfA(cfgPath, "%s\\scripts\\mono\\etc\\mono\\4.5\\machine.config", baseDir);

    LogF("[Mono] step 1: files  mono-dll=%d  mscorlib=%d  machine.config=%d  shim=%d",
         PathExists(monoDllPath), PathExists(mscorlibPath), PathExists(cfgPath), PathExists(shimPath));

    HMODULE hVc = LoadLibraryA("VCRUNTIME140.dll");
    LogF("[Mono] step 2: VCRUNTIME140.dll %s (err %lu)", hVc ? "OK" : "MISSING", hVc ? 0UL : GetLastError());

    LogMessage("[Mono] step 3: LoadLibrary mono-2.0-sgen.dll ...");
    HMODULE hMono = LoadLibraryExA(monoDllPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!hMono) {
        LogF("[Mono] LoadLibrary FAILED, error %lu", GetLastError());
        return;
    }
    LogMessage("[Mono] step 3 done");

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

    LogF("[Mono] step 4: exports  set_dirs=%d jit_init=%d asm_open=%d get_image=%d class_from_name=%d get_method=%d invoke=%d",
         pSetDirs != nullptr, pJitInit != nullptr, pAsmOpen != nullptr, pGetImage != nullptr,
         pClassFromName != nullptr, pGetMethod != nullptr, pMonoInvoke != nullptr);

    if (!pJitInit || !pAsmOpen || !pGetImage || !pClassFromName || !pGetMethod || !pMonoInvoke) {
        LogMessage("[Mono] required exports missing, giving up.");
        return;
    }

    if (pSetDirs) {
        LogF("[Mono] step 5: mono_set_dirs(%s, %s)", libDir, etcDir);
        pSetDirs(libDir, etcDir);
        LogMessage("[Mono] step 5 done");
    }

    LogMessage("[Mono] step 6: mono_jit_init_version ... (if the log ends here, the crash is inside Mono's JIT init)");
    void* domain = pJitInit("MiniDotNetDomain", "v4.0.30319");
    if (!domain) {
        LogMessage("[Mono] mono_jit_init_version returned NULL.");
        return;
    }
    LogMessage("[Mono] step 6 done: domain created");

    LogMessage("[Mono] step 7: opening ScriptHookVDotNet3.dll ...");
    void* asmShim = pAsmOpen(domain, shimPath);
    if (!asmShim) {
        LogMessage("[Mono] failed to open ScriptHookVDotNet3.dll");
        return;
    }
    LogMessage("[Mono] step 7 done");

    void* img = pGetImage(asmShim);
    void* klass = img ? pClassFromName(img, "GTA", "MonoBridgeHost") : nullptr;
    if (!klass) {
        LogMessage("[Mono] class GTA.MonoBridgeHost not found in shim.");
        return;
    }

    g_MonoTickMethod = pGetMethod(klass, "TickAll", 0);
    if (!g_MonoTickMethod) {
        LogMessage("[Mono] method TickAll not found in shim.");
        return;
    }
    g_MonoReady = true;
    LogMessage("[SUCCESS] Mono Runtime & ScriptHookVDotNet3 Bridge Active!");
}

// ======================= plugins =======================
void LoadPluginDll(const wchar_t* dllPath, const wchar_t* fileName, bool isCore) {
    HMODULE hMod = LoadLibraryW(dllPath);
    if (!hMod) {
        LogWide("[Plugin] LoadLibrary failed:", dllPath);
        return;
    }

    auto initNatives = (init_native_fn)GetProcAddress(hMod, "SetupNatives");
    auto tickFn = (tick_fn)GetProcAddress(hMod, "OnTick");
    if (!tickFn) {
        LogWide("[Plugin] No OnTick export:", dllPath);
        return;
    }

    if (initNatives) {
        initNatives((void*)pNativeInit, (void*)pNativePush64, (void*)pNativeCall, (void*)pGetEntityAddr, (void*)pWorldGetAllVeh);
    }

    if (isCore) {
        g_CoreTick = tickFn;
        g_RegisterMod = (register_mod_fn)GetProcAddress(hMod, "RegisterMod");
        auto getFlags = (get_flags_fn)GetProcAddress(hMod, "GetModFlags");
        if (getFlags) g_Flags = getFlags();
        LogF("[SUCCESS] MiniDotNet Core Engine Active! (RegisterMod=%d GetModFlags=%d)", g_RegisterMod != nullptr, getFlags != nullptr);
    } else if (g_ModTicks && g_ModCount < 32) {
        int slot = (g_RegisterMod && fileName) ? g_RegisterMod(fileName) : -1;
        g_ModSlots[g_ModCount] = slot;
        g_ModTicks[g_ModCount++] = tickFn;
        LogWide("[SUCCESS] Loaded External AOT Mod Plugin:", fileName ? fileName : L"?");
    }
}

void LoadAllPlugins() {
    g_ModTicks = (tick_fn*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 32 * sizeof(tick_fn));
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

// ======================= main loop (runs on the ScriptHookV fiber) =======================
void ScriptMain() {
    AddVectoredExceptionHandler(1, CrashVeh);
    LogMessage("[ASI] ScriptMain started");
    LoadAllPlugins();

    char* ansiDir = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAX_PATH);
    GetCurrentDirectoryA(MAX_PATH, ansiDir);

    while (true) {
        g_Iter++;
        if (g_Iter == 1 || g_Iter == 100 || g_Iter == 199 || g_Iter == 300) LogF("[Loop] iteration %d", g_Iter);

        if (g_CoreTick) g_CoreTick();
        for (int i = 0; i < g_ModCount; i++) {
            bool on = (g_ModSlots[i] < 0) || !g_Flags || (g_Flags[g_ModSlots[i]] != 0);
            if (g_ModTicks[i] && on) g_ModTicks[i]();
        }

        if (!g_MonoReady && !g_MonoOff) {
            g_MonoWait++;
            if (g_MonoWait == 200) {
                char flag[MAX_PATH];
                wsprintfA(flag, "%s\\scripts\\MiniDotNet.nomono", ansiDir);
                if (PathExists(flag)) {
                    g_MonoOff = true;
                    LogMessage("[Mono] DISABLED: scripts\\MiniDotNet.nomono exists. Delete that file to enable the Mono bridge.");
                } else {
                    InitMonoBridge(ansiDir);
                }
            }
        } else if (g_MonoReady && g_MonoTickMethod && pMonoInvoke) {
            g_MonoTicks++;
            if (g_MonoTicks <= 3) LogF("[Mono] calling TickAll (#%d) ...", g_MonoTicks);
            void* exc = nullptr;
            pMonoInvoke(g_MonoTickMethod, nullptr, nullptr, &exc);
            if (g_MonoTicks <= 3) LogF("[Mono] TickAll #%d returned", g_MonoTicks);
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

            LogF("[ASI] attached. SHV exports: register=%d wait=%d init=%d push=%d call=%d entityAddr=%d allVeh=%d",
                 pRegister != nullptr, pScriptWait != nullptr, pNativeInit != nullptr, pNativePush64 != nullptr,
                 pNativeCall != nullptr, pGetEntityAddr != nullptr, pWorldGetAllVeh != nullptr);

            if (pRegister) pRegister(hModule, ScriptMain);
        } else {
            LogMessage("[ASI] ScriptHookV.dll not found.");
        }
    }
    return TRUE;
}
