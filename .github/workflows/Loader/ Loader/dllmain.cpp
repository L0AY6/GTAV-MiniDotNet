#include <windows.h>
#include <string>
#include <filesystem>
#include <fstream>
#include "main.h"
#include "nethost.h"
#include "coreclr_delegates.h"
#include "hostfxr.h"

namespace fs = std::filesystem;

typedef void (CORECLR_DELEGATE_CALLTYPE* tick_fn)();
tick_fn g_ManagedTick = nullptr;
HMODULE g_HostFxrLib = nullptr;

void LogMessage(const std::string& msg) {
    std::ofstream log("MiniDotNet.log", std::ios::app);
    if (log.is_open()) {
        log << msg << std::endl;
    }
}

bool InitializeDotNet8() {
    char_t buffer[MAX_PATH];
    size_t buffer_size = sizeof(buffer) / sizeof(char_t);
    
    int rc = get_hostfxr_path(buffer, &buffer_size, nullptr);
    if (rc != 0) {
        LogMessage("[ERROR] Could not find .NET 8 hostfxr.dll. Ensure .NET 8 Desktop Runtime x64 is installed.");
        return false;
    }

    g_HostFxrLib = LoadLibraryW(buffer);
    if (!g_HostFxrLib) {
        LogMessage("[ERROR] Failed to load hostfxr.dll into memory.");
        return false;
    }

    auto init_fptr = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(g_HostFxrLib, "hostfxr_initialize_for_runtime_config");
    auto get_delegate_fptr = (hostfxr_get_runtime_delegate_fn)GetProcAddress(g_HostFxrLib, "hostfxr_get_runtime_delegate");
    auto close_fptr = (hostfxr_close_fn)GetProcAddress(g_HostFxrLib, "hostfxr_close");

    if (!init_fptr || !get_delegate_fptr || !close_fptr) {
        LogMessage("[ERROR] Failed to resolve hostfxr exports.");
        return false;
    }

    fs::path runtimeConfig = fs::current_path() / "scripts" / "MiniDotNet.Core.runtimeconfig.json";
    fs::path coreDll = fs::current_path() / "scripts" / "MiniDotNet.Core.dll";

    if (!fs::exists(runtimeConfig) || !fs::exists(coreDll)) {
        LogMessage("[ERROR] Missing MiniDotNet.Core.dll or runtimeconfig.json in scripts folder.");
        return false;
    }

    hostfxr_handle cxt = nullptr;
    rc = init_fptr(runtimeConfig.c_str(), nullptr, &cxt);
    if (rc != 0 || cxt == nullptr) {
        LogMessage("[ERROR] hostfxr_initialize_for_runtime_config failed with code: " + std::to_string(rc));
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
        coreDll.c_str(),
        L"MiniDotNet.Core.ScriptEngine, MiniDotNet.Core",
        L"OnTick",
        UNMANAGEDCALLERSONLY_METHOD,
        nullptr,
        (void**)&g_ManagedTick
    );

    if (rc != 0 || !g_ManagedTick) {
        LogMessage("[ERROR] Failed to bind C# OnTick method. Code: " + std::to_string(rc));
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
