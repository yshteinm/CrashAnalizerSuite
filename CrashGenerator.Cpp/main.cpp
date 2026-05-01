#include <iostream>
#include <string>
#include <windows.h>
#include <dbghelp.h>

#pragma comment(lib, "Dbghelp.lib")

std::wstring GetDumpFilePath()
{
    wchar_t modulePath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, modulePath, MAX_PATH);
    wchar_t* lastSlash = wcsrchr(modulePath, L'\\');
    if (lastSlash != nullptr)
    {
        *(lastSlash + 1) = L'\0';
    }

    SYSTEMTIME st{};
    GetLocalTime(&st);

    wchar_t fileName[128]{};
    swprintf_s(
        fileName,
        L"CrashGenerator_%04u%02u%02u_%02u%02u%02u.dmp",
        st.wYear,
        st.wMonth,
        st.wDay,
        st.wHour,
        st.wMinute,
        st.wSecond);

    return std::wstring(modulePath) + fileName;
}

LONG WINAPI WriteCrashDump(EXCEPTION_POINTERS* exceptionPointers)
{
    const std::wstring dumpPath = GetDumpFilePath();

    HANDLE dumpFile = CreateFileW(
        dumpPath.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (dumpFile != INVALID_HANDLE_VALUE)
    {
        MINIDUMP_EXCEPTION_INFORMATION exceptionInfo{};
        exceptionInfo.ThreadId = GetCurrentThreadId();
        exceptionInfo.ExceptionPointers = exceptionPointers;
        exceptionInfo.ClientPointers = FALSE;

        const BOOL dumpResult = MiniDumpWriteDump(
            GetCurrentProcess(),
            GetCurrentProcessId(),
            dumpFile,
            MiniDumpWithFullMemory,
            &exceptionInfo,
            nullptr,
            nullptr);

        CloseHandle(dumpFile);

        if (dumpResult == TRUE)
        {
            std::wcout << L"Dump generated: " << dumpPath << std::endl;
        }
        else
        {
            std::wcout << L"MiniDumpWriteDump failed. Error: " << GetLastError() << std::endl;
        }
    }
    else
    {
        std::wcout << L"Failed to create dump file. Error: " << GetLastError() << std::endl;
    }

    return EXCEPTION_EXECUTE_HANDLER;
}

void CauseNullPointerCrash()
{
    int* ptr = nullptr;
    *ptr = 42;
}

int main()
{
    SetUnhandledExceptionFilter(WriteCrashDump);

    std::cout << "CrashGenerator.Cpp - Console Application" << std::endl;
    std::cout << "Generating a synthetic crash..." << std::endl;

    __try
    {
        CauseNullPointerCrash();
    }
    __except (WriteCrashDump(GetExceptionInformation()))
    {
        std::cout << "Crash handled after dump generation." << std::endl;
    }

    return 0;
}