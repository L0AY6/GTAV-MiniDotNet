#include <windows.h>
#include "main.h"
#include "coreclr_delegates.h"
#include "hostfxr.h"

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

bool FindHostFxrPath(wchar_t* outPath, size_t maxLen) {
    WIN32_FIND_DATAW findData;
    HANDLE hFind = FindFirstFileW(L"C:\\Program Files\\dotnet\\host\\fxr\\*", &findData);
    if (hFind == INVALID_HANDLE_VALUE) return false;

    bool found = false;
    do {
        if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && findData.cFileName[0] != L'.') {
            wsprintfW(outPath, L"C:\\Program Files\\dotnet\\host\\fxr\\%s\\hostfxr.dll", findData.cFileName);
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
    if (!FindHostFxrPath(hostfxrPath, MAX_PATH)) {
        LogMessage("[ERROR] hostfxr.dll not found in C:\\Program Files\\dotnet\\host\\fxr");
        return false;
    }

    LogMessage("[INFO] Found hostfxr.dll, attempting to load...");
    g_HostFxrLib = LoadLibraryW(hostfxrPath);
    if (!g_HostFxrLib) {
        char buf[128];
        wsprintfA(buf, "[ERROR] LoadLibraryW failed for hostfxr.dll. Error code: %lu", GetLastError());
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
    wsprintfW(runtimeConfig, L"%s\\scripts\\MiniDotNet.Core.runtimeconfig.json", currentDir);
    wsprintfW(coreDll, L"%s\\scripts\\MiniDotNet.Core.dll", currentDir);

    if (GetFileAttributesW(runtimeConfig) == INVALID_FILE_ATTRIBUTES || GetFileAttributesW(coreDll) == INVALID_FILE_ATTRIBUTES) {
        LogMessage("[ERROR] Missing MiniDotNet.Core.dll or runtimeconfig.json in scripts folder.");
        return false;
    }

    hostfxr_handle cxt = nullptr;
    int rc = init_fptr(runtimeConfig, nullptr, &cxt);
    if (rc != 0 || cxt == nullptr) {
        char buf[128];
        wsprintfA(buf, "[ERROR] hostfxr_initialize_for_runtime_config failed with code: %d", rc);
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
        char buf[128];
        wsprintfA(buf, "[ERROR] Failed to bind C# OnTick method. Code: %d", rc);
        LogMessage(buf);
        return false;
    }

    LogMessage("[SUCCESS] .NET 8 Runtime initialized and C# ScriptEngine bound successfully!");
    return true;
}

void ScriptMain() {
    LogMessage("--- MiniDotNet Loader Started ---");
    if (!InitializeDotNet8()) {
        return;
    }

    while (true) {
        if (g_ManagedTick) {
            g_ManagedTick();
        }
        WAIT(0);
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        scriptRegister(hModule, ScriptMain);
    } else if (ul_reason_for_call == DLL_PROCESS_DETACH) {
        scriptUnregister(hModule);
    }
    return TRUE;
}
