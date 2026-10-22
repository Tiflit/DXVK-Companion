// Original bounded validation probe for issue #250; not part of Companion.
// No graphics DLL is statically imported. Runtime calls require --run and
// explicit route/adapter/identity arguments; --self-test is CPU-only.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
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
enum class Origin { Unspecified, System, Fixture };
struct Options {
    Mode mode = Mode::Invalid;
    Route route = Route::D3D10;
    UINT adapter = 0;
    UINT vendor = 0;
    UINT device = 0;
    Origin dxgiOrigin = Origin::Unspecified;
    Origin runtimeOrigin = Origin::Unspecified;
};

bool Qualified(const Options& o) {
    return o.dxgiOrigin != Origin::Unspecified && o.runtimeOrigin != Origin::Unspecified;
}

bool ParseOrigin(std::wstring_view text, Origin& origin) {
    if (text == L"system") origin = Origin::System;
    else if (text == L"fixture") origin = Origin::Fixture;
    else return false;
    return true;
}

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
    // Four legacy options or all six qualified options, following the gate.
    if ((args.size() != 9 && args.size() != 13) || args[0] != L"--run") return o;
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
        } else if (key == L"--expect-dxgi-origin") {
            bit = 16;
            if (!ParseOrigin(value, o.dxgiOrigin)) return Options{};
        } else if (key == L"--expect-runtime-origin") {
            bit = 32;
            if (!ParseOrigin(value, o.runtimeOrigin)) return Options{};
        } else return Options{};
        if ((seen & bit) != 0) return Options{};
        seen |= bit;
    }
    if (seen == 15 || seen == 63) o.mode = Mode::Runtime;
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

template<class Function> Function FunctionAt(FARPROC address) {
    Function result = nullptr;
    // Windows function pointers have the same representation; use the SDK's
    // exact declaration without C4191 or a hand-maintained ABI signature.
    static_assert(sizeof(result) == sizeof(address));
    std::memcpy(&result, &address, sizeof(result));
    return result;
}

// Own exactly the reference acquired by GetModuleHandleExW. The injected
// releaser is used only by synthetic CPU lifetime checks; production releases
// normally, without PIN or UNCHANGED_REFCOUNT.
class ModuleReference {
    HMODULE module = nullptr;
    std::function<void(HMODULE)> release;
public:
    explicit ModuleReference(std::function<void(HMODULE)> cleanup =
        [](HMODULE value) { FreeLibrary(value); }) : release(cleanup) {}
    ~ModuleReference() { if (module) release(module); }
    HMODULE Get() const { return module; }
    HMODULE* Out() { return &module; }
    ModuleReference(const ModuleReference&) = delete;
    ModuleReference& operator=(const ModuleReference&) = delete;
};

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
    SetLastError(ERROR_SUCCESS);
    const DWORD length = GetFinalPathNameByHandleW(file, buffer.data(),
        static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    const DWORD queryError = GetLastError();
    CloseHandle(file);
    if (length == 0 || length >= buffer.size()) {
        SetLastError(length >= buffer.size() ? ERROR_INSUFFICIENT_BUFFER : queryError);
        return false;
    }
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

struct Snapshot {
    bool complete = true;
    bool duplicate = false;
    UINT count = 0;
    std::vector<std::wstring> paths{};
};

struct ProofFacts {
    bool resolved = false;
    bool acquired = false;
    bool sameModule = false;
    bool canonical = false;
    bool sameFile = false;
    bool expectedOrigin = false;
    std::wstring path{};
};

bool Verified(const ProofFacts& proof) {
    return proof.resolved && proof.acquired && proof.sameModule && proof.canonical &&
        proof.sameFile && proof.expectedOrigin;
}

bool EvidenceAllowed(const Snapshot& snapshot, bool qualified,
    const ProofFacts& dxgi, const ProofFacts& runtime) {
    return snapshot.complete && Verified(dxgi) && Verified(runtime) &&
        (!snapshot.duplicate || qualified);
}

bool OwnerObserved(const Snapshot& snapshot, const std::wstring& path) {
    if (path.empty()) return false;
    for (const auto& observed : snapshot.paths)
        if (_wcsicmp(path.c_str(), observed.c_str()) == 0) return true;
    return false;
}

bool RuntimeEvidenceAllowed(const Snapshot& snapshot, bool qualified,
    const ProofFacts& dxgi, const ProofFacts& runtime) {
    return EvidenceAllowed(snapshot, qualified, dxgi, runtime) &&
        OwnerObserved(snapshot, dxgi.path) && OwnerObserved(snapshot, runtime.path);
}

const char* PathOrigin(const std::wstring& path, const Locations& locations) {
    return InDirectory(path, locations.fixture) ? "fixture" :
        InDirectory(path, locations.system) ? "system" : "other";
}

bool ExpectedPathMatches(const std::wstring& actual, const std::wstring& expectedPath,
    Origin expected, const Locations& locations) {
    if (expected == Origin::Unspecified) return true; // Legacy, never qualifies duplicates.
    return !actual.empty() && !expectedPath.empty() &&
        _wcsicmp(actual.c_str(), expectedPath.c_str()) == 0 &&
        std::strcmp(PathOrigin(actual, locations), expected == Origin::Fixture ? "fixture" : "system") == 0;
}

ProofFacts VerifyExport(FARPROC address, HMODULE library, const wchar_t* libraryName,
    const char* exportName, const char* role, Origin expected,
    const Locations& locations, ModuleReference& owner) {
    ProofFacts proof;
    proof.resolved = address != nullptr;
    DWORD queryError = ERROR_SUCCESS;
    if (address) {
        // FROM_ADDRESS interprets this value as an address, never as a string.
        LPCWSTR location = nullptr;
        static_assert(sizeof(location) == sizeof(address));
        std::memcpy(&location, &address, sizeof(location));
        SetLastError(ERROR_SUCCESS);
        proof.acquired = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            location, owner.Out()) != FALSE;
        if (!proof.acquired) queryError = GetLastError();
    }
    proof.sameModule = proof.acquired && owner.Get() == library;
    std::wstring path, heldPath, expectedPath;
    std::string encodedPath;
    proof.canonical = proof.acquired && ModulePath(owner.Get(), path) &&
        ModulePath(library, heldPath) && Utf8(path, encodedPath);
    if (proof.canonical) {
        proof.path = path;
        proof.sameFile = _wcsicmp(path.c_str(), heldPath.c_str()) == 0;
        if (expected == Origin::Unspecified) {
            // Legacy mode still proves exact ownership, but cannot admit duplicates.
            proof.expectedOrigin = true;
        } else {
            const std::wstring& directory = expected == Origin::Fixture ?
                locations.fixture : locations.system;
            proof.expectedOrigin = CanonicalFile(directory + L"\\" + libraryName, expectedPath) &&
                ExpectedPathMatches(path, expectedPath, expected, locations);
        }
    }
    Record("export_proof").String("role", role).String("export", exportName)
        .String("path", encodedPath).String("origin", proof.canonical ? PathOrigin(path, locations) : "unknown")
        .String("expected_origin", expected == Origin::Fixture ? "fixture" :
            expected == Origin::System ? "system" : "unspecified")
        .Boolean("resolved", proof.resolved).Boolean("owner_acquired", proof.acquired)
        .Boolean("same_module", proof.sameModule).Boolean("canonical", proof.canonical)
        .Boolean("same_file", proof.sameFile).Boolean("expected_origin_matches", proof.expectedOrigin)
        .Number("win32_error", queryError).Boolean("verified", Verified(proof)).Emit();
    return proof;
}

// Injected boundary keeps the traversal/error/lifetime checks CPU-only.
struct SnapshotApi {
    std::function<HANDLE(DWORD, DWORD)> capture;
    std::function<BOOL(HANDLE, MODULEENTRY32W*)> first;
    std::function<BOOL(HANDLE, MODULEENTRY32W*)> next;
    std::function<DWORD()> error;
    std::function<void(HANDLE)> close;
    std::function<bool(const std::wstring&, std::wstring&)> canonical;
    std::function<void(const Record&)> emit;
};

class SnapshotHandle {
    HANDLE handle;
    const std::function<void(HANDLE)>& close;
public:
    SnapshotHandle(HANDLE value, const std::function<void(HANDLE)>& cleanup) noexcept
        : handle(value), close(cleanup) {}
    ~SnapshotHandle() { if (handle != INVALID_HANDLE_VALUE && handle != nullptr) close(handle); }
    HANDLE Get() const { return handle; }
    SnapshotHandle(const SnapshotHandle&) = delete;
    SnapshotHandle& operator=(const SnapshotHandle&) = delete;
};

Snapshot CollectSnapshot(const char* phase, const Locations& locations,
    DWORD processId, const SnapshotApi& api) {
    Snapshot result;
    const auto fail = [&](const char* stage, UINT index, DWORD error, bool truncated = false) {
        result.complete = false;
        api.emit(Record("module_error").String("phase", phase).String("stage", stage)
            .Number("module_index", index).Number("win32_error", error).Boolean("truncated", truncated));
    };
    const auto collect = [&] {
        // Exactly one calling-process module capture; never retry BAD_LENGTH.
        const HANDLE captured = api.capture(TH32CS_SNAPMODULE, processId);
        if (captured == INVALID_HANDLE_VALUE || captured == nullptr) {
            fail("snapshot_capture", 0, api.error());
            return;
        }
        SnapshotHandle snapshot(captured, api.close);
        MODULEENTRY32W entry{};
        entry.dwSize = static_cast<DWORD>(sizeof(entry));
        if (!api.first(snapshot.Get(), &entry)) {
            // Even NO_MORE_FILES is incomplete here: this executable must exist.
            fail("snapshot_first", 0, api.error());
            return;
        }
        std::array<UINT, GraphicsNames.size()> counts{};
        for (UINT i = 0; i < 1024; ++i) {
            if (entry.th32ProcessID != processId) {
                fail("entry_pid", i, ERROR_INVALID_DATA);
                return;
            }
            const size_t nameCapacity = sizeof(entry.szModule) / sizeof(wchar_t);
            const size_t nameLength = wcsnlen_s(entry.szModule, nameCapacity);
            // Reject boundary-length strings too: truncation cannot be excluded.
            if (nameLength == 0 || nameLength >= nameCapacity - 1) {
                fail("entry_name", i, nameLength == 0 ? ERROR_INVALID_DATA : ERROR_INSUFFICIENT_BUFFER,
                    nameLength >= nameCapacity - 1);
                return;
            }
            if (std::wstring_view(entry.szModule, nameLength).find_first_of(L"\\/:") != std::wstring_view::npos) {
                fail("entry_name", i, ERROR_INVALID_DATA);
                return;
            }
            for (size_t j = 0; j < GraphicsNames.size(); ++j) {
                if (_wcsicmp(entry.szModule, GraphicsNames[j]) != 0) continue;
                ++result.count;
                if (++counts[j] > 1) result.duplicate = true;
                const size_t pathCapacity = sizeof(entry.szExePath) / sizeof(wchar_t);
                const size_t pathLength = wcsnlen_s(entry.szExePath, pathCapacity);
                if (pathLength == 0 || pathLength >= pathCapacity - 1) {
                    fail("entry_path", i, pathLength == 0 ? ERROR_INVALID_DATA : ERROR_INSUFFICIENT_BUFFER,
                        pathLength >= pathCapacity - 1);
                    return;
                }
                const std::wstring copiedPath(entry.szExePath, pathLength);
                const size_t slash = copiedPath.find_last_of(L'\\');
                const bool absolute = (copiedPath.size() > 2 && copiedPath[1] == L':' &&
                    copiedPath[2] == L'\\') || copiedPath.rfind(L"\\\\", 0) == 0;
                if (!absolute || slash == std::wstring::npos ||
                    _wcsicmp(copiedPath.c_str() + slash + 1, GraphicsNames[j]) != 0) {
                    fail("entry_path", i, ERROR_INVALID_DATA);
                    return;
                }
                std::wstring path;
                std::string encodedPath, encodedName;
                // Names/paths come from the captured entry, never a live HMODULE.
                if (!api.canonical(copiedPath, path)) {
                    fail("canonical_path", i, api.error());
                    return;
                }
                if (path.empty() || !Utf8(path, encodedPath) || !Utf8(GraphicsNames[j], encodedName)) {
                    fail("path_encoding", i, ERROR_INVALID_DATA);
                    return;
                }
                result.paths.push_back(path);
                api.emit(Record("module").String("phase", phase).String("name", encodedName)
                    .String("origin", PathOrigin(path, locations)).String("path", encodedPath));
            }
            entry = MODULEENTRY32W{};
            entry.dwSize = static_cast<DWORD>(sizeof(entry));
            if (!api.next(snapshot.Get(), &entry)) {
                const DWORD error = api.error();
                if (error != ERROR_NO_MORE_FILES) fail("snapshot_next", i, error);
                return;
            }
        }
        // One bounded lookahead distinguishes EOF at 1024 from excess entries;
        // the returned excess entry is never processed, resolved or disclosed.
        fail("entry_limit", 1023, ERROR_INSUFFICIENT_BUFFER, true);
    };
    collect(); // Snapshot handle closes before emitting the final summary.
    api.emit(Record("graphics_modules").String("phase", phase)
        .Number("count", result.count).Boolean("complete", result.complete)
        .Boolean("duplicate_basename", result.duplicate));
    return result;
}

Snapshot GraphicsSnapshot(const char* phase, const Locations& locations) {
    const SnapshotApi api{
        [](DWORD flags, DWORD pid) { return CreateToolhelp32Snapshot(flags, pid); },
        [](HANDLE handle, MODULEENTRY32W* entry) { return Module32FirstW(handle, entry); },
        [](HANDLE handle, MODULEENTRY32W* entry) { return Module32NextW(handle, entry); },
        [] { return GetLastError(); },
        [](HANDLE handle) { CloseHandle(handle); },
        CanonicalFile,
        [](const Record& record) { record.Emit(); }
    };
    return CollectSnapshot(phase, locations, GetCurrentProcessId(), api);
}

int Failure(const char* stage, HRESULT hr, const Locations* locations = nullptr) {
    Record("failure").String("stage", stage).String("hresult", Hex(static_cast<UINT>(hr))).Emit();
    if (locations) GraphicsSnapshot("failure", *locations);
    return 3;
}

int Runtime(const Options& o) {
    Record("probe").Number("schema", 1).String("architecture", "x64")
        .String("route", RouteName(o.route)).Number("adapter", o.adapter)
        .String("expected_vendor", Hex(o.vendor)).String("expected_device", Hex(o.device))
        .Boolean("qualified", Qualified(o)).Emit();
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
    if (!loaded.complete || (loaded.duplicate && !Qualified(o)))
        return Failure("ambiguous_or_unobserved_modules", E_UNEXPECTED);
    // Resolve and prove BOTH critical functions before invoking either of them.
    const char* deviceExport = o.route == Route::D3D10 ? "D3D10CreateDevice" :
        o.route == Route::D3D101 ? "D3D10CreateDevice1" : "D3D11CreateDevice";
    const FARPROC factoryAddress = GetProcAddress(dxgi.Get(), "CreateDXGIFactory");
    const FARPROC deviceAddress = GetProcAddress(runtime.Get(), deviceExport);
    // Acquired owner references and original libraries outlive all COM objects.
    ModuleReference factoryOwner, deviceOwner;
    const ProofFacts factoryProof = VerifyExport(factoryAddress, dxgi.Get(), L"dxgi.dll",
        "CreateDXGIFactory", "dxgi", o.dxgiOrigin, locations, factoryOwner);
    const ProofFacts deviceProof = VerifyExport(deviceAddress, runtime.Get(), libraryName,
        deviceExport, "runtime", o.runtimeOrigin, locations, deviceOwner);
    if (!RuntimeEvidenceAllowed(loaded, Qualified(o), factoryProof, deviceProof))
        return Failure("export_provenance", E_UNEXPECTED, &locations);
    const auto createFactory = FunctionAt<decltype(&CreateDXGIFactory)>(factoryAddress);
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

    // Factory/adapter discovery may load further modules. Refuse incomplete
    // evidence before the single device attempt as well as at its conclusion.
    const Snapshot selected = GraphicsSnapshot("selected", locations);
    if (!RuntimeEvidenceAllowed(selected, Qualified(o), factoryProof, deviceProof))
        return Failure("selected_module_evidence", E_UNEXPECTED);

    const auto finish = [&](HRESULT code, bool nonnull, UINT featureLevel) {
        // Devices/context are still alive when module evidence is captured.
        const Snapshot final = GraphicsSnapshot("device", locations);
        const bool evidence = RuntimeEvidenceAllowed(final, Qualified(o), factoryProof, deviceProof);
        const bool created = DeviceCreated(code, nonnull);
        Record("device").String("hresult", Hex(static_cast<UINT>(code)))
            .Boolean("nonnull", nonnull).Boolean("created", created)
            .String("feature_level", Hex(featureLevel)).Boolean("evidence_complete", evidence).Emit();
        return DeviceExitCode(code, nonnull, evidence);
    };
    if (o.route == Route::D3D10) {
        const auto create = FunctionAt<decltype(&D3D10CreateDevice)>(deviceAddress);
        Com<ID3D10Device> device;
        hr = create(adapter.Get(), D3D10_DRIVER_TYPE_HARDWARE, nullptr, 0,
            D3D10_SDK_VERSION, device.Out());
        return finish(hr, device.Get() != nullptr, static_cast<UINT>(D3D_FEATURE_LEVEL_10_0));
    }
    if (o.route == Route::D3D101) {
        const auto create = FunctionAt<decltype(&D3D10CreateDevice1)>(deviceAddress);
        Com<ID3D10Device1> device;
        hr = create(adapter.Get(), D3D10_DRIVER_TYPE_HARDWARE, nullptr, 0,
            D3D10_FEATURE_LEVEL_10_1, D3D10_1_SDK_VERSION, device.Out());
        return finish(hr, device.Get() != nullptr, static_cast<UINT>(D3D_FEATURE_LEVEL_10_1));
    }
    const auto create = FunctionAt<decltype(&D3D11CreateDevice)>(deviceAddress);
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

    // Additional #255 checks: all inputs/handles here are synthetic. No owner
    // query, module/adapter enumeration or real runtime callback is invoked.
    args = valid;
    args.insert(args.end(), {L"--expect-dxgi-origin", L"fixture",
        L"--expect-runtime-origin", L"system"});
    const auto qualified = Parse(args);
    check("qualified_mixed_origins", qualified.mode == Mode::Runtime && Qualified(qualified) &&
        qualified.dxgiOrigin == Origin::Fixture && qualified.runtimeOrigin == Origin::System);
    check("legacy_stays_unqualified", !Qualified(Parse(valid)));
    for (const auto& origins : std::vector<std::pair<std::wstring, std::wstring>>{
        {L"system", L"system"}, {L"fixture", L"fixture"}, {L"system", L"fixture"}}) {
        auto input = args;
        input[10] = origins.first;
        input[12] = origins.second;
        check("qualified_origin_combinations", Parse(input).mode == Mode::Runtime && Qualified(Parse(input)));
    }
    auto reordered = std::vector<std::wstring>{L"--run", L"--expect-runtime-origin", L"system",
        L"--device", L"1", L"--expect-dxgi-origin", L"fixture", L"--vendor", L"2",
        L"--adapter", L"31", L"--route", L"d3d11"};
    check("qualified_reordered", Parse(reordered).mode == Mode::Runtime && Qualified(Parse(reordered)));
    UINT qualificationCalls = 0;
    const auto mockQualifiedRuntime = [&](const Options&) { ++qualificationCalls; return 73; };
    const auto invalidQualification = [&](const std::vector<std::wstring>& input) {
        return Dispatch(input, mockQualifiedRuntime, fakeSelf, fakeHelp) == 2;
    };
    for (const auto& replacement : std::vector<std::pair<size_t, std::wstring>>{
        {9, L"--expect-runtime-origin"}, {11, L"--expect-dxgi-origin"},
        {10, L"other"}, {12, L"other"}, {10, L""}, {12, L""},
        {10, L"System"}, {12, L"FIXTURE"}, {10, L" system"}, {12, L"fixture "},
        {9, L"--expect-origin"}, {11, L"--unknown"}}) {
        auto input = args;
        input[replacement.first] = replacement.second;
        check("invalid_qualification_refused", invalidQualification(input));
    }
    for (const wchar_t* key : {L"--expect-dxgi-origin", L"--expect-runtime-origin"}) {
        auto input = valid;
        input.insert(input.end(), {key, L"system"});
        check("partial_qualification_refused", invalidQualification(input));
    }
    auto missingValue = args;
    missingValue.pop_back();
    check("missing_origin_value_refused", invalidQualification(missingValue));
    auto extra = args;
    extra.insert(extra.end(), {L"--expect-dxgi-origin", L"fixture"});
    check("extra_qualification_refused", invalidQualification(extra));
    check("invalid_qualification_no_dispatch", qualificationCalls == 0);
    check("qualified_dispatch_once", Dispatch(args, mockQualifiedRuntime, fakeSelf, fakeHelp) == 73 &&
        qualificationCalls == 1);

    const ProofFacts good{true, true, true, true, true, true};
    const Snapshot unique{true, false, 2}, duplicate{true, true, 3};
    check("legacy_unique_proven", EvidenceAllowed(unique, false, good, good));
    check("legacy_duplicate_refused", !EvidenceAllowed(duplicate, false, good, good));
    check("qualified_unique_proven", EvidenceAllowed(unique, true, good, good));
    check("qualified_duplicate_proven", EvidenceAllowed(duplicate, true, good, good));
    for (bool ProofFacts::* field : {&ProofFacts::resolved, &ProofFacts::acquired,
        &ProofFacts::sameModule, &ProofFacts::canonical, &ProofFacts::sameFile,
        &ProofFacts::expectedOrigin}) {
        auto bad = good;
        bad.*field = false;
        check("missing_or_mismatched_dxgi_proof_refused",
            !EvidenceAllowed(unique, true, bad, good) && !EvidenceAllowed(duplicate, true, bad, good));
        check("missing_or_mismatched_runtime_proof_refused",
            !EvidenceAllowed(unique, true, good, bad) && !EvidenceAllowed(duplicate, true, good, bad));
        check("legacy_failed_proof_refused", !EvidenceAllowed(unique, false, bad, good) &&
            !EvidenceAllowed(unique, false, good, bad));
    }
    check("unknown_proofs_refused", !EvidenceAllowed(duplicate, true, ProofFacts{}, ProofFacts{}));
    const Locations syntheticLocations{L"C:\\fixture", L"C:\\system"};
    check("exact_expected_fixture_path", ExpectedPathMatches(L"C:\\fixture\\dxgi.dll",
        L"c:\\FIXTURE\\DXGI.DLL", Origin::Fixture, syntheticLocations));
    check("exact_expected_system_path", ExpectedPathMatches(L"C:\\system\\d3d10.dll",
        L"C:\\system\\d3d10.dll", Origin::System, syntheticLocations));
    check("same_origin_wrong_file_refused", !ExpectedPathMatches(L"C:\\fixture\\other.dll",
        L"C:\\fixture\\dxgi.dll", Origin::Fixture, syntheticLocations));
    check("unexpected_origin_refused", !ExpectedPathMatches(L"C:\\system\\dxgi.dll",
        L"C:\\system\\dxgi.dll", Origin::Fixture, syntheticLocations));
    check("redirected_expected_file_refused", !ExpectedPathMatches(L"C:\\elsewhere\\dxgi.dll",
        L"C:\\elsewhere\\dxgi.dll", Origin::Fixture, syntheticLocations));
    check("missing_expected_path_refused", !ExpectedPathMatches(L"C:\\fixture\\dxgi.dll",
        L"", Origin::Fixture, syntheticLocations));
    for (bool hasDuplicate : {false, true}) {
        const Snapshot incomplete{false, hasDuplicate, 2};
        check("incomplete_or_query_error_refused",
            !EvidenceAllowed(incomplete, true, good, good) && !EvidenceAllowed(incomplete, false, good, good));
    }

    const HMODULE syntheticHandle = reinterpret_cast<HMODULE>(static_cast<UINT_PTR>(1));
    UINT releases = 0;
    bool exactRelease = true;
    const auto fakeRelease = [&](HMODULE handle) { ++releases; exactRelease &= handle == syntheticHandle; };
    {
        ModuleReference failedQuery(fakeRelease);
    }
    check("unacquired_reference_not_released", releases == 0);
    {
        ModuleReference owner(fakeRelease);
        *owner.Out() = syntheticHandle;
        check("owned_reference_held_in_scope", owner.Get() == syntheticHandle && releases == 0);
    }
    check("owned_reference_released_once", releases == 1 && exactRelease);
    const auto earlyRefusal = [&] {
        ModuleReference owner(fakeRelease);
        *owner.Out() = syntheticHandle;
        return false;
    };
    check("early_refusal_balances_reference", !earlyRefusal() && releases == 2 && exactRelease);
    try {
        ModuleReference owner(fakeRelease);
        *owner.Out() = syntheticHandle;
        throw 1; // Exercise C++ unwinding only, never native fault injection.
    } catch (int) { }
    check("exception_balances_reference", releases == 3 && exactRelease);

    // #257 exercises the production collector via fake snapshot APIs. These
    // entries, handles, PIDs, paths and canonical results are all synthetic.
    const auto entryFor = [](const wchar_t* name, const wchar_t* path) {
        MODULEENTRY32W entry{};
        entry.dwSize = static_cast<DWORD>(sizeof(entry));
        entry.th32ProcessID = 123;
        wcscpy_s(entry.szModule, name);
        wcscpy_s(entry.szExePath, path);
        return entry;
    };
    struct FakeSnapshot {
        std::vector<MODULEENTRY32W> entries;
        std::vector<std::wstring> canonicalInputs;
        std::vector<std::string> events;
        std::wstring canonicalOverride;
        DWORD captureError = 0, firstError = 0, lastError = 0;
        UINT captures = 0, firstCalls = 0, nextCalls = 0, closes = 0;
        UINT failNextAt = 0;
        size_t cursor = 0;
        bool exactBoundary = true, canonicalFails = false, canonicalThrows = false;
        bool nullCapture = false;
        SnapshotApi Api() {
            const HANDLE fake = reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(1));
            return {
                [this, fake](DWORD flags, DWORD pid) {
                    ++captures;
                    exactBoundary &= flags == TH32CS_SNAPMODULE && pid == 123;
                    lastError = captureError;
                    return nullCapture ? nullptr : captureError ? INVALID_HANDLE_VALUE : fake;
                },
                [this, fake](HANDLE handle, MODULEENTRY32W* entry) -> BOOL {
                    ++firstCalls;
                    exactBoundary &= handle == fake && entry->dwSize == sizeof(*entry) && entry->szModule[0] == 0;
                    cursor = 0;
                    if (firstError || entries.empty()) {
                        lastError = firstError ? firstError : ERROR_NO_MORE_FILES;
                        return FALSE;
                    }
                    *entry = entries[0];
                    return TRUE;
                },
                [this, fake](HANDLE handle, MODULEENTRY32W* entry) -> BOOL {
                    ++nextCalls;
                    exactBoundary &= handle == fake && entry->dwSize == sizeof(*entry) && entry->szModule[0] == 0;
                    if (nextCalls == failNextAt) { lastError = ERROR_INVALID_HANDLE; return FALSE; }
                    if (++cursor == entries.size()) { lastError = ERROR_NO_MORE_FILES; return FALSE; }
                    *entry = entries[cursor];
                    return TRUE;
                },
                [this] { return lastError; },
                [this, fake](HANDLE handle) { ++closes; exactBoundary &= handle == fake; },
                [this](const std::wstring& input, std::wstring& output) {
                    canonicalInputs.push_back(input);
                    if (canonicalThrows) throw 1;
                    if (canonicalFails) { lastError = ERROR_FILE_NOT_FOUND; return false; }
                    output = canonicalOverride.empty() ? input : canonicalOverride;
                    return true;
                },
                [this](const Record& record) { events.push_back(record.Text()); }
            };
        }
    };
    const auto collectFake = [&](FakeSnapshot& fake) {
        const auto api = fake.Api();
        return CollectSnapshot("test", syntheticLocations, 123, api);
    };
    const auto errorIs = [](const FakeSnapshot& fake, const char* stage, DWORD code) {
        UINT errors = 0;
        bool matches = false;
        for (const auto& event : fake.events) {
            if (event.find("\"event\":\"module_error\"") == std::string::npos) continue;
            ++errors;
            matches = event.find(std::string("\"stage\":\"") + stage + "\"") != std::string::npos &&
                event.find("\"win32_error\":" + std::to_string(code) + ",") != std::string::npos;
        }
        return errors == 1 && matches;
    };
    const auto unrelated = entryFor(L"unrelated.dll", L"C:\\unreported\\unrelated.dll");
    FakeSnapshot emptyGraphics;
    emptyGraphics.entries = {unrelated};
    const auto none = collectFake(emptyGraphics);
    check("captured_unrelated_entry_never_resolved_or_disclosed", none.complete && none.count == 0 &&
        emptyGraphics.canonicalInputs.empty() && emptyGraphics.events.size() == 1 &&
        emptyGraphics.events[0].find("unrelated") == std::string::npos);
    check("capture_current_pid_modules_only_initialized_entries", emptyGraphics.exactBoundary &&
        emptyGraphics.captures == 1 && emptyGraphics.firstCalls == 1 && emptyGraphics.nextCalls == 1 && emptyGraphics.closes == 1);
    FakeSnapshot allNames;
    for (const wchar_t* name : GraphicsNames) {
        const auto path = std::wstring(L"C:\\fixture\\") + name;
        allNames.entries.push_back(entryFor(name, path.c_str()));
    }
    const auto all = collectFake(allNames);
    check("all_six_exact_names_from_copied_paths", all.complete && all.count == 6 && !all.duplicate &&
        all.paths == allNames.canonicalInputs && all.paths.size() == 6 && allNames.closes == 1);
    FakeSnapshot similarNames;
    similarNames.entries = {entryFor(L"dxgi.dll.bak", L"C:\\unreported\\dxgi.dll.bak"),
        entryFor(L"xdxgi.dll", L"C:\\unreported\\xdxgi.dll"), entryFor(L"D3D11.DLL", L"C:\\fixture\\D3D11.DLL")};
    const auto similar = collectFake(similarNames);
    check("exact_case_insensitive_filter_no_suffix_matches", similar.complete && similar.count == 1 &&
        similarNames.canonicalInputs == std::vector<std::wstring>{L"C:\\fixture\\D3D11.DLL"});
    for (DWORD error : {DWORD(ERROR_BAD_LENGTH), DWORD(ERROR_ACCESS_DENIED), DWORD(ERROR_INVALID_HANDLE)}) {
        FakeSnapshot captureFailure;
        captureFailure.captureError = error;
        const auto failed = collectFake(captureFailure);
        check("capture_error_retained_without_retry_or_release", !failed.complete &&
            captureFailure.captures == 1 && captureFailure.firstCalls == 0 && captureFailure.closes == 0 &&
            errorIs(captureFailure, "snapshot_capture", error));
    }
    FakeSnapshot nullSnapshot;
    nullSnapshot.nullCapture = true;
    check("null_snapshot_unknown_error_refused", !collectFake(nullSnapshot).complete &&
        errorIs(nullSnapshot, "snapshot_capture", 0) && nullSnapshot.closes == 0);
    FakeSnapshot emptyList;
    check("empty_process_list_never_complete", !collectFake(emptyList).complete &&
        errorIs(emptyList, "snapshot_first", ERROR_NO_MORE_FILES) && emptyList.closes == 1 && emptyList.nextCalls == 0);
    FakeSnapshot firstFailure;
    firstFailure.firstError = ERROR_ACCESS_DENIED;
    check("first_error_closes_once", !collectFake(firstFailure).complete &&
        errorIs(firstFailure, "snapshot_first", ERROR_ACCESS_DENIED) && firstFailure.closes == 1);
    FakeSnapshot nextFailure;
    nextFailure.entries = {allNames.entries[4], allNames.entries[3]};
    nextFailure.failNextAt = 1;
    const auto partial = collectFake(nextFailure);
    check("next_error_not_end_of_list", !partial.complete && partial.count == 1 && partial.paths.size() == 1 &&
        errorIs(nextFailure, "snapshot_next", ERROR_INVALID_HANDLE) && nextFailure.nextCalls == 1 &&
        nextFailure.canonicalInputs.size() == 1 && nextFailure.closes == 1);
    FakeSnapshot limit;
    limit.entries.assign(1024, unrelated);
    check("exact_1024_entries_with_proven_eof", collectFake(limit).complete && limit.nextCalls == 1024 && limit.closes == 1);
    FakeSnapshot excess;
    excess.entries.assign(1024, unrelated);
    excess.entries.push_back(entryFor(L"dxgi.dll", L"invalid"));
    check("excess_entry_never_processed", !collectFake(excess).complete && excess.nextCalls == 1024 &&
        excess.canonicalInputs.empty() && excess.closes == 1 && errorIs(excess, "entry_limit", ERROR_INSUFFICIENT_BUFFER));
    check("limit_error_index_bounded", excess.events[0].find("\"module_index\":1023,") != std::string::npos);
    for (UINT malformed = 0; malformed < 4; ++malformed) {
        FakeSnapshot nameFailure;
        auto entry = unrelated;
        if (malformed == 0) entry.szModule[0] = 0;
        if (malformed == 1 || malformed == 2) {
            for (auto& character : entry.szModule) character = L'x';
            if (malformed == 2) entry.szModule[(sizeof(entry.szModule) / sizeof(wchar_t)) - 1] = 0;
        }
        if (malformed == 3) wcscpy_s(entry.szModule, L"folder\\dxgi.dll");
        nameFailure.entries = {entry};
        check("malformed_name_refuses_without_path_query", !collectFake(nameFailure).complete &&
            nameFailure.canonicalInputs.empty() && nameFailure.closes == 1 && nameFailure.nextCalls == 0 &&
            errorIs(nameFailure, "entry_name", malformed == 1 || malformed == 2 ? ERROR_INSUFFICIENT_BUFFER : ERROR_INVALID_DATA));
    }
    FakeSnapshot wrongPid;
    auto foreign = unrelated;
    foreign.th32ProcessID = 124;
    wrongPid.entries = {foreign};
    check("inconsistent_pid_refused", !collectFake(wrongPid).complete && wrongPid.closes == 1 &&
        wrongPid.canonicalInputs.empty() && errorIs(wrongPid, "entry_pid", ERROR_INVALID_DATA));
    for (UINT malformed = 0; malformed < 5; ++malformed) {
        FakeSnapshot pathFailure;
        auto entry = allNames.entries[4]; // dxgi.dll
        if (malformed == 0) entry.szExePath[0] = 0;
        if (malformed == 1 || malformed == 2) {
            for (auto& character : entry.szExePath) character = L'x';
            if (malformed == 2) entry.szExePath[MAX_PATH - 1] = 0;
        }
        if (malformed == 3) wcscpy_s(entry.szExePath, L"dxgi.dll");
        if (malformed == 4) wcscpy_s(entry.szExePath, L"C:\\fixture\\other.dll");
        pathFailure.entries = {entry};
        check("malformed_path_refuses_before_file_access", !collectFake(pathFailure).complete &&
            pathFailure.canonicalInputs.empty() && pathFailure.closes == 1 &&
            errorIs(pathFailure, "entry_path", malformed == 1 || malformed == 2 ? ERROR_INSUFFICIENT_BUFFER : ERROR_INVALID_DATA));
    }
    FakeSnapshot canonicalFailure;
    canonicalFailure.entries = {allNames.entries[4]};
    canonicalFailure.canonicalFails = true;
    check("canonical_failure_keeps_incomplete", !collectFake(canonicalFailure).complete &&
        canonicalFailure.closes == 1 && errorIs(canonicalFailure, "canonical_path", ERROR_FILE_NOT_FOUND));
    FakeSnapshot encodingFailure;
    encodingFailure.entries = {allNames.entries[4]};
    encodingFailure.canonicalOverride = std::wstring(1, wchar_t(0xd800));
    check("copied_path_encoding_failure_refused", !collectFake(encodingFailure).complete &&
        encodingFailure.closes == 1 && errorIs(encodingFailure, "path_encoding", ERROR_INVALID_DATA));
    FakeSnapshot canonicalHandoff;
    canonicalHandoff.entries = {allNames.entries[4]};
    canonicalHandoff.canonicalOverride = L"C:\\system\\dxgi.dll";
    const auto redirected = collectFake(canonicalHandoff);
    check("canonical_result_not_copied_spelling_is_observed", redirected.complete &&
        redirected.paths == std::vector<std::wstring>{L"C:\\system\\dxgi.dll"} &&
        canonicalHandoff.canonicalInputs == std::vector<std::wstring>{L"C:\\fixture\\dxgi.dll"} &&
        canonicalHandoff.events[0].find("\"origin\":\"system\"") != std::string::npos);
    FakeSnapshot unwind;
    unwind.entries = {allNames.entries[4]};
    unwind.canonicalThrows = true;
    bool unwound = false;
    try { collectFake(unwind); } catch (int) { unwound = true; }
    check("snapshot_reference_closes_on_exception", unwound && unwind.closes == 1 && unwind.captures == 1);

    FakeSnapshot duplicateOwners;
    duplicateOwners.entries = {allNames.entries[4], entryFor(L"dxgi.dll", L"C:\\system\\dxgi.dll"), allNames.entries[3]};
    const auto capturedOwners = collectFake(duplicateOwners);
    auto dxgiProof = good, runtimeProof = good;
    dxgiProof.path = L"c:\\FIXTURE\\DXGI.DLL";
    runtimeProof.path = L"C:\\fixture\\d3d11.dll";
    check("captured_duplicates_retained_with_owners", capturedOwners.complete && capturedOwners.duplicate &&
        capturedOwners.count == 3 && capturedOwners.paths.size() == 3 && duplicateOwners.closes == 1);
    check("qualified_duplicate_requires_observed_owners", RuntimeEvidenceAllowed(capturedOwners, true, dxgiProof, runtimeProof) &&
        !RuntimeEvidenceAllowed(capturedOwners, false, dxgiProof, runtimeProof));
    FakeSnapshot uniqueOwners;
    uniqueOwners.entries = {allNames.entries[4], allNames.entries[3]};
    const auto capturedUnique = collectFake(uniqueOwners);
    check("legacy_and_qualified_unique_owner_evidence", RuntimeEvidenceAllowed(capturedUnique, true, dxgiProof, runtimeProof) &&
        RuntimeEvidenceAllowed(capturedUnique, false, dxgiProof, runtimeProof));
    for (bool missingDxgi : {true, false}) {
        auto snapshot = capturedOwners;
        snapshot.paths = {missingDxgi ? runtimeProof.path : dxgiProof.path};
        check("missing_either_exact_owner_path_refused", !RuntimeEvidenceAllowed(snapshot, true, dxgiProof, runtimeProof));
    }
    check("empty_proven_owner_path_refused", !RuntimeEvidenceAllowed(capturedOwners, true, good, runtimeProof) &&
        !RuntimeEvidenceAllowed(capturedOwners, true, dxgiProof, good));
    auto wrongOwnerPath = capturedOwners;
    wrongOwnerPath.paths = {L"C:\\system\\dxgi.dll", runtimeProof.path};
    check("same_basename_other_owner_path_refused", !RuntimeEvidenceAllowed(wrongOwnerPath, true, dxgiProof, runtimeProof));
    auto incompleteOwners = capturedOwners;
    incompleteOwners.complete = false;
    check("observed_owners_cannot_override_incomplete", !RuntimeEvidenceAllowed(incompleteOwners, true, dxgiProof, runtimeProof));
    auto badOwner = dxgiProof;
    badOwner.sameModule = false;
    check("observed_path_cannot_override_failed_export_proof", !RuntimeEvidenceAllowed(capturedOwners, true, badOwner, runtimeProof));
    check("no_graphics_after_cpu_checks", graphicsAbsent());
    Record("self_test").Number("total", total).Number("failed", failed)
        .String("architecture", "x64").Boolean("cpu_only", true).Emit();
    return failed == 0 ? 0 : 1;
}

int Usage(bool invalid) {
    Record("usage").Boolean("invalid_arguments", invalid)
        .String("self_test", "D3D10Probe.exe --self-test")
        .String("runtime", "--run --route d3d10|d3d10.1|d3d11 --adapter 0..31 --vendor ID --device ID")
        .String("qualification", "Add BOTH --expect-dxgi-origin system|fixture and --expect-runtime-origin system|fixture")
        .String("notice", "Runtime requires separate host authorization; output contains private loaded paths.")
        .Emit();
    return invalid ? 2 : 0;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        // Bound untrusted CLI input before copying/dispatch; no graphics calls.
        if (argc > 14) return Usage(true);
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
