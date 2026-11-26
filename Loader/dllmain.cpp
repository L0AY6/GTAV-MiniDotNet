#include <windows.h>
#include <stdio.h>
#include "coreclr_delegates.h"
#include "hostfxr.h"

typedef void (*scriptRegister_t)(HMODULE, void(*)());
typedef void (*scriptUnregister_t)(HMODULE);
typedef void (*scriptWait_t)(DWORD);

scriptWait_t pScriptWait = nullptr;

typedef void (CORECLR_DELEGATE_CALLTYPE* tick_fn)();
volatile tick_fn g_ManagedTick = nullptr;
HMODULE g_HostFxrLib = nullptr;

void LogMessage(const char* msg) {
    HANDLE hFile = CreateFileA("MiniDotNet.log", FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(hFile, msg, (DWORD)lstrlenA(msg), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        CloseHandle(hFile);
    }
}

bool SearchFxrInBase(const wchar_t* baseDir, wchar_t* outPath) {
    wchar_t* searchPattern = new wchar_t[MAX_PATH];
    swprintf_s(searchPattern, MAX_PATH, L"%s\\*", baseDir);

    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(searchPattern, &findData);
    delete[] searchPattern;
    if (hFind == INVALID_HANDLE_VALUE) return false;

    bool found = false;
    do {
        if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && *(findData.cFileName) != L'.') {
            swprintf_s(outPath, MAX_PATH, L"%s\\%s\\hostfxr.dll", baseDir, findData.cFileName);
            if (GetFileAttributesW(outPath) != INVALID_FILE_ATTRIBUTES) {
                found = true;
            }
        }
    } while (FindNextFileW(hFind, &findData));

    FindClose(hFind);
    return found;
}

DWORD WINAPI DotNetInitThread(LPVOID lpParam) {
    SetEnvironmentVariableW(L"DOTNET_GCHeapHardLimit", L"0x8000000");
    SetEnvironmentVariableW(L"DOTNET_EnableWriteXorExecute", L"0");

    wchar_t* hostfxrPath = new wchar_t[MAX_PATH];
    if (!SearchFxrInBase(L"C:\\Program Files\\dotnet\\host\\fxr", hostfxrPath)) {
        delete[] hostfxrPath;
        LogMessage("[ERROR] hostfxr.dll not found in C:\\Program Files\\dotnet\\host\\fxr");
        return 0;
    }

    LogMessage("[BG Thread] Loading hostfxr.dll...");
    g_HostFxrLib = LoadLibraryW(hostfxrPath);
    delete[] hostfxrPath;

    if (!g_HostFxrLib) {
        char* buf = new char;
        sprintf_s(buf, 256, "[ERROR] LoadLibraryW failed. Error: %lu", GetLastError());
        LogMessage(buf);
        delete[] buf;
        return 0;
    }

    LogMessage("[BG Thread] hostfxr.dll loaded! Resolving functions...");
    auto init_fptr = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(g_HostFxrLib, "hostfxr_initialize_for_runtime_config");
    auto get_delegate_fptr = (hostfxr_get_runtime_delegate_fn)GetProcAddress(g_HostFxrLib, "hostfxr_get_runtime_delegate");
    auto close_fptr = (hostfxr_close_fn)GetProcAddress(g_HostFxrLib, "hostfxr_close");

    if (!init_fptr || !get_delegate_fptr || !close_fptr) {
        LogMessage("[ERROR] Failed to resolve hostfxr exports.");
        return 0;
    }

    wchar_t* currentDir = new wchar_t[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, currentDir);

    wchar_t* runtimeConfig = new wchar_t[MAX_PATH];
    wchar_t* coreDll = new wchar_t[MAX_PATH];
    swprintf_s(runtimeConfig, MAX_PATH, L"%s\\scripts\\MiniDotNet.Core.runtimeconfig.json", currentDir);
    swprintf_s(coreDll, MAX_PATH, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);
    delete[] currentDir;

    if (GetFileAttributesW(runtimeConfig) == INVALID_FILE_ATTRIBUTES || GetFileAttributesW(coreDll) == INVALID_FILE_ATTRIBUTES) {
        delete[] runtimeConfig;
        delete[] coreDll;
        LogMessage("[ERROR] Missing MiniDotNet.Core.dll or runtimeconfig.json.");
        return 0;
    }

    hostfxr_initialize_parameters params = {};
    params.size = sizeof(hostfxr_initialize_parameters);
    params.host_path = L"C:\\Program Files\\dotnet\\dotnet.exe";
    params.dotnet_root = L"C:\\Program Files\\dotnet";

    LogMessage("[BG Thread] Initializing .NET 8 CoreCLR...");
    hostfxr_handle cxt = nullptr;
    int rc = init_fptr(runtimeConfig, &params, &cxt);
    delete[] runtimeConfig;

    if (rc != 0 || cxt == nullptr) {
        delete[] coreDll;
        char* buf = new char;
        sprintf_s(buf, 256, "[ERROR] hostfxr_initialize failed with code: %d", rc);
        LogMessage(buf);
        delete[] buf;
        if (cxt) close_fptr(cxt);
        return 0;
    }

    LogMessage("[BG Thread] CoreCLR initialized! Binding C# ScriptEngine...");
    load_assembly_and_get_function_pointer_fn load_assembly = nullptr;
    rc = get_delegate_fptr(cxt, hdt_load_assembly_and_get_function_pointer, (void**)&load_assembly);
    close_fptr(cxt);

    if (rc != 0 || !load_assembly) {
        delete[] coreDll;
        LogMessage("[ERROR] Failed to get load_assembly delegate.");
        return 0;
    }

    tick_fn tempTick = nullptr;
    rc = load_assembly(
        coreDll,
        L"MiniDotNet.Core.ScriptEngine, MiniDotNet.Core",
        L"OnTick",
        UNMANAGEDCALLERSONLY_METHOD,
        nullptr,
        (void**)&tempTick
    );
    delete[] coreDll;

    if (rc != 0 || !tempTick) {
        char* buf = new char;
        sprintf_s(buf, 256, "[ERROR] Failed to bind C# OnTick. Code: %d", rc);
        LogMessage(buf);
        delete[] buf;
        return 0;
    }

    g_ManagedTick = tempTick;
    LogMessage("[SUCCESS] .NET 8 Runtime initialized and C# ScriptEngine bound successfully!");
    return 0;
}

void ScriptMain() {
    LogMessage("--- ScriptMain Started (Non-Blocking) ---");
    CreateThread(NULL, 0, DotNetInitThread, NULL, 0, NULL);

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
        if (!hSHV) {
            hSHV = LoadLibraryA("ScriptHookV.dll");
        }

        if (hSHV) {
            auto pRegister = (scriptRegister_t)GetProcAddress(hSHV, "?scriptRegister@@YAXPEAUHINSTANCE__@@P6AXXZ@Z");
            pScriptWait = (scriptWait_t)GetProcAddress(hSHV, "?scriptWait@@YAXK@Z");

            if (pRegister) {
                LogMessage("[INFO] Registering ScriptMain directly from DllMain...");
                pRegister(hModule, ScriptMain);
            } else {
                LogMessage("[ERROR] Could not resolve scriptRegister.");
            }
        } else {
            LogMessage("[ERROR] Could not load ScriptHookV.dll");
        }
    }
    return TRUE;
}
