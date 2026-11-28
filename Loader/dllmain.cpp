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

    // Preload core .NET 4.5 assemblies directly from mono\lib\mono\4.5 so Mono never fails to resolve them
    const char* coreLibs[] = {
        "System.dll", "System.Core.dll", "System.Xml.dll", "System.Configuration.dll",
        "Mono.Security.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "Microsoft.CSharp.dll"
    };
    for (int i = 0; i < 8; i++) {
        char dllFull[MAX_PATH];
        wsprintfA(dllFull, "%s\\scripts\\mono\\lib\\mono\\4.5\\%s", baseDir, coreLibs[i]);
        if (PathExists(dllFull)) {
            void* a = pAsmOpen(domain, dllFull);
            LogF("[Mono] Preload %s => %s", coreLibs[i], a ? "OK" : "FAIL");
        }
    }

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
