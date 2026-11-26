#include <windows.h>
#include <stdio.h>
#include "coreclr_delegates.h"
#include "hostfxr.h"

typedef void (*scriptRegister_t)(HMODULE, void(*)());
typedef void (*scriptUnregister_t)(HMODULE);
typedef void (*scriptWait_t)(DWORD);

scriptWait_t pScriptWait = nullptr;

typedef void (CORECLR_DELEGATE_CALLTYPE* tick_fn)();
tick_fn g_ManagedTick = nullptr;
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

bool FindHostFxrPath(wchar_t* outPath) {
    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(L"C:\\Program Files\\dotnet\\host\\fxr\\*", &findData);
    if (hFind == INVALID_HANDLE_VALUE) return false;

    bool found = false;
    do {
        if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && findData.cFileName[0] != L'.') {
            swprintf_s(outPath, MAX_PATH, L"C:\\Program Files\\dotnet\\host\\fxr\\%s\\hostfxr.dll", findData.cFileName);
            if (GetFileAttributesW(outPath) != INVALID_FILE_ATTRIBUTES) {
                found = true;
            }
        }
    } while (FindNextFileW(hFind, &findData));

    FindClose(hFind);
    return found;
}

bool InitializeDotNet8() {
    wchar_t hostfxrPath[MAX_PATH] = { 0 };
    if (!FindHostFxrPath(hostfxrPath)) {
        LogMessage("[ERROR] hostfxr.dll not found in C:\\Program Files\\dotnet\\host\\fxr");
        return false;
    }

    LogMessage("[INFO] Found hostfxr.dll, loading...");
    g_HostFxrLib = LoadLibraryW(hostfxrPath);
    if (!g_HostFxrLib) {
        char buf[256];
        sprintf_s(buf, "[ERROR] LoadLibraryW failed for hostfxr.dll. Error: %lu", GetLastError());
        LogMessage(buf);
        return false;
    }

    auto init_fptr = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(g_HostFxrLib, "hostfxr_initialize_for_runtime_config");
    auto get_delegate_fptr = (hostfxr_get_runtime_delegate_fn)GetProcAddress(g_HostFxrLib, "hostfxr_get_runtime_delegate");
    auto close_fptr = (hostfxr_close_fn)GetProcAddress(g_HostFxrLib, "hostfxr_close");

    if (!init_fptr || !get_delegate_fptr || !close_fptr) {
        LogMessage("[ERROR] Failed to resolve hostfxr exports.");
        return false;
    }

    wchar_t currentDir[MAX_PATH] = { 0 };
    GetCurrentDirectoryW(MAX_PATH, currentDir);

    wchar_t runtimeConfig[MAX_PATH] = { 0 };
    wchar_t coreDll[MAX_PATH] = { 0 };
    swprintf_s(runtimeConfig, MAX_PATH, L"%s\\scripts\\MiniDotNet.Core.runtimeconfig.json", currentDir);
    swprintf_s(coreDll, MAX_PATH, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);

    if (GetFileAttributesW(runtimeConfig) == INVALID_FILE_ATTRIBUTES || GetFileAttributesW(coreDll) == INVALID_FILE_ATTRIBUTES) {
        LogMessage("[ERROR] Missing MiniDotNet.Core.dll or runtimeconfig.json in scripts folder.");
        return false;
    }

    hostfxr_handle cxt = nullptr;
    int rc = init_fptr(runtimeConfig, nullptr, &cxt);
    if (rc != 0 || cxt == nullptr) {
        char buf[256];
        sprintf_s(buf, "[ERROR] hostfxr_initialize failed with code: %d", rc);
        LogMessage(buf);
        if (cxt) close_fptr(cxt);
        return false;
    }

    load_assembly_and_get_function_pointer_fn load_assembly = nullptr;
    rc = get_delegate_fptr(cxt, hdt_load_assembly_and_get_function_pointer, (void**)&load_assembly);
    close_fptr(cxt);

    if (rc != 0 || !load_assembly) {
        LogMessage("[ERROR] Failed to get load_assembly delegate.");
        return false;
    }

    rc = load_assembly(
        coreDll,
        L"MiniDotNet.Core.ScriptEngine, MiniDotNet.Core",
        L"OnTick",
        UNMANAGEDCALLERSONLY_METHOD,
        nullptr,
        (void**)&g_ManagedTick
    );

    if (rc != 0 || !g_ManagedTick) {
        char buf[256];
        sprintf_s(buf, "[ERROR] Failed to bind C# OnTick. Code: %d", rc);
        LogMessage(buf);
        return false;
    }

    LogMessage("[SUCCESS] .NET 8 Runtime initialized and C# ScriptEngine bound successfully!");
    return true;
}

void ScriptMain() {
    LogMessage("--- ScriptMain Started ---");
    if (!InitializeDotNet8()) return;

    while (true) {
        if (g_ManagedTick) {
            g_ManagedTick();
        }
        if (pScriptWait) pScriptWait(0);
        else Sleep(16);
    }
}

DWORD WINAPI InitThread(LPVOID lpParam) {
    HMODULE hMod = (HMODULE)lpParam;
    LogMessage("--- MiniDotNet DLL Loaded Successfully! Waiting for ScriptHookV... ---");

    HMODULE hSHV = NULL;
    for (int i = 0; i < 50; i++) {
        hSHV = GetModuleHandleA("ScriptHookV.dll");
        if (hSHV) break;
        Sleep(200);
    }

    if (!hSHV) {
        LogMessage("[ERROR] ScriptHookV.dll not found in memory.");
        return 0;
    }

    auto pRegister = (scriptRegister_t)GetProcAddress(hSHV, "?scriptRegister@@YAXPEAUHINSTANCE__@@P6AXXZ@Z");
    pScriptWait = (scriptWait_t)GetProcAddress(hSHV, "?scriptWait@@YAXK@Z");

    if (pRegister) {
        LogMessage("[INFO] Registering script with ScriptHookV...");
        pRegister(hMod, ScriptMain);
    } else {
        LogMessage("[ERROR] Could not find scriptRegister export in ScriptHookV.dll");
    }
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        CreateThread(NULL, 0, InitThread, hModule, 0, NULL);
    }
    return TRUE;
}
