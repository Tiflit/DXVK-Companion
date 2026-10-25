// QueueFixture.cpp - CPU-only native x64 process lifecycle validation helper.
// Standalone validation fixture for Issue #299; not part of Companion production code.
// No graphics libraries imported or loaded. No helper file writes, registry, or network calls.
// Supervised via control/stop-N.request marker file and finite watchdog.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cwchar>
#include <cstring>

static_assert(sizeof(void*) == 8, "QueueFixture requires 64-bit architecture.");

namespace {

void WriteStdout(const char* msg) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    if (hOut != INVALID_HANDLE_VALUE && hOut != NULL) {
        DWORD len = static_cast<DWORD>(strlen(msg));
        DWORD written = 0;
        WriteFile(hOut, msg, len, &written, NULL);
    }
}

// Exit codes:
// 0: Normal stop on regular-file marker (stdout: STOPPED)
// 2: Invalid / missing / extra command-line arguments
// 3: Invalid executable name / path / layout before READY
// 4: Control directory missing or inaccessible before READY
// 5: Pre-existing stop marker refuses startup
// 6: Directory marker encountered during wait loop
// 7: Unexpected file access or path error during wait loop
// 8: Watchdog expired (25 minutes, stdout: WATCHDOG)

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    // 1. Exact supported invocation: QueueFixture.exe --slot N (N in 1..4)
    if (argc != 3) {
        return 2;
    }
    if (wcscmp(argv[1], L"--slot") != 0) {
        return 2;
    }
    if (wcslen(argv[2]) != 1 || argv[2][0] < L'1' || argv[2][0] > L'4') {
        return 2;
    }
    const int slot = argv[2][0] - L'0';

    // 2. Fixed disposable layout verification: <FIXTURE_ROOT>/target/QueueFixture.exe
    wchar_t exePath[MAX_PATH];
    DWORD len = GetModuleFileNameW(NULL, exePath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        return 3;
    }

    // Normalize forward slashes to backslashes
    for (DWORD i = 0; i < len; ++i) {
        if (exePath[i] == L'/') {
            exePath[i] = L'\\';
        }
    }

    // Reject path traversal
    if (wcsstr(exePath, L"..") != NULL) {
        return 3;
    }

    // Verify executable name is QueueFixture.exe
    wchar_t* lastSlash = wcsrchr(exePath, L'\\');
    if (lastSlash == NULL) {
        return 3;
    }
    if (_wcsicmp(lastSlash + 1, L"QueueFixture.exe") != 0) {
        return 3;
    }

    // Verify parent directory is target
    *lastSlash = L'\0';
    wchar_t* secondLastSlash = wcsrchr(exePath, L'\\');
    if (secondLastSlash == NULL) {
        return 3;
    }
    if (_wcsicmp(secondLastSlash + 1, L"target") != 0) {
        return 3;
    }

    // exePath is now <FIXTURE_ROOT>
    *secondLastSlash = L'\0';
    const wchar_t* fixtureRoot = exePath;
    if (wcslen(fixtureRoot) == 0) {
        return 3;
    }

    // 3. Verify control directory exists (<FIXTURE_ROOT>/control)
    // Supervisor creates the control directory, never the helper.
    wchar_t controlDir[MAX_PATH];
    int dirWritten = swprintf_s(controlDir, MAX_PATH, L"%s\\control", fixtureRoot);
    if (dirWritten <= 0 || dirWritten >= MAX_PATH) {
        return 3;
    }
    DWORD dirAttr = GetFileAttributesW(controlDir);
    if (dirAttr == INVALID_FILE_ATTRIBUTES || !(dirAttr & FILE_ATTRIBUTE_DIRECTORY)) {
        return 4;
    }

    // Construct control file path: <FIXTURE_ROOT>/control/stop-N.request
    wchar_t controlPath[MAX_PATH];
    int fileWritten = swprintf_s(controlPath, MAX_PATH, L"%s\\control\\stop-%d.request", fixtureRoot, slot);
    if (fileWritten <= 0 || fileWritten >= MAX_PATH) {
        return 3;
    }

    // 4. Pre-existing stop marker check: refuses startup with distinct nonzero result
    DWORD markerAttr = GetFileAttributesW(controlPath);
    if (markerAttr != INVALID_FILE_ATTRIBUTES) {
        return 5;
    }
    DWORD err = GetLastError();
    if (err != ERROR_FILE_NOT_FOUND) {
        return 4;
    }

    // 5. Signal readiness
    WriteStdout("READY\r\n");

    // 6. Polling loop with 250ms intervals and 25-minute watchdog
    const ULONGLONG startTick = GetTickCount64();
    const ULONGLONG watchdogMs = 25ULL * 60ULL * 1000ULL; // 25 minutes = 1,500,000 ms

    while (true) {
        const ULONGLONG currentTick = GetTickCount64();
        if (currentTick - startTick >= watchdogMs) {
            WriteStdout("WATCHDOG\r\n");
            return 8;
        }

        DWORD attr = GetFileAttributesW(controlPath);
        if (attr != INVALID_FILE_ATTRIBUTES) {
            if ((attr & FILE_ATTRIBUTE_DIRECTORY) != 0) {
                // A directory marker yields nonzero
                return 6;
            }
            // Regular-file marker causes stdout STOPPED and exit 0
            WriteStdout("STOPPED\r\n");
            return 0;
        }

        DWORD loopErr = GetLastError();
        if (loopErr != ERROR_FILE_NOT_FOUND) {
            // Unexpected access, missing control directory, or path error yields nonzero
            return 7;
        }

        Sleep(250);
    }
}
