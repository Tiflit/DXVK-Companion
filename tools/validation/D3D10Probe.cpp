// Original bounded validation probe for issue #250; not part of Companion.
// No graphics DLL is statically imported. Runtime calls require --run and
// explicit route/adapter/identity arguments; --self-test is CPU-only.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#include <d3d10_1.h>
#include <d3d11.h>
#include <dxgi.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <functional>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

static_assert(sizeof(void*) == 8, "This initial probe supports x64 only.");

namespace {
enum class Mode { Help, SelfTest, Runtime, Invalid };
enum class Route { D3D10, D3D101, D3D11 };
struct Options {
    Mode mode = Mode::Invalid;
    Route route = Route::D3D10;
    UINT adapter = 0;
    UINT vendor = 0;
    UINT device = 0;
};

constexpr std::array<const wchar_t*, 6> GraphicsNames = {
    L"d3d10.dll", L"d3d10_1.dll", L"d3d10core.dll",
    L"d3d11.dll", L"dxgi.dll", L"vulkan-1.dll"
};

bool ParseNumber(std::wstring_view text, UINT maximum, UINT& result) {
    if (text.empty()) return false;
    UINT base = 10;
    if (text.size() > 2 && text[0] == L'0' && text[1] == L'x') {
        base = 16;
        text.remove_prefix(2);
    }
    if (text.empty()) return false;
    UINT value = 0;
    for (wchar_t c : text) {
        UINT digit = 16;
        if (c >= L'0' && c <= L'9') digit = static_cast<UINT>(c - L'0');
        else if (c >= L'a' && c <= L'f') digit = static_cast<UINT>(c - L'a') + 10;
        else if (c >= L'A' && c <= L'F') digit = static_cast<UINT>(c - L'A') + 10;
        if (digit >= base || digit > maximum || value > (maximum - digit) / base)
            return false;
        value = value * base + digit;
    }
    result = value;
    return true;
}

Options Parse(const std::vector<std::wstring>& args) {
    Options o;
    if (args.empty() || args == std::vector<std::wstring>{L"--help"}) {
        o.mode = Mode::Help;
        return o;
    }
    if (args == std::vector<std::wstring>{L"--self-test"}) {
        o.mode = Mode::SelfTest;
        return o;
    }
    // Exactly four distinct options, in any order, following the explicit gate.
    if (args.size() != 9 || args[0] != L"--run") return o;
    unsigned seen = 0;
    for (size_t i = 1; i < args.size(); i += 2) {
        const auto& key = args[i];
        const auto& value = args[i + 1];
        unsigned bit = 0;
        if (key == L"--route") {
            bit = 1;
            if (value == L"d3d10") o.route = Route::D3D10;
            else if (value == L"d3d10.1") o.route = Route::D3D101;
            else if (value == L"d3d11") o.route = Route::D3D11;
            else return Options{};
        } else if (key == L"--adapter") {
            bit = 2;
            if (!ParseNumber(value, 31, o.adapter)) return Options{};
        } else if (key == L"--vendor") {
            bit = 4;
            if (!ParseNumber(value, 0xffff, o.vendor)) return Options{};
        } else if (key == L"--device") {
            bit = 8;
            if (!ParseNumber(value, 0xffff, o.device)) return Options{};
        } else return Options{};
        if ((seen & bit) != 0) return Options{};
        seen |= bit;
    }
    if (seen == 15) o.mode = Mode::Runtime;
    return o;
}

const char* RouteName(Route route) {
    switch (route) {
    case Route::D3D10: return "d3d10";
    case Route::D3D101: return "d3d10.1";
    case Route::D3D11: return "d3d11";
    }
    return "invalid";
}

std::string Hex(UINT value) {
    std::ostringstream s;
    s << "0x" << std::hex << std::setfill('0') << std::setw(8) << value;
    return s.str();
}

bool DeviceCreated(HRESULT code, bool nonnull) {
    return SUCCEEDED(code) && nonnull;
}

int DeviceExitCode(HRESULT code, bool nonnull, bool evidence) {
    return DeviceCreated(code, nonnull) && evidence ? 0 : 3;
}

std::string Escape(std::string_view text) {
    std::ostringstream s;
    s << '"';
    for (unsigned char c : text) {
        if (c == '"' || c == '\\') s << '\\' << static_cast<char>(c);
        else if (c < 0x20)
            s << "\\u00" << std::hex << std::setfill('0') << std::setw(2)
              << static_cast<unsigned>(c);
        else s << static_cast<char>(c);
    }
    s << '"';
    return s.str();
}

class Record {
    std::ostringstream stream;
public:
    explicit Record(const char* event) { stream << "{\"event\":" << Escape(event); }
    Record& String(const char* key, std::string_view value) {
        stream << ',' << Escape(key) << ':' << Escape(value);
        return *this;
    }
    Record& Number(const char* key, UINT value) {
        stream << ',' << Escape(key) << ':' << value;
        return *this;
    }
    Record& Boolean(const char* key, bool value) {
        stream << ',' << Escape(key) << ':' << (value ? "true" : "false");
        return *this;
    }
    std::string Text() const { return stream.str() + "}"; }
    void Emit() const { std::cout << Text() << '\n' << std::flush; }
};

bool Utf8(const std::wstring& value, std::string& output) {
    if (value.empty()) { output.clear(); return true; }
    // All callers supply bounded Windows buffers or fixed names.
    const int length = static_cast<int>(value.size());
    const int required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
        value.data(), length, nullptr, 0, nullptr, nullptr);
    if (required == 0) return false;
    output.resize(static_cast<size_t>(required));
    return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        length, output.data(), required, nullptr, nullptr) == required;
}

template<class T> class Com {
    T* pointer = nullptr;
public:
    ~Com() { if (pointer) pointer->Release(); }
    T* Get() const { return pointer; }
    T** Out() { return &pointer; }
    Com() = default;
    Com(const Com&) = delete;
    Com& operator=(const Com&) = delete;
};

class Library {
    HMODULE module;
public:
    explicit Library(const wchar_t* name) : module(LoadLibraryW(name)) {}
    ~Library() { if (module) FreeLibrary(module); }
    HMODULE Get() const { return module; }
    Library(const Library&) = delete;
    Library& operator=(const Library&) = delete;
};

template<class Function> Function Resolve(HMODULE module, const char* name) {
    const FARPROC address = GetProcAddress(module, name);
    Function result = nullptr;
    // Windows function pointers have the same representation; use the SDK's
    // exact declaration without C4191 or a hand-maintained ABI signature.
    static_assert(sizeof(result) == sizeof(address));
    std::memcpy(&result, &address, sizeof(result));
    return result;
}

std::wstring StripExtendedPrefix(std::wstring path) {
    if (path.rfind(L"\\\\?\\UNC\\", 0) == 0) return L"\\\\" + path.substr(8);
    if (path.rfind(L"\\\\?\\", 0) == 0) return path.substr(4);
    return path;
}

bool CanonicalFile(const std::wstring& path, std::wstring& canonical) {
    HANDLE file = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetFinalPathNameByHandleW(file, buffer.data(),
        static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    CloseHandle(file);
    if (length == 0 || length >= buffer.size()) return false;
    canonical = StripExtendedPrefix(std::wstring(buffer.data(), length));
    return true;
}

bool ModulePath(HMODULE module, std::wstring& canonical) {
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetModuleFileNameW(module, buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return false;
    return CanonicalFile(std::wstring(buffer.data(), length), canonical);
}

bool InDirectory(const std::wstring& path, const std::wstring& directory) {
    return path.size() > directory.size() &&
        _wcsnicmp(path.c_str(), directory.c_str(), directory.size()) == 0 &&
        path[directory.size()] == L'\\';
}

struct Locations { std::wstring fixture; std::wstring system; };
bool GetLocations(Locations& locations) {
    std::wstring exe;
    if (!ModulePath(nullptr, exe)) return false;
    const size_t slash = exe.find_last_of(L'\\');
    if (slash == std::wstring::npos) return false;
    locations.fixture = exe.substr(0, slash);
    std::array<wchar_t, 32768> buffer{};
    const UINT size = GetSystemDirectoryW(buffer.data(), static_cast<UINT>(buffer.size()));
    if (size == 0 || size >= buffer.size()) return false;
    // Resolve the system directory via a known existing file without writing.
    std::wstring systemKernel;
    if (!CanonicalFile(std::wstring(buffer.data(), size) + L"\\kernel32.dll", systemKernel))
        return false;
    locations.system = systemKernel.substr(0, systemKernel.find_last_of(L'\\'));
    return true;
}

struct Snapshot { bool complete = true; bool duplicate = false; UINT count = 0; };
Snapshot GraphicsSnapshot(const char* phase, const Locations& locations) {
    Snapshot result;
    std::array<HMODULE, 1024> modules{};
    DWORD needed = 0;
    std::array<UINT, GraphicsNames.size()> counts{};
    if (!K32EnumProcessModules(GetCurrentProcess(), modules.data(),
        static_cast<DWORD>(sizeof(modules)), &needed) ||
        needed > sizeof(modules) || needed % sizeof(HMODULE) != 0) {
        Record("module_error").String("phase", phase).String("stage", "enumeration").Emit();
        result.complete = false;
    } else {
        const size_t count = needed / sizeof(HMODULE);
        for (size_t i = 0; i < count; ++i) {
            std::array<wchar_t, 260> base{};
            const DWORD length = K32GetModuleBaseNameW(GetCurrentProcess(), modules[i],
                base.data(), static_cast<DWORD>(base.size()));
            if (length == 0 || length >= base.size()) {
                result.complete = false;
                continue;
            }
            for (size_t j = 0; j < GraphicsNames.size(); ++j) {
                if (_wcsicmp(base.data(), GraphicsNames[j]) != 0) continue;
                ++result.count;
                if (++counts[j] > 1) result.duplicate = true;
                std::wstring path;
                std::string encodedPath, encodedName;
                if (!ModulePath(modules[i], path) || !Utf8(path, encodedPath) ||
                    !Utf8(GraphicsNames[j], encodedName)) {
                    result.complete = false;
                    Record("module_error").String("phase", phase)
                        .String("stage", "canonical_path").Emit();
                    continue;
                }
                const char* origin = InDirectory(path, locations.fixture) ? "fixture" :
                    InDirectory(path, locations.system) ? "system" : "other";
                Record("module").String("phase", phase).String("name", encodedName)
                    .String("origin", origin).String("path", encodedPath).Emit();
            }
        }
    }
    Record("graphics_modules").String("phase", phase)
        .Number("count", result.count).Boolean("complete", result.complete)
        .Boolean("duplicate_basename", result.duplicate).Emit();
    return result;
}

int Failure(const char* stage, HRESULT hr, const Locations* locations = nullptr) {
    Record("failure").String("stage", stage).String("hresult", Hex(static_cast<UINT>(hr))).Emit();
    if (locations) GraphicsSnapshot("failure", *locations);
    return 3;
}

int Runtime(const Options& o) {
    Record("probe").Number("schema", 1).String("architecture", "x64")
        .String("route", RouteName(o.route)).Number("adapter", o.adapter)
        .String("expected_vendor", Hex(o.vendor)).String("expected_device", Hex(o.device)).Emit();
    Locations locations;
    if (!GetLocations(locations)) return Failure("reference_paths", HRESULT_FROM_WIN32(GetLastError()));
    const Snapshot before = GraphicsSnapshot("before", locations);
    if (!before.complete || before.count != 0)
        return Failure("preloaded_or_unobserved_graphics", E_UNEXPECTED);

    const wchar_t* libraryName = o.route == Route::D3D10 ? L"d3d10.dll" :
        o.route == Route::D3D101 ? L"d3d10_1.dll" : L"d3d11.dll";
    Library runtime(libraryName); // Normal name-based loading, no search overrides.
    if (!runtime.Get()) return Failure("runtime_load", HRESULT_FROM_WIN32(GetLastError()), &locations);
    // Any DXGI already imported by the runtime takes part in normal resolution.
    // Never force System32 or a candidate core into the route.
    Library dxgi(L"dxgi.dll");
    if (!dxgi.Get()) return Failure("dxgi_load", HRESULT_FROM_WIN32(GetLastError()), &locations);
    const Snapshot loaded = GraphicsSnapshot("loaded", locations);
    if (!loaded.complete || loaded.duplicate)
        return Failure("ambiguous_or_unobserved_modules", E_UNEXPECTED);
    const auto createFactory = Resolve<decltype(&CreateDXGIFactory)>(dxgi.Get(), "CreateDXGIFactory");
    if (!createFactory) return Failure("factory_export", HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND), &locations);
    Com<IDXGIFactory> factory;
    HRESULT hr = createFactory(__uuidof(IDXGIFactory), reinterpret_cast<void**>(factory.Out()));
    if (FAILED(hr) || !factory.Get()) return Failure("factory_create", hr, &locations);
    Com<IDXGIAdapter> adapter;
    hr = factory.Get()->EnumAdapters(o.adapter, adapter.Out());
    if (FAILED(hr) || !adapter.Get()) return Failure("adapter_select", hr, &locations);
    DXGI_ADAPTER_DESC desc{};
    hr = adapter.Get()->GetDesc(&desc);
    if (FAILED(hr)) return Failure("adapter_description", hr, &locations);
    std::string description;
    const size_t descriptionLength = wcsnlen_s(desc.Description, 128);
    if (descriptionLength >= 128 ||
        !Utf8(std::wstring(desc.Description, descriptionLength), description))
        return Failure("adapter_description_encoding", E_FAIL, &locations);
    const bool expected = desc.VendorId == o.vendor && desc.DeviceId == o.device;
    Record("adapter").Number("ordinal", o.adapter).String("description", description)
        .String("vendor", Hex(desc.VendorId)).String("device", Hex(desc.DeviceId))
        .Boolean("identity_matches", expected).Emit();
    if (!expected) return Failure("adapter_identity_mismatch", E_INVALIDARG, &locations);

    const auto finish = [&](HRESULT code, bool nonnull, UINT featureLevel) {
        // Devices/context are still alive when module evidence is captured.
        const Snapshot final = GraphicsSnapshot("device", locations);
        const bool evidence = final.complete && !final.duplicate;
        const bool created = DeviceCreated(code, nonnull);
        Record("device").String("hresult", Hex(static_cast<UINT>(code)))
            .Boolean("nonnull", nonnull).Boolean("created", created)
            .String("feature_level", Hex(featureLevel)).Boolean("evidence_complete", evidence).Emit();
        return DeviceExitCode(code, nonnull, evidence);
    };
    if (o.route == Route::D3D10) {
        const auto create = Resolve<decltype(&D3D10CreateDevice)>(runtime.Get(), "D3D10CreateDevice");
        if (!create) return Failure("device_export", HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND), &locations);
        Com<ID3D10Device> device;
        hr = create(adapter.Get(), D3D10_DRIVER_TYPE_HARDWARE, nullptr, 0,
            D3D10_SDK_VERSION, device.Out());
        return finish(hr, device.Get() != nullptr, static_cast<UINT>(D3D_FEATURE_LEVEL_10_0));
    }
    if (o.route == Route::D3D101) {
        const auto create = Resolve<decltype(&D3D10CreateDevice1)>(runtime.Get(), "D3D10CreateDevice1");
        if (!create) return Failure("device_export", HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND), &locations);
        Com<ID3D10Device1> device;
        hr = create(adapter.Get(), D3D10_DRIVER_TYPE_HARDWARE, nullptr, 0,
            D3D10_FEATURE_LEVEL_10_1, D3D10_1_SDK_VERSION, device.Out());
        return finish(hr, device.Get() != nullptr, static_cast<UINT>(D3D_FEATURE_LEVEL_10_1));
    }
    const auto create = Resolve<decltype(&D3D11CreateDevice)>(runtime.Get(), "D3D11CreateDevice");
    if (!create) return Failure("device_export", HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND), &locations);
    Com<ID3D11Device> device;
    Com<ID3D11DeviceContext> context; // Destroy before the device, then adapter/factory/libraries.
    const D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0;
    D3D_FEATURE_LEVEL actual{};
    hr = create(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, &requested, 1,
        D3D11_SDK_VERSION, device.Out(), &actual, context.Out());
    return finish(hr, device.Get() != nullptr, static_cast<UINT>(actual));
}

int Dispatch(const std::vector<std::wstring>& args,
    const std::function<int(const Options&)>& run,
    const std::function<int()>& selfTest, const std::function<int(bool)>& help) {
    const Options o = Parse(args);
    switch (o.mode) {
    case Mode::Runtime: return run(o);
    case Mode::SelfTest: return selfTest();
    case Mode::Help: return help(false);
    case Mode::Invalid: return help(true);
    }
    return 2;
}

int SelfTest() {
    UINT total = 0, failed = 0;
    const auto check = [&](const char* name, bool passed) {
        ++total;
        if (!passed) ++failed;
        Record("check").Number("index", total).String("name", name).Boolean("passed", passed).Emit();
    };
    const auto graphicsAbsent = [] {
        for (const wchar_t* name : GraphicsNames)
            if (GetModuleHandleW(name)) return false; // Query only, never load.
        return true;
    };
    check("no_graphics_before_cpu_checks", graphicsAbsent());
    const std::vector<std::wstring> valid = {
        L"--run", L"--route", L"d3d10", L"--adapter", L"0",
        L"--vendor", L"0x8086", L"--device", L"65535"
    };
    auto args = valid;
    const auto parsed = Parse(args);
    check("explicit_runtime", parsed.mode == Mode::Runtime &&
        parsed.route == Route::D3D10 && parsed.adapter == 0 &&
        parsed.vendor == 0x8086 && parsed.device == 0xffff);
    args[2] = L"d3d10.1";
    check("route_10_1", Parse(args).mode == Mode::Runtime && Parse(args).route == Route::D3D101);
    args[2] = L"d3d11";
    check("route_11", Parse(args).mode == Mode::Runtime && Parse(args).route == Route::D3D11);
    args = {L"--run", L"--device", L"1", L"--vendor", L"2",
        L"--adapter", L"31", L"--route", L"d3d11"};
    check("reordered_bounds", Parse(args).mode == Mode::Runtime && Parse(args).adapter == 31);

    const std::vector<std::pair<const char*, std::vector<std::wstring>>> invalid = {
        {"no_run_gate", {L"--route", L"d3d10"}},
        {"mixed_selftest_runtime", {L"--self-test", L"--run"}},
        {"selftest_extra", {L"--self-test", L"anything"}},
        {"help_extra", {L"--help", L"anything"}},
        {"run_alone", {L"--run"}},
        {"unknown_flag", {L"--other"}},
        {"missing_value", {L"--run", L"--route", L"d3d10", L"--adapter", L"0",
            L"--vendor", L"1", L"--device"}},
        {"missing_identity", {L"--run", L"--route", L"d3d10", L"--adapter", L"0"}},
        {"duplicate_option", {L"--run", L"--route", L"d3d10", L"--adapter", L"0",
            L"--vendor", L"1", L"--vendor", L"2"}}
    };
    UINT runtimeCalls = 0;
    const auto fakeRuntime = [&](const Options&) { ++runtimeCalls; return 71; };
    const auto fakeSelf = [] { return 72; };
    const auto fakeHelp = [](bool error) { return error ? 2 : 0; };
    for (const auto& entry : invalid)
        check(entry.first, Dispatch(entry.second, fakeRuntime, fakeSelf, fakeHelp) == 2);
    for (const auto& replacement : std::vector<std::pair<size_t, std::wstring>>{
        {0, L"--RUN"}, {1, L"--unknown"}, {2, L"d3d9"}, {4, L"-1"},
        {4, L"32"}, {4, L"4294967296"}, {4, L"0x100000000"}, {4, L""},
        {4, L"+1"}, {4, L" 1"}, {4, L"1 "}, {4, L"1.0"},
        {6, L"0x"}, {6, L"0xGG"}, {6, L"65536"}, {6, L"0x10000"},
        {8, L"999999999999999999999999"}, {8, L"0X1"}}) {
        args = valid;
        args[replacement.first] = replacement.second;
        check("reject_malformed_or_out_of_range",
            Dispatch(args, fakeRuntime, fakeSelf, fakeHelp) == 2);
    }
    check("invalid_never_dispatches_runtime", runtimeCalls == 0);
    check("default_help_gate", Dispatch({}, fakeRuntime, fakeSelf, fakeHelp) == 0);
    check("help_gate", Dispatch({L"--help"}, fakeRuntime, fakeSelf, fakeHelp) == 0);
    check("selftest_gate", Dispatch({L"--self-test"}, fakeRuntime, fakeSelf, fakeHelp) == 72);
    check("nonruntime_never_dispatches_runtime", runtimeCalls == 0);
    check("valid_dispatches_once", Dispatch(valid, fakeRuntime, fakeSelf, fakeHelp) == 71 &&
        runtimeCalls == 1); // Injected callback only, no graphics execution.
    UINT number = 99;
    check("decimal_zero", ParseNumber(L"0", 0xffff, number) && number == 0);
    check("hex_maximum", ParseNumber(L"0xffff", 0xffff, number) && number == 0xffff);
    check("hex_mixed_case_digits", ParseNumber(L"0xaB", 0xffff, number) && number == 0xab);
    check("json_escapes", Escape(std::string("\"\\\n\t") + char(1)) ==
        "\"\\\"\\\\\\u000a\\u0009\\u0001\"");
    check("hex_fixed_width", Hex(0x80004005) == "0x80004005" && Hex(0) == "0x00000000");
    check("failed_hresult_nonnull_is_failure", !DeviceCreated(E_FAIL, true) &&
        DeviceExitCode(E_FAIL, true, true) == 3);
    check("success_null_is_failure", !DeviceCreated(S_OK, false) &&
        DeviceExitCode(S_OK, false, true) == 3);
    check("incomplete_evidence_is_failure", DeviceCreated(S_OK, true) &&
        DeviceExitCode(S_OK, true, false) == 3);
    check("success_requires_device_and_evidence", DeviceExitCode(S_OK, true, true) == 0);
    check("fixed_structured_record", Record("device").String("hresult", Hex(0))
        .Boolean("nonnull", true).Number("adapter", 2).Text() ==
        "{\"event\":\"device\",\"hresult\":\"0x00000000\",\"nonnull\":true,\"adapter\":2}");
    check("directory_boundary", InDirectory(L"C:\\fixture\\dxgi.dll", L"c:\\FIXTURE") &&
        !InDirectory(L"C:\\fixture-other\\dxgi.dll", L"C:\\fixture") &&
        !InDirectory(L"C:\\fixture", L"C:\\fixture"));
    check("extended_dos_path", StripExtendedPrefix(L"\\\\?\\C:\\fixture\\dxgi.dll") ==
        L"C:\\fixture\\dxgi.dll");
    check("extended_unc_path", StripExtendedPrefix(L"\\\\?\\UNC\\server\\share\\dxgi.dll") ==
        L"\\\\server\\share\\dxgi.dll");
    std::string encoded;
    check("unicode_utf8", Utf8(L"\u00e9", encoded) && encoded == "\xc3\xa9");
    check("invalid_unicode_rejected", !Utf8(std::wstring(1, wchar_t(0xd800)), encoded));
    check("no_graphics_after_cpu_checks", graphicsAbsent());
    Record("self_test").Number("total", total).Number("failed", failed)
        .String("architecture", "x64").Boolean("cpu_only", true).Emit();
    return failed == 0 ? 0 : 1;
}

int Usage(bool invalid) {
    Record("usage").Boolean("invalid_arguments", invalid)
        .String("self_test", "D3D10Probe.exe --self-test")
        .String("runtime", "--run --route d3d10|d3d10.1|d3d11 --adapter 0..31 --vendor ID --device ID")
        .String("notice", "Runtime requires separate host authorization; output contains private loaded paths.")
        .Emit();
    return invalid ? 2 : 0;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        // Bound untrusted CLI input before copying/dispatch; no graphics calls.
        if (argc > 10) return Usage(true);
        std::vector<std::wstring> args;
        for (int i = 1; i < argc; ++i) {
            if (wcsnlen_s(argv[i], 128) >= 128) return Usage(true);
            args.emplace_back(argv[i]);
        }
        return Dispatch(args, Runtime, SelfTest, Usage);
    } catch (...) {
        Record("failure").String("stage", "unhandled_cpp_exception").Emit();
        return 4; // Not a device success; native crashes/hangs require human handling.
    }
}
